#Requires -Version 5.1
# Shared helpers for HvPortal installers. Uses only built-in Windows tooling (sc.exe, NetSecurity, ACL APIs).

function Assert-Administrator {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not ([Security.Principal.WindowsPrincipal]$id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this script from an elevated PowerShell (Run as administrator).'
    }
}

function Invoke-Sc([string[]]$Arguments) {
    $output = & sc.exe @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "sc.exe $($Arguments -join ' ') failed ($LASTEXITCODE): $output" }
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

function Install-ServiceBinaries([string]$Source, [string]$InstallPath, [string]$ServiceName) {
    $backup = $null
    if (Test-Path -LiteralPath $InstallPath) {
        $backup = "$InstallPath.bak-$(Get-Date -Format yyyyMMddHHmmss)"
        Copy-Item -LiteralPath $InstallPath -Destination $backup -Recurse
        Write-Host "Previous version backed up to $backup (rollback: Restore-HvPortalBackup)."
        Get-ChildItem -LiteralPath $InstallPath -Force | Remove-Item -Recurse -Force
    } else {
        New-Item -ItemType Directory -Force -Path $InstallPath | Out-Null
    }
    Copy-Item -Path (Join-Path $Source '*') -Destination $InstallPath -Recurse -Force
    return $backup
}

function Set-ServiceEnvironment([string]$ServiceName, [hashtable]$Variables) {
    # Service-scoped environment (registry "Environment" REG_MULTI_SZ), read by the service process only.
    $key = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
    $values = $Variables.GetEnumerator() | Sort-Object Key | ForEach-Object { "$($_.Key)=$($_.Value)" }
    New-ItemProperty -Path $key -Name Environment -PropertyType MultiString -Value ([string[]]$values) -Force | Out-Null
}

function Register-HvPortalService([string]$ServiceName, [string]$DisplayName, [string]$Description, [string]$Exe, [switch]$KeepLogon) {
    $existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $existing) {
        New-Service -Name $ServiceName -DisplayName $DisplayName -BinaryPathName "`"$Exe`"" -StartupType Automatic -Description $Description | Out-Null
    } else {
        Invoke-Sc @('config', $ServiceName, 'binPath=', "`"$Exe`"")
    }
    # Least privilege: per-service virtual account (not LocalSystem), unrestricted service SID.
    # -KeepLogon: upgrade of a service that runs as a dedicated local account (Install-Agent -ServiceAccount).
    if ($KeepLogon) { Invoke-Sc @('config', $ServiceName, 'start=', 'delayed-auto') }
    else { Invoke-Sc @('config', $ServiceName, 'obj=', "NT SERVICE\$ServiceName", 'start=', 'delayed-auto') }
    Invoke-Sc @('sidtype', $ServiceName, 'unrestricted')
    Invoke-Sc @('failure', $ServiceName, 'reset=', '86400', 'actions=', 'restart/10000/restart/30000/restart/60000')
    Invoke-Sc @('failureflag', $ServiceName, '1')
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
        & secedit.exe /export /cfg $inf /areas USER_RIGHTS /quiet | Out-Null
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path $inf)) { throw "secedit /export failed ($LASTEXITCODE)." }
        $lines = Edit-UserRightsInf -Lines (Get-Content -LiteralPath $inf) -Sid $Sid -Add $Add -Remove $Remove
        Set-Content -LiteralPath $inf -Value $lines -Encoding Unicode
        & secedit.exe /configure /db (Join-Path $dir 'rights.sdb') /cfg $inf /areas USER_RIGHTS /quiet | Out-Null
        $check = Join-Path $dir 'check.inf'
        & secedit.exe /export /cfg $check /areas USER_RIGHTS /quiet | Out-Null
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

function Wait-ServiceRunning([string]$ServiceName, [int]$Seconds = 60) {
    $svc = Get-Service -Name $ServiceName
    $svc.Start()
    $svc.WaitForStatus('Running', [TimeSpan]::FromSeconds($Seconds))
    Start-Sleep -Seconds 5
    if ((Get-Service -Name $ServiceName).Status -ne 'Running') { throw "$ServiceName stopped after start. Check the Application event log." }
}

function Restore-HvPortalBackup([string]$ServiceName, [string]$InstallPath, [string]$Backup) {
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    Get-ChildItem -LiteralPath $InstallPath -Force | Remove-Item -Recurse -Force
    Copy-Item -Path (Join-Path $Backup '*') -Destination $InstallPath -Recurse -Force
    Start-Service -Name $ServiceName
    Write-Host "Restored $InstallPath from $Backup."
}
