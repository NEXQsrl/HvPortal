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

function Register-HvPortalService([string]$ServiceName, [string]$DisplayName, [string]$Description, [string]$Exe) {
    $existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $existing) {
        New-Service -Name $ServiceName -DisplayName $DisplayName -BinaryPathName "`"$Exe`"" -StartupType Automatic -Description $Description | Out-Null
    } else {
        Invoke-Sc @('config', $ServiceName, 'binPath=', "`"$Exe`"")
    }
    # Least privilege: per-service virtual account (not LocalSystem), unrestricted service SID.
    Invoke-Sc @('config', $ServiceName, 'obj=', "NT SERVICE\$ServiceName", 'start=', 'delayed-auto')
    Invoke-Sc @('sidtype', $ServiceName, 'unrestricted')
    Invoke-Sc @('failure', $ServiceName, 'reset=', '86400', 'actions=', 'restart/10000/restart/30000/restart/60000')
    Invoke-Sc @('failureflag', $ServiceName, '1')
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
