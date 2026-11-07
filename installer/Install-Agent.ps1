#Requires -Version 5.1
<#
.SYNOPSIS
  Installs or upgrades the HvPortal agent on a Hyper-V host and, optionally, enrolls it.
.DESCRIPTION
  - Service HvPortalAgent runs as NT SERVICE\HvPortalAgent, added to the local "Hyper-V Administrators"
    group (S-1-5-32-578). It is NOT a member of Administrators and needs no domain account.
  - No inbound firewall rule is created: the agent connects out to the gateway (TLS, port 8444 by default).
  - Enrollment uses a one-time token from the portal and the gateway CA fingerprint (pinning).
    Pass the token through -EnrollmentToken or HVP_ENROLLMENT_TOKEN to keep it out of command history.
  - -Management: management server (no Hyper-V needed). The agent manages the remote hosts assigned to it in the
    portal with the credentials stored there (WMI/DCOM, WinRM, SMB administrative shares). The service account is
    NOT added to "Hyper-V Administrators". Use an enrollment token of a *management agent* (portal: Hosts ->
    Management agents); the role comes from the gateway, the switch only adjusts the local setup.
  - -ServiceAccount <name>: run the service as a dedicated LOCAL user instead of the virtual account. The user is
    created (or, if created earlier by this script, reset) with a random 40-character password nobody knows, never
    expires, gets only "Log on as a service", is denied local, Remote Desktop, network and batch logon, and is added to
    "Hyper-V Administrators" (not Administrators). Same rights as the default account, but a named identity.
    Upgrades without -ServiceAccount keep the account the service already uses.
  - -StorageFolders 'D:\VMs','E:\ISO': the only folders of this host the portal may use (file explorer, folder pickers,
    VM locations, new disks, ISO/template libraries, backup folders). Missing folders are created. Enforced by the agent;
    the portal cannot change it. Without the parameter an upgrade keeps the current list; -StorageFolders '' removes
    the restriction (every local fixed drive, system folders excluded).
  - Updates from the portal: a scheduled task "\HvPortal\HvPortal Agent Update" (SYSTEM) is registered; the agent
    service may only start it. It installs a package downloaded by the agent only when the package's signed catalog is
    signed by the same certificate as the installed agent (unsigned builds are never installed this way).
    -DisableRemoteUpdate removes the task (updates by hand only).
.EXAMPLE
  $env:HVP_ENROLLMENT_TOKEN = '<token>'; .\Install-Agent.ps1 -Source ..\agent -Gateway https://gw01.contoso.local:8444 -CaSha256 <fingerprint>
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Source,
    [string]$InstallPath = "$env:ProgramFiles\HvPortal\Agent",
    [string]$DataPath = "$env:ProgramData\HvPortal\Agent",
    [string]$Gateway,
    [string]$CaSha256,
    [string]$EnrollmentToken = $env:HVP_ENROLLMENT_TOKEN,
    [switch]$ForceReenroll,
    [switch]$Management,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,19}$')][string]$ServiceAccount,
    [string[]]$StorageFolders,
    [switch]$DisableRemoteUpdate
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
Assert-Administrator
$service = 'HvPortalAgent'
$exe = Join-Path $InstallPath 'HvPortal.Agent.exe'
if (-not (Test-Path (Join-Path $Source 'HvPortal.Agent.exe'))) { throw "HvPortal.Agent.exe not found in $Source." }
if (-not $Management -and -not (Get-Service vmms -ErrorAction SilentlyContinue)) { Write-Warning 'The Hyper-V management service (vmms) is not installed on this machine. For a management server use -Management.' }
# VM configuration, creation, checkpoints and host resource views use the Hyper-V PowerShell module (fixed script Ops\HvPortal.Ops.ps1).
# Management agents run that script on the remote hosts (WinRM), so the module is needed there, not here.
if (-not $Management -and -not (Get-Module -ListAvailable -Name Hyper-V)) {
    Write-Warning 'The Hyper-V PowerShell module is not installed: inventory, power and console work, but configuration/creation/checkpoints will fail. Install it with: Install-WindowsFeature Hyper-V-PowerShell'
}
$policy = Get-ExecutionPolicy -Scope MachinePolicy
if ($policy -eq 'AllSigned') {
    Write-Warning 'Group Policy enforces AllSigned for PowerShell: the unsigned operations script will be refused unless this build is signed.'
}
if (-not (Test-Path (Join-Path $Source 'Ops\HvPortal.Ops.ps1'))) { throw "Ops\HvPortal.Ops.ps1 not found in $Source (incomplete package)." }
# Checked before anything changes: a wrong folder must not leave the agent stopped.
$storageList = $null
if ($PSBoundParameters.ContainsKey('StorageFolders')) { $storageList = @(Test-StorageFolders $StorageFolders) }
if ($Management -and -not (Test-Path (Join-Path $Source 'Ops\HvPortal.Remote.ps1'))) { throw "Ops\HvPortal.Remote.ps1 not found in $Source (package older than 0.4.0)." }

