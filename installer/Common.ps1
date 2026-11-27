#Requires -Version 5.1
# Shared helpers for HvPortal installers. Uses only built-in Windows tooling (WMI, registry, NetSecurity, ACL APIs,
# LocalAccounts cmdlets) and, where nothing else exists, the Windows tools sc.exe, net.exe and secedit.exe.
# 0.9.6: on some servers a security product refuses programs started by a script ("Access is denied" when PowerShell
# starts sc.exe, although the session is elevated and the same command works when typed). So everything an upgrade
# needs is done in process; a Windows tool is started only when a setting really has to change and nothing else can.

function Assert-Administrator {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not ([Security.Principal.WindowsPrincipal]$id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this script from an elevated PowerShell (Run as administrator).'
    }
}

function Get-SystemTool([string]$Name) {
    # Windows tools by full path: an elevated installer never runs a program found through PATH.
    return Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::System)) $Name
}

function Invoke-SystemTool([string]$Name, [string[]]$Arguments) {
    # Runs a Windows tool and tells apart "it ran and returned an exit code" from "Windows did not start it".
    # The second case is what PowerShell reports as "Program 'sc.exe' failed to run: Access is denied"
    # (ApplicationFailedException): the tool never ran, so it is not an answer of the tool and not a missing right.
    $result = [pscustomobject]@{ Tool = $Name; Started = $false; ExitCode = $null; Lines = [string[]]@(); Output = ''; Reason = '' }
    $tool = Get-SystemTool $Name
    $previous = $ErrorActionPreference
    # Not Stop: a line written to stderr would otherwise end the script before the exit code is read.
    $ErrorActionPreference = 'Continue'
    try {
        $out = & $tool @Arguments 2>&1
        $result.ExitCode = $LASTEXITCODE
        $result.Started = $true
        $result.Lines = [string[]]@(@($out) | ForEach-Object { "$_" })
        $result.Output = (($result.Lines | ForEach-Object { $_.Trim() } | Where-Object { $_ }) -join ' ')
    } catch [System.Management.Automation.ApplicationFailedException], [System.Management.Automation.CommandNotFoundException] {
        # The Windows message alone ("Access is denied"): the PowerShell text also carries the script position.
        $inner = $_.Exception.InnerException
        $result.Reason = if ($inner -and $inner.Message) { $inner.Message.Trim() } else { ("$($_.Exception.Message)" -split "`r?`n")[0].Trim() }
    } finally {
        $ErrorActionPreference = $previous
    }
    return $result
}

function Get-ToolNotStartedMessage($Result) {
    return "Windows did not start $($Result.Tool) ($($Result.Reason)). The tool is part of Windows and this session is elevated: " +
        "this normally means that an antivirus / EDR or application control rule refuses programs started by a script. " +
        "It is not caused by the parameters of the setup."
}