$virtual = "NT SERVICE\$service"
$previousLogon = $null
if (Get-Service $service -ErrorAction SilentlyContinue) {
    $previousLogon = Get-ServiceStartName $service
    Stop-HvPortalService -ServiceName $service
}
# Upgrade of a service already running as a dedicated local account: keep it (its password is not known here).
$keepLogon = (-not $ServiceAccount) -and $previousLogon -and $previousLogon -notlike 'NT SERVICE\*' -and $previousLogon -ne 'LocalSystem'
$backup = Install-ServiceBinaries -Source $Source -InstallPath $InstallPath -ServiceName $service
Register-HvPortalService -ServiceName $service -DisplayName 'HvPortal Agent' -Description 'HvPortal Hyper-V host agent (typed operations only)' -Exe $exe -KeepLogon:$keepLogon

$runAs = $virtual
if ($ServiceAccount) {
    $plain = New-RandomPassword
    $secure = ConvertTo-SecureString $plain -AsPlainText -Force
    $user = Get-LocalUser -Name $ServiceAccount -ErrorAction SilentlyContinue
    if ($user) {
        # Never take over an existing user (e.g. Administrator): only accounts created by this script are reset.
        if ($user.Description -ne $HvPortalServiceAccountMarker) { throw "The local user '$ServiceAccount' already exists and was not created by HvPortal: choose another name." }
        Set-LocalUser -Name $ServiceAccount -Password $secure -PasswordNeverExpires $true -AccountNeverExpires
        Enable-LocalUser -Name $ServiceAccount
    } else {
        New-LocalUser -Name $ServiceAccount -Password $secure -PasswordNeverExpires -UserMayNotChangePassword -AccountNeverExpires `
            -Description $HvPortalServiceAccountMarker | Out-Null
    }
    $sid = (Get-LocalUser -Name $ServiceAccount).SID.Value
    Set-AccountUserRights -Sid $sid -Add $HvPortalServiceAccountRights
    $svc = Get-CimInstance Win32_Service -Filter "Name='$service'"
    # Win32_Service.Change keeps the password off the command line (sc.exe obj= ... password= would expose it).
    $r = Invoke-CimMethod -InputObject $svc -MethodName Change -Arguments @{ StartName = ".\$ServiceAccount"; StartPassword = $plain }
    $plain = $null
    $secure = $null
    if ($r.ReturnValue -ne 0) { throw "Could not set the service account (Win32_Service.Change returned $($r.ReturnValue))." }
    $runAs = "$env:COMPUTERNAME\$ServiceAccount"
    Write-Host "Service account: $runAs (random password, only 'Log on as a service'; local, RDP, network and batch logon denied)."
} elseif ($keepLogon) {
    $runAs = if ($previousLogon.StartsWith('.\')) { "$env:COMPUTERNAME\" + $previousLogon.Substring(2) } else { $previousLogon }
    Write-Host "Keeping the service account $runAs."
}
$accounts = @($virtual)
if ($runAs -ne $virtual) { $accounts += $runAs }
Set-PrivateAcl -Path $InstallPath -ExtraAccounts $accounts -ReadOnlyForExtra
New-Item -ItemType Directory -Force -Path $DataPath | Out-Null
Set-PrivateAcl -Path $DataPath -ExtraAccounts $accounts
Register-EventSource 'HvPortal Agent'
Set-ServiceEnvironment -ServiceName $service -Variables @{ HVP_AGENT_DATA_DIR = $DataPath }

# Allowed storage folders (read by the agent at start; private file in the data directory).
if ($null -ne $storageList) { Set-StorageFolders -DataPath $DataPath -Folders $storageList }
$storageFile = Join-Path $DataPath 'storage.json'
if (Test-Path $storageFile) {
    $current = @((Get-Content $storageFile -Raw | ConvertFrom-Json).folders)
    Write-Host "Storage folders the portal may use: $($current -join ', ')"
} else {
    Write-Host 'Storage folders: not restricted (every local fixed drive, system folders excluded). Restrict with -StorageFolders.'
}

# Updates from the portal (signed builds only): the updater task, that only the agent service account may start.
if ($DisableRemoteUpdate) {
    Unregister-AgentUpdater
    Write-Host 'Updates from the portal disabled (updater task removed).'
} else {
    $sids = @($accounts | ForEach-Object { (New-Object System.Security.Principal.NTAccount $_).Translate([System.Security.Principal.SecurityIdentifier]).Value })
    Register-AgentUpdater -SourceDirectory $PSScriptRoot -ServiceSids $sids
}

# Least privilege for Hyper-V: membership in the built-in "Hyper-V Administrators" group (SID S-1-5-32-578).
# A management agent needs no local Hyper-V rights: it uses the credentials of each remote host.
$hvAdmins = (New-Object System.Security.Principal.SecurityIdentifier 'S-1-5-32-578').Translate([System.Security.Principal.NTAccount]).Value.Split('\')[-1]
$hvMembers = @(Get-LocalGroupMember -SID 'S-1-5-32-578' -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
if ($runAs -ne $virtual -and $hvMembers -contains $virtual) {
    # Dedicated account: the Hyper-V rights belong to it only.
    & net.exe localgroup "$hvAdmins" "$virtual" /delete | Out-Null
}
if (-not $Management) {
    if ($hvMembers -notcontains $runAs) {
        & net.exe localgroup "$hvAdmins" "$runAs" /add 2>$null | Out-Null
        $addCode = $LASTEXITCODE
        # Get-LocalGroupMember fails on groups with orphaned SIDs: "already a member" is fine.
        # (net.exe lists local users without the computer prefix.)
        $listed = @(& net.exe localgroup "$hvAdmins")
        if ($addCode -ne 0 -and -not ($listed -contains $runAs -or $listed -contains $runAs.Split('\')[-1])) { throw "Could not add $runAs to '$hvAdmins'." }
    }
} else {
    Write-Host 'Management agent: remote hosts need WMI/DCOM (TCP 135 + dynamic RPC), WinRM (5985, or 5986 with -UseHttps in the portal) and SMB (445) from this server.'
    Write-Host 'Workgroup hosts over WinRM HTTP: add them to TrustedHosts on this server, e.g. Set-Item WSMan:\localhost\Client\TrustedHosts -Value "hv01,hv02" -Concatenate'
}

if ($Gateway -and $CaSha256 -and $EnrollmentToken) {
    $env:HVP_AGENT_DATA_DIR = $DataPath
    $env:HVP_ENROLLMENT_TOKEN = $EnrollmentToken
    $enrollArgs = @('enroll', '--gateway', $Gateway, '--ca-sha256', $CaSha256)
    if ($ForceReenroll) { $enrollArgs += '--force' }
    # A freshly copied unsigned executable can be briefly locked by antivirus scanning ("Access is denied"):
    # retry the launch a few times. Enrollment itself is never retried after it has run (the token is single use).
    $code = $null
    for ($attempt = 1; $attempt -le 5 -and $null -eq $code; $attempt++) {
        try {
            & $exe @enrollArgs
            $code = $LASTEXITCODE
        } catch [System.Management.Automation.ApplicationFailedException] {
            Write-Warning "Could not start the agent (attempt $attempt/5): $($_.Exception.Message). Retrying in 5 s..."
            Start-Sleep -Seconds 5
        }
    }
    if ($null -eq $code) { throw 'The agent executable could not be started. Check antivirus / application control policies.' }
    Remove-Item Env:\HVP_ENROLLMENT_TOKEN -ErrorAction SilentlyContinue
    if ($code -ne 0) { throw 'Enrollment failed (see message above). The service was installed but not started.' }
    Set-PrivateAcl -Path $DataPath -ExtraAccounts $accounts
} elseif (-not (Test-Path (Join-Path $DataPath 'agent.json'))) {
    Write-Warning 'Agent installed but not enrolled. Re-run with -Gateway, -CaSha256 and the one-time token.'
    return
}

try {
    Wait-ServiceRunning $service
} catch {
    if ($backup) { Restore-HvPortalBackup -ServiceName $service -InstallPath $InstallPath -Backup $backup }
    throw
}
& $exe status
Write-Host "Diagnostics: & '$exe' inventory   (runs as you, not as the service)"