function Format-ToolCommand([string]$Name, [string[]]$Arguments) {
    # The command as it can be typed in an elevated prompt.
    return (@($Name) + @($Arguments | ForEach-Object { if ($_ -match '\s' -and $_ -notmatch '^".*"$') { "`"$_`"" } else { $_ } })) -join ' '
}

function Invoke-Sc([string[]]$Arguments) {
    $r = Invoke-SystemTool 'sc.exe' $Arguments
    if (-not $r.Started) { throw "$(Get-ToolNotStartedMessage $r) Command: $(Format-ToolCommand 'sc.exe' $Arguments)" }
    if ($r.ExitCode -ne 0) { throw "sc.exe $($Arguments -join ' ') failed ($($r.ExitCode)): $($r.Output)" }
}

function Set-PrivateAcl([string]$Path, [string[]]$ExtraAccounts, [switch]$ReadOnlyForExtra) {
    # SYSTEM + Administrators full control; service virtual account full (data) or read/execute (binaries).
    $acl = New-Object System.Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    $inherit = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
    $none = [System.Security.AccessControl.PropagationFlags]::None
    foreach ($sid in 'S-1-5-18', 'S-1-5-32-544') {
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
            (New-Object System.Security.Principal.SecurityIdentifier $sid), 'FullControl', $inherit, $none, 'Allow')))
    }
    foreach ($account in $ExtraAccounts) {
        $rights = if ($ReadOnlyForExtra) { 'ReadAndExecute' } else { 'FullControl' }
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($account, $rights, $inherit, $none, 'Allow')))
    }
    if ($ReadOnlyForExtra) {
        # Program files: authenticated users may read/execute (no secrets live here).
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
            (New-Object System.Security.Principal.SecurityIdentifier 'S-1-5-11'), 'ReadAndExecute', $inherit, $none, 'Allow')))
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}

function Stop-HvPortalService([string]$ServiceName, [int]$Seconds = 120) {
    # Stop-Service returns as soon as the service controller reports "Stopped", but the process can still be running
    # its last shutdown steps and keep its files open (e.g. clrjit.dll): replacing the binaries at that moment fails
    # with "Access denied". Wait for the process itself to end (and end it if it hangs) before touching the files.
    $svc = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
    if (-not $svc) { return }
    $processId = [int]$svc.ProcessId
    $exeName = if ($svc.PathName) { [IO.Path]::GetFileNameWithoutExtension(($svc.PathName -replace '"', '').Trim()) } else { $null }
    if ($svc.State -ne 'Stopped') {
        Write-Host "Stopping $ServiceName..."
        Stop-Service -Name $ServiceName -Force -WarningAction SilentlyContinue
    }
    if ($processId -le 0) { return }
    $proc = Get-Process -Id $processId -ErrorAction SilentlyContinue
    # Never wait for (or end) a different program that reused the process id.
    if (-not $proc -or ($exeName -and $proc.ProcessName -ne $exeName)) { return }
    if (-not $proc.WaitForExit($Seconds * 1000)) {
        Write-Warning "$ServiceName (PID $processId) did not exit within $Seconds s after stopping: ending it."
        Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
        $proc.WaitForExit(15000) | Out-Null
    }
}

function Clear-DirectoryContent([string]$Path, [int]$Attempts = 10) {
    # Files of a process that has just exited can stay locked for a few seconds (antivirus scan, image unmapping):
    # retry instead of failing the first time.
    for ($attempt = 1; ; $attempt++) {
        try {
            Get-ChildItem -LiteralPath $Path -Force | Remove-Item -Recurse -Force -ErrorAction Stop
            return
        } catch {
            if ($attempt -ge $Attempts) { throw }
            Write-Warning "Files in $Path are still in use (attempt $attempt/$Attempts): $($_.Exception.Message) Retrying in 3 s..."
            Start-Sleep -Seconds 3
        }
    }
}

function Install-ServiceBinaries([string]$Source, [string]$InstallPath, [string]$ServiceName) {
    $backup = $null
    if (Test-Path -LiteralPath $InstallPath) {
        $backup = "$InstallPath.bak-$(Get-Date -Format yyyyMMddHHmmss)"
        try {
            Copy-Item -LiteralPath $InstallPath -Destination $backup -Recurse -ErrorAction Stop
        } catch {
            # Nothing has been replaced yet: start the current version again instead of leaving the service stopped.
            $err = $_
            Start-Service -Name $ServiceName -ErrorAction SilentlyContinue
            throw "Upgrade not applied: the current files could not be backed up to ${backup}: $($err.Exception.Message) The current version is unchanged."
        }
        Write-Host "Previous version backed up to $backup (rollback: Restore-HvPortalBackup)."
        try {
            Clear-DirectoryContent -Path $InstallPath
            Copy-Item -Path (Join-Path $Source '*') -Destination $InstallPath -Recurse -Force -ErrorAction Stop
        } catch {
            # Never leave a half-replaced installation behind: put the previous version back and start it again.
            $err = $_
            Write-Warning "Could not replace the files in ${InstallPath}: $($err.Exception.Message) Restoring the previous version."
            $restored = $false
            try {
                Clear-DirectoryContent -Path $InstallPath
                Copy-Item -Path (Join-Path $backup '*') -Destination $InstallPath -Recurse -Force -ErrorAction Stop
                $restored = $true
                Start-Service -Name $ServiceName -ErrorAction SilentlyContinue
            } catch {
                Write-Warning "Automatic restore failed: $($_.Exception.Message)"
            }
            if ($restored) {
                throw "Upgrade not applied: $($err.Exception.Message) The previous version was put back (copy in $backup)."
            }
            throw "Upgrade not applied and the previous version could NOT be put back: $($err.Exception.Message) Restore it manually from $backup (Restore-HvPortalBackup), then start $ServiceName."
        }
        return $backup
    }
    New-Item -ItemType Directory -Force -Path $InstallPath | Out-Null
    Copy-Item -Path (Join-Path $Source '*') -Destination $InstallPath -Recurse -Force
    return $backup
}

function Set-ServiceEnvironment([string]$ServiceName, [hashtable]$Variables) {
    # Service-scoped environment (registry "Environment" REG_MULTI_SZ), read by the service process only.
    $key = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
    $values = $Variables.GetEnumerator() | Sort-Object Key | ForEach-Object { "$($_.Key)=$($_.Value)" }
    New-ItemProperty -Path $key -Name Environment -PropertyType MultiString -Value ([string[]]$values) -Force | Out-Null
}

# Restart after 10 s, 30 s and 60 s when the service process ends unexpectedly; the count starts again after a day.
$script:HvPortalFailureResetSeconds = 86400
$script:HvPortalFailureActions = @('restart/10000', 'restart/30000', 'restart/60000')

function ConvertFrom-ServiceFailureActions([byte[]]$Bytes) {
    # The registry value FailureActions is a SERVICE_FAILURE_ACTIONS structure: reset period in seconds, two unused
    # pointers, the number of actions, the offset of the first one, then a (type, delay in ms) pair per action.
    # Returned in the notation of "sc.exe failure" (restart/10000); $null when the value is missing or not readable.
    if (-not $Bytes -or $Bytes.Length -lt 20) { return $null }
    $count = [BitConverter]::ToInt32($Bytes, 12)
    $offset = [BitConverter]::ToInt32($Bytes, 16)
    if ($count -lt 0 -or $count -gt 16 -or $offset -lt 20 -or $Bytes.Length -lt ($offset + 8 * $count)) { return $null }
    $names = @{ 0 = 'none'; 1 = 'restart'; 2 = 'reboot'; 3 = 'run' }
    $actions = New-Object System.Collections.Generic.List[string]
    for ($i = 0; $i -lt $count; $i++) {
        $type = [BitConverter]::ToInt32($Bytes, $offset + 8 * $i)
        $delay = [BitConverter]::ToUInt32($Bytes, $offset + 8 * $i + 4)
        $actions.Add("$(if ($names.ContainsKey($type)) { $names[$type] } else { "type$type" })/$delay")
    }
    return [pscustomobject]@{ ResetSeconds = [BitConverter]::ToUInt32($Bytes, 0); Actions = [string[]]$actions.ToArray() }
}

function Get-HvPortalServiceState([string]$ServiceName) {
    # What the Service Control Manager has stored for the service, read from its registry key: no program is started.
    # $null when the service does not exist.
    $key = Get-ItemProperty -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" -ErrorAction SilentlyContinue
    if (-not $key) { return $null }
    $names = @($key.PSObject.Properties | ForEach-Object { $_.Name })
    $failure = if ($names -contains 'FailureActions') { ConvertFrom-ServiceFailureActions ([byte[]]$key.FailureActions) } else { $null }
    return [pscustomobject]@{
        Path                = if ($names -contains 'ImagePath') { [string]$key.ImagePath } else { '' }
        Account             = if ($names -contains 'ObjectName') { [string]$key.ObjectName } else { '' }
        Start               = if ($names -contains 'Start') { [int]$key.Start } else { -1 }
        DelayedStart        = ($names -contains 'DelayedAutostart') -and ([int]$key.DelayedAutostart -eq 1)
        SidType             = if ($names -contains 'ServiceSidType') { [int]$key.ServiceSidType } else { 0 }
        FailureResetSeconds = if ($failure) { [long]$failure.ResetSeconds } else { -1 }
        FailureActions      = if ($failure) { [string[]]$failure.Actions } else { [string[]]@() }
        FailureFlag         = ($names -contains 'FailureActionsOnNonCrashFailures') -and ([int]$key.FailureActionsOnNonCrashFailures -eq 1)
    }
}

function Get-ServiceRegistrationGaps($State, [string]$Exe, [string]$Account) {
    # Names of the settings that are not as the installer wants them. Empty for a service registered by an earlier
    # installer, which is every upgrade (also from the portal): then nothing is changed and no Windows tool is started.
    # $Account empty = the account is not looked at (dedicated local account, see Register-HvPortalService -KeepLogon).
    $gaps = New-Object System.Collections.Generic.List[string]
    if (-not $State) { return , [string[]]@('Path', 'Account', 'AutomaticStart', 'DelayedStart', 'SidType', 'FailureActions', 'FailureFlag') }
    if (("$($State.Path)" -replace '"', '').Trim() -ne $Exe) { $gaps.Add('Path') }
    if ($Account -and "$($State.Account)" -ne $Account) { $gaps.Add('Account') }
    if ($State.Start -ne 2) { $gaps.Add('AutomaticStart') }
    if (-not $State.DelayedStart) { $gaps.Add('DelayedStart') }
    if ($State.SidType -ne 1) { $gaps.Add('SidType') }
    if ($State.FailureResetSeconds -ne $HvPortalFailureResetSeconds -or (@($State.FailureActions) -join ',') -ne ($HvPortalFailureActions -join ',')) { $gaps.Add('FailureActions') }
    if (-not $State.FailureFlag) { $gaps.Add('FailureFlag') }
    return , [string[]]$gaps.ToArray()
}

function Set-ServiceRequiredSettings([string]$ServiceName, [string]$Exe, [string]$Account, [string[]]$Gaps) {
    # Program, account and automatic start: without them the service is not the one the installer promises, so a
    # failure here is an error. In process through WMI (Win32_Service.Change, as for a dedicated account); sc.exe only
    # when WMI refuses. The outcome is read back by the caller.
    $change = @{}
    $sc = @('config', $ServiceName)
    if ($Gaps -contains 'Path') { $change.PathName = "`"$Exe`""; $sc += @('binPath=', "`"$Exe`"") }
    # A virtual account has no password: none is sent (as "sc.exe config obj=" does).
    if ($Gaps -contains 'Account') { $change.StartName = $Account; $sc += @('obj=', $Account) }
    if ($Gaps -contains 'AutomaticStart') { $change.StartMode = 'Automatic'; $sc += @('start=', 'auto') }
    $wmi = $null
    try {
        $svc = Get-CimInstance -ClassName Win32_Service -Filter "Name='$ServiceName'" -ErrorAction Stop
        if (-not $svc) { throw "service $ServiceName not found" }
        $r = Invoke-CimMethod -InputObject $svc -MethodName Change -Arguments $change -ErrorAction Stop
        if ($r.ReturnValue -ne 0 -and $change.ContainsKey('StartName')) {
            # Some systems want the empty password of the account spelled out.
            $change.StartPassword = ''
            $r = Invoke-CimMethod -InputObject $svc -MethodName Change -Arguments $change -ErrorAction Stop
        }
        if ($r.ReturnValue -eq 0) { return }
        $wmi = "Win32_Service.Change returned $($r.ReturnValue)"
    } catch {
        $wmi = $_.Exception.Message
    }
    try {
        Invoke-Sc $sc
    } catch {
        throw "The service $ServiceName could not be configured ($($Gaps -join ', ')). WMI: $wmi. sc.exe: $($_.Exception.Message)"
    }
}

function Set-ServiceRecommendedSettings([string]$ServiceName, [string[]]$Gaps) {
    # Delayed start, service SID type and restart after a failure can only be set with sc.exe. The service works
    # without them (it runs as its own account, which already is its service SID), so when sc.exe cannot be used they
    # are reported with the commands to type, and the installation goes on. Returns the commands not applied.
    $steps = New-Object System.Collections.Generic.List[object]
    if ($Gaps -contains 'DelayedStart') { $steps.Add(@('config', $ServiceName, 'start=', 'delayed-auto')) }
    if ($Gaps -contains 'SidType') { $steps.Add(@('sidtype', $ServiceName, 'unrestricted')) }
    if ($Gaps -contains 'FailureActions') { $steps.Add(@('failure', $ServiceName, 'reset=', "$HvPortalFailureResetSeconds", 'actions=', ($HvPortalFailureActions -join '/'))) }
    if ($Gaps -contains 'FailureFlag') { $steps.Add(@('failureflag', $ServiceName, '1')) }
    $pending = New-Object System.Collections.Generic.List[string]
    $notStarted = $null
    foreach ($step in $steps) {
        $command = Format-ToolCommand 'sc.exe' $step
        # Refused once: not tried again for the other settings (every attempt is one more alert of the security product).
        if ($notStarted) { $pending.Add($command); continue }
        $r = Invoke-SystemTool 'sc.exe' $step
        if (-not $r.Started) { $notStarted = $r; $pending.Add($command); continue }
        if ($r.ExitCode -ne 0) {
            Write-Warning "$command failed ($($r.ExitCode)): $($r.Output)"
            $pending.Add($command)
        }
    }
    if ($pending.Count -gt 0) {
        $why = if ($notStarted) { "$(Get-ToolNotStartedMessage $notStarted) " } else { '' }
        Write-Warning ("${why}Service ${ServiceName}: $($pending.Count) recommended setting(s) not applied (start delayed after boot, service SID, " +
            "restart after a failure). The service works without them. To apply them, type in an elevated prompt:`n  " + ($pending -join "`n  "))
    }
    return , [string[]]$pending.ToArray()
}

function New-HvPortalServiceEntry([string]$ServiceName, [string]$DisplayName, [string]$Description, [string]$Exe) {
    # Created with its virtual account from the start (no password), so that the service does not exist as LocalSystem
    # even for a moment and no further step is needed for the account. If Windows refuses that, it is created as
    # before 0.9.6 and the account is set right after (Set-ServiceRequiredSettings).
    $service = @{ Name = $ServiceName; DisplayName = $DisplayName; BinaryPathName = "`"$Exe`""; StartupType = 'Automatic'; Description = $Description }
    try {
        $credential = New-Object System.Management.Automation.PSCredential("NT SERVICE\$ServiceName", (New-Object System.Security.SecureString))
        New-Service @service -Credential $credential -ErrorAction Stop | Out-Null
    } catch {
        if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) { return }
        New-Service @service -ErrorAction Stop | Out-Null
    }
}

function Register-HvPortalService([string]$ServiceName, [string]$DisplayName, [string]$Description, [string]$Exe, [switch]$KeepLogon) {
    # Least privilege: per-service virtual account (not LocalSystem), unrestricted service SID.
    # -KeepLogon: the account is left as it is (a service that runs as a dedicated local account, or about to get one:
    # Install-Agent -ServiceAccount sets it right after).
    if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
        New-HvPortalServiceEntry -ServiceName $ServiceName -DisplayName $DisplayName -Description $Description -Exe $Exe
    }
    $account = if ($KeepLogon) { '' } else { "NT SERVICE\$ServiceName" }
    $required = @('Path', 'Account', 'AutomaticStart')
    $gaps = Get-ServiceRegistrationGaps (Get-HvPortalServiceState $ServiceName) $Exe $account
    if ($gaps.Count -eq 0) {
        Write-Host "Service $ServiceName is already registered as required: nothing to change."
        return
    }
    $needed = [string[]]@($gaps | Where-Object { $required -contains $_ })
    if ($needed.Count -gt 0) {
        Set-ServiceRequiredSettings -ServiceName $ServiceName -Exe $Exe -Account $account -Gaps $needed
        # Never taken on trust: what the service has now is read again.
        $left = [string[]]@((Get-ServiceRegistrationGaps (Get-HvPortalServiceState $ServiceName) $Exe $account) | Where-Object { $required -contains $_ })
        if ($left.Count -gt 0) { throw "The service $ServiceName is still not registered as required after the change: $($left -join ', ')." }
    }
    $recommended = [string[]]@($gaps | Where-Object { $required -notcontains $_ })
    if ($recommended.Count -gt 0) { Set-ServiceRecommendedSettings -ServiceName $ServiceName -Gaps $recommended | Out-Null }
}

function Remove-HvPortalService([string]$ServiceName) {
    # In process first (Win32_Service.Delete); sc.exe only if WMI refuses.
    try {
        $svc = Get-CimInstance -ClassName Win32_Service -Filter "Name='$ServiceName'" -ErrorAction Stop
        if ($svc) {
            $r = Invoke-CimMethod -InputObject $svc -MethodName Delete -ErrorAction Stop
            # 16 = already marked for deletion.
            if ($r.ReturnValue -eq 0 -or $r.ReturnValue -eq 16) { return }
        }
    } catch {
        Write-Verbose "Win32_Service.Delete: $($_.Exception.Message)"
    }
    Invoke-Sc @('delete', $ServiceName)
}

function Get-LocalGroupName([string]$GroupSid) {
    # The local (translated) name of a built-in group, e.g. "Hyper-V Administrators" / "Administrateurs Hyper-V".
    return (New-Object System.Security.Principal.SecurityIdentifier $GroupSid).Translate([System.Security.Principal.NTAccount]).Value.Split('\')[-1]
}

function Add-AccountToLocalGroup([string]$GroupSid, [string]$Account) {
    # In process first (LocalAccounts cmdlets); net.exe only when the cmdlet fails for another reason than "already a
    # member" (on some systems it cannot work on groups that hold orphaned SIDs). Throws when the account is not added.
    $cmdlet = $null
    try {
        Add-LocalGroupMember -SID $GroupSid -Member $Account -ErrorAction Stop
        return
    } catch {
        if ("$($_.FullyQualifiedErrorId)" -like 'MemberExists*' -or $_.Exception.GetType().Name -eq 'MemberExistsException') { return }
        $cmdlet = $_.Exception.Message
    }
    $group = Get-LocalGroupName $GroupSid
    $add = Invoke-SystemTool 'net.exe' @('localgroup', $group, $Account, '/add')
    if ($add.Started -and $add.ExitCode -eq 0) { return }
    if ($add.Started) {
        # "Already a member" is fine; net.exe lists local users without the computer prefix.
        $listed = @((Invoke-SystemTool 'net.exe' @('localgroup', $group)).Lines | ForEach-Object { $_.Trim() })
        if ($listed -contains $Account -or $listed -contains $Account.Split('\')[-1]) { return }
        throw "Could not add $Account to '$group'. Add-LocalGroupMember: $cmdlet. net.exe: $($add.Output)"
    }
    throw "Could not add $Account to '$group'. Add-LocalGroupMember: $cmdlet. $(Get-ToolNotStartedMessage $add)"
}

function Remove-AccountFromLocalGroup([string]$GroupSid, [string]$Account) {
    # Clean-up, never fatal: a membership that cannot be removed is reported with the command to type.
    try {
        Remove-LocalGroupMember -SID $GroupSid -Member $Account -ErrorAction Stop
        return
    } catch {
        if ("$($_.FullyQualifiedErrorId)" -like 'MemberNotFound*' -or $_.Exception.GetType().Name -eq 'MemberNotFoundException') { return }
        # No such group on this machine: nothing to remove.
        if ("$($_.FullyQualifiedErrorId)" -like 'GroupNotFound*' -or $_.Exception.GetType().Name -eq 'GroupNotFoundException') { return }
    }
    try {
        $group = Get-LocalGroupName $GroupSid
        $r = Invoke-SystemTool 'net.exe' @('localgroup', $group, $Account, '/delete')
        if (-not $r.Started) {
            Write-Warning "$Account could not be removed from '$group'. $(Get-ToolNotStartedMessage $r) By hand: $(Format-ToolCommand 'net.exe' @('localgroup', $group, $Account, '/delete'))"
        }
    } catch {
        Write-Warning "$Account could not be removed from the local group ${GroupSid}: $($_.Exception.Message)"
    }
}

function Undo-HvPortalUpgrade([string]$ServiceName, [string]$InstallPath, [string]$Backup, [bool]$Start = $true) {
    # A step failed after the files of an upgrade were replaced: the previous version is put back and started again,
    # so that a failed upgrade never leaves a working service stopped. Does nothing on a first installation (no
    # backup). Never throws: the caller reports its own error.
    if (-not $Backup) { return }
    Write-Warning "The upgrade failed after the files were replaced: putting the previous version back from $Backup."
    try {
        # Looked at before anything is deleted: an empty or missing copy must not empty the installation folder.
        if (-not (Test-Path -LiteralPath $Backup) -or -not (Get-ChildItem -LiteralPath $Backup -Force | Select-Object -First 1)) {
            throw "the copy in $Backup is missing or empty."
        }
        Stop-HvPortalService -ServiceName $ServiceName
        Clear-DirectoryContent -Path $InstallPath
        Copy-Item -Path (Join-Path $Backup '*') -Destination $InstallPath -Recurse -Force -ErrorAction Stop
    } catch {
        Write-Warning "The previous version could NOT be put back: $($_.Exception.Message) Restore it by hand from $Backup (Restore-HvPortalBackup), then start $ServiceName."
        return
    }
    if (-not $Start) {
        Write-Host "The previous version was put back in $InstallPath ($ServiceName is not started)."
        return
    }
    try {
        Start-Service -Name $ServiceName -ErrorAction Stop
        Write-Host "The previous version was put back and $ServiceName runs again."
    } catch {
        Write-Warning "The previous version was put back, but $ServiceName did not start: $($_.Exception.Message)"
    }
}

function Get-ServiceStartName([string]$ServiceName) {
    $svc = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
    if ($svc) { return [string]$svc.StartName }
    return $null
}

# Description that marks local accounts created by Install-Agent -ServiceAccount (never reuse or delete other users).
$script:HvPortalServiceAccountMarker = 'HvPortal agent service account (no interactive logon)'

# Logon rights of a dedicated service account: only "log on as a service"; every other kind of logon denied.
$script:HvPortalServiceAccountRights = @('SeServiceLogonRight', 'SeDenyInteractiveLogonRight', 'SeDenyRemoteInteractiveLogonRight', 'SeDenyNetworkLogonRight', 'SeDenyBatchLogonRight')

# Right needed to attach a backup disk to the host for file restore (Install-Agent -FileRestore); never granted by default.
$script:HvPortalFileRestoreRight = @('SeManageVolumePrivilege')

function Edit-UserRightsInf([string[]]$Lines, [string]$Sid, [string[]]$Add = @(), [string[]]$Remove = @()) {
    # Pure text transformation of a "secedit /export /areas USER_RIGHTS" file: adds/removes *SID in the given rights.
    # A right left without members is written with an empty value so that secedit clears it.
    $entry = "*$Sid"
    $out = New-Object System.Collections.Generic.List[string]
    $done = @{}
    $inRights = $false
    $hasSection = $false
    $flush = {
        foreach ($r in $Add) { if (-not $done.ContainsKey($r)) { $out.Add("$r = $entry"); $done[$r] = $true } }
    }
    foreach ($line in $Lines) {
        if ($line -match '^\s*\[(.+)\]\s*$') {
            if ($inRights) { & $flush }
            $inRights = $Matches[1] -eq 'Privilege Rights'
            if ($inRights) { $hasSection = $true }
            $out.Add($line)
            continue
        }
        if ($inRights -and $line -match '^\s*(\w+)\s*=\s*(.*)$' -and ($Add -contains $Matches[1] -or $Remove -contains $Matches[1])) {
            $right = $Matches[1]
            $members = @($Matches[2].Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ -and $_ -ne $entry })
            if ($Add -contains $right) { $members += $entry; $done[$right] = $true }
            $out.Add("$right = $($members -join ',')".TrimEnd())
            continue
        }
        $out.Add($line)
    }
    if ($inRights) { & $flush }
    if (-not $hasSection -and $Add.Count -gt 0) { $out.Add('[Privilege Rights]'); & $flush }
    return , $out.ToArray()
}

function Set-AccountUserRights([string]$Sid, [string[]]$Add = @(), [string[]]$Remove = @()) {
    # Local security policy through the built-in secedit.exe (no P/Invoke); verified by exporting it again.
    $dir = Join-Path ([IO.Path]::GetTempPath()) ('hvp-secedit-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $dir | Out-Null
    try {
        $inf = Join-Path $dir 'rights.inf'
        $secedit = Get-SystemTool 'secedit.exe'
        & $secedit /export /cfg $inf /areas USER_RIGHTS /quiet | Out-Null
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path $inf)) { throw "secedit /export failed ($LASTEXITCODE)." }
        $lines = Edit-UserRightsInf -Lines (Get-Content -LiteralPath $inf) -Sid $Sid -Add $Add -Remove $Remove
        Set-Content -LiteralPath $inf -Value $lines -Encoding Unicode
        & $secedit /configure /db (Join-Path $dir 'rights.sdb') /cfg $inf /areas USER_RIGHTS /quiet | Out-Null
        $check = Join-Path $dir 'check.inf'
        & $secedit /export /cfg $check /areas USER_RIGHTS /quiet | Out-Null
        $text = Get-Content -LiteralPath $check
        foreach ($r in $Add) {
            if (-not ($text | Where-Object { $_ -match "^\s*$r\s*=.*\*$([regex]::Escape($Sid))(,|\s*$)" })) { throw "User right $r was not granted (secedit). Is a Group Policy overriding the local policy?" }
        }
        foreach ($r in $Remove) {
            if ($text | Where-Object { $_ -match "^\s*$r\s*=.*\*$([regex]::Escape($Sid))(,|\s*$)" }) { Write-Warning "User right $r still lists $Sid." }
        }
    } finally {
        Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function New-RandomPassword([int]$Length = 40) {
    # 64 symbols: uniform from random bytes (256 % 64 = 0). Nobody needs to know it: the service stores it.
    $chars = [char[]]'abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789!#%+-=?_'
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        do {
            $bytes = New-Object byte[] $Length
            $rng.GetBytes($bytes)
            $s = -join ($bytes | ForEach-Object { $chars[$_ % $chars.Length] })
        } until ($s -cmatch '[a-z]' -and $s -cmatch '[A-Z]' -and $s -match '\d' -and $s -match '[^A-Za-z0-9]')
    } finally { $rng.Dispose() }
    return $s
}

function Grant-PrivateKeyRead([System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate, [string]$Account) {
    # Read access to the machine key file of a certificate in LocalMachine\My (CNG or legacy CSP).
    # Returns $false when the key file cannot be located (e.g. key on an HSM): the caller then explains the manual step.
    $key = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($Certificate)
    if (-not $key) { $key = [System.Security.Cryptography.X509Certificates.ECDsaCertificateExtensions]::GetECDsaPrivateKey($Certificate) }
    if (-not $key) { return $false }
    $name = if ($key -is [System.Security.Cryptography.RSACng] -or $key -is [System.Security.Cryptography.ECDsaCng]) { $key.Key.UniqueName }
            elseif ($key -is [System.Security.Cryptography.RSACryptoServiceProvider]) { $key.CspKeyContainerInfo.UniqueKeyContainerName }
            else { $null }
    if (-not $name) { return $false }
    $file = @("$env:ProgramData\Microsoft\Crypto\Keys\$name", "$env:ProgramData\Microsoft\Crypto\RSA\MachineKeys\$name") |
        Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $file) { return $false }
    # Access section only: Set-Acl would also try to rewrite the owner.
    $item = Get-Item -LiteralPath $file
    $acl = $item.GetAccessControl([System.Security.AccessControl.AccessControlSections]::Access)
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($Account, 'Read', 'Allow')))
    $item.SetAccessControl($acl)
    Write-Host "Granted $Account read access to the private key of $($Certificate.Subject)."
    return $true
}

function Register-EventSource([string]$Source) {
    if (-not [System.Diagnostics.EventLog]::SourceExists($Source)) {
        [System.Diagnostics.EventLog]::CreateEventSource($Source, 'Application')
    }
}

# Top-level folders never usable by the portal (same list as HostPaths.BlockedRootFolders in the agent).
$script:HvPortalBlockedRootFolders = @('Windows', 'Users', 'User', 'inetpub', 'VBRCatalog', 'ovhupd', 'Packages', 'PerfLogs', 'Program Files',
    'Program Files (x86)', 'ProgramData', 'Recovery', 'Boot', 'System Volume Information', 'Documents and Settings', 'Config.Msi', 'MSOCache',
    'OneDriveTemp', 'Intel', 'AMD', 'NVIDIA')

function Test-StorageFolders([string[]]$Folders) {
    # Returns the normalized list ('' or nothing = no restriction). Local folders of fixed drives only, no system folders.
    $list = New-Object System.Collections.Generic.List[string]
    foreach ($raw in @($Folders | ForEach-Object { if ($_) { $_.Split(',;') } } | ForEach-Object { $_.Trim() } | Where-Object { $_ })) {
        if ($raw -notmatch '^[A-Za-z]:\\' -or $raw -match '[*?"<>|]' -or $raw.Contains('..') -or $raw.Length -gt 200) {
            throw "Invalid storage folder '$raw': use local folders such as D:\VMs."
        }
        $f = $raw.Substring(0, 1).ToUpperInvariant() + $raw.Substring(1)
        if ($f.Length -gt 3) { $f = $f.TrimEnd('\') }
        $segments = $f.Split('\') | Where-Object { $_ }
        if ($segments.Count -gt 1 -and ($HvPortalBlockedRootFolders -contains $segments[1] -or $segments[1].StartsWith('$'))) {
            throw "Storage folder '$f' is (inside) a system folder."
        }
        $drive = New-Object System.IO.DriveInfo $f.Substring(0, 3)
        if ($drive.DriveType -ne 'Fixed') { throw "Storage folder '$f' is not on a local fixed drive." }
        if ($list -contains $f) { continue }
        $list.Add($f)
    }
    if ($list.Count -gt 16) { throw 'At most 16 storage folders.' }
    return , $list.ToArray()
}

function Set-StorageFolders([string]$DataPath, [string[]]$Folders) {
    $file = Join-Path $DataPath 'storage.json'
    if (-not $Folders -or $Folders.Count -eq 0) {
        if (Test-Path $file) { Remove-Item $file -Force }
        return
    }
    foreach ($f in $Folders) {
        if (-not (Test-Path -LiteralPath $f)) { New-Item -ItemType Directory -Path $f -Force | Out-Null; Write-Host "Created storage folder $f." }
    }
    @{ folders = @($Folders) } | ConvertTo-Json | Set-Content -LiteralPath $file -Encoding UTF8
}

$script:HvPortalUpdaterTask = 'HvPortal Agent Update'
$script:HvPortalGatewayUpdaterTask = 'HvPortal Gateway Update'

function Register-HvPortalUpdater([string]$SourceDirectory, [string[]]$ServiceSids, [string]$TaskName, [string]$ScriptName, [string]$What) {
    # SYSTEM task running a fixed updater script from Program Files; the service account may only run it.
    $script = Join-Path $SourceDirectory $ScriptName
    if (-not (Test-Path $script)) { Write-Warning "$ScriptName not in this package: updates from the portal are not available."; return }
    $dir = Join-Path $env:ProgramFiles 'HvPortal\Updater'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    Copy-Item $script $dir -Force
    Copy-Item (Join-Path $SourceDirectory 'Common.ps1') $dir -Force
    Set-PrivateAcl -Path $dir -ExtraAccounts @() -ReadOnlyForExtra
    $ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $action = New-ScheduledTaskAction -Execute $ps -Argument "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$(Join-Path $dir $ScriptName)`""
    $principal = New-ScheduledTaskPrincipal -UserId 'NT AUTHORITY\SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Hours 1) -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    Register-ScheduledTask -TaskName $TaskName -TaskPath '\HvPortal\' -Action $action -Principal $principal -Settings $settings -Force | Out-Null
    # Read + execute (start) for the service account; full control only for SYSTEM and Administrators.
    $sddl = 'D:P(A;;FA;;;SY)(A;;FA;;;BA)' + (($ServiceSids | ForEach-Object { "(A;;GRGX;;;$_)" }) -join '')
    $scheduler = New-Object -ComObject 'Schedule.Service'
    $scheduler.Connect()
    $scheduler.GetFolder('\HvPortal').GetTask($TaskName).SetSecurityDescriptor($sddl, 0)
    Write-Host "Updater task registered: signed $What updates can be installed from the portal."
}

function Unregister-HvPortalUpdater([string]$TaskName, [string]$ScriptName) {
    Get-ScheduledTask -TaskName $TaskName -TaskPath '\HvPortal\' -ErrorAction SilentlyContinue | Unregister-ScheduledTask -Confirm:$false
    # The agent and the gateway may share the folder (same server): only this updater's script, the folder when empty.
    $dir = Join-Path $env:ProgramFiles 'HvPortal\Updater'
    Remove-Item (Join-Path $dir $ScriptName) -Force -ErrorAction SilentlyContinue
    if ((Test-Path $dir) -and -not (Get-ChildItem $dir -Filter 'Update-HvPortal*.ps1')) { Remove-Item $dir -Recurse -Force }
}

function Register-AgentUpdater([string]$SourceDirectory, [string[]]$ServiceSids) {
    Register-HvPortalUpdater -SourceDirectory $SourceDirectory -ServiceSids $ServiceSids -TaskName $HvPortalUpdaterTask -ScriptName 'Update-HvPortalAgent.ps1' -What 'agent'
}

function Unregister-AgentUpdater { Unregister-HvPortalUpdater -TaskName $HvPortalUpdaterTask -ScriptName 'Update-HvPortalAgent.ps1' }

function Register-GatewayUpdater([string]$SourceDirectory, [string[]]$ServiceSids) {
    Register-HvPortalUpdater -SourceDirectory $SourceDirectory -ServiceSids $ServiceSids -TaskName $HvPortalGatewayUpdaterTask -ScriptName 'Update-HvPortalGateway.ps1' -What 'gateway'
}

function Unregister-GatewayUpdater { Unregister-HvPortalUpdater -TaskName $HvPortalGatewayUpdaterTask -ScriptName 'Update-HvPortalGateway.ps1' }

function Wait-ServiceRunning([string]$ServiceName, [int]$Seconds = 60) {
    $svc = Get-Service -Name $ServiceName
    $svc.Start()
    $svc.WaitForStatus('Running', [TimeSpan]::FromSeconds($Seconds))
    Start-Sleep -Seconds 5
    if ((Get-Service -Name $ServiceName).Status -ne 'Running') { throw "$ServiceName stopped after start. Check the Application event log." }
}

function Restore-HvPortalBackup([string]$ServiceName, [string]$InstallPath, [string]$Backup) {
    Stop-HvPortalService -ServiceName $ServiceName
    Clear-DirectoryContent -Path $InstallPath
    Copy-Item -Path (Join-Path $Backup '*') -Destination $InstallPath -Recurse -Force
    Start-Service -Name $ServiceName
    Write-Host "Restored $InstallPath from $Backup."
}
