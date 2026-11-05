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
    [switch]$ForceReenroll
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
Assert-Administrator
$service = 'HvPortalAgent'
$exe = Join-Path $InstallPath 'HvPortal.Agent.exe'
if (-not (Test-Path (Join-Path $Source 'HvPortal.Agent.exe'))) { throw "HvPortal.Agent.exe not found in $Source." }
if (-not (Get-Service vmms -ErrorAction SilentlyContinue)) { Write-Warning 'The Hyper-V management service (vmms) is not installed on this machine.' }
# VM configuration, creation, checkpoints and host resource views use the Hyper-V PowerShell module (fixed script Ops\HvPortal.Ops.ps1).
if (-not (Get-Module -ListAvailable -Name Hyper-V)) {
    Write-Warning 'The Hyper-V PowerShell module is not installed: inventory, power and console work, but configuration/creation/checkpoints will fail. Install it with: Install-WindowsFeature Hyper-V-PowerShell'
}
$policy = Get-ExecutionPolicy -Scope MachinePolicy
if ($policy -eq 'AllSigned') {
    Write-Warning 'Group Policy enforces AllSigned for PowerShell: the unsigned operations script will be refused unless this build is signed.'
}
if (-not (Test-Path (Join-Path $Source 'Ops\HvPortal.Ops.ps1'))) { throw "Ops\HvPortal.Ops.ps1 not found in $Source (incomplete package)." }

if (Get-Service $service -ErrorAction SilentlyContinue) { Stop-Service $service -Force }
$backup = Install-ServiceBinaries -Source $Source -InstallPath $InstallPath -ServiceName $service
Register-HvPortalService -ServiceName $service -DisplayName 'HvPortal Agent' -Description 'HvPortal Hyper-V host agent (typed operations only)' -Exe $exe
Set-PrivateAcl -Path $InstallPath -ExtraAccounts @("NT SERVICE\$service") -ReadOnlyForExtra
New-Item -ItemType Directory -Force -Path $DataPath | Out-Null
Set-PrivateAcl -Path $DataPath -ExtraAccounts @("NT SERVICE\$service")
Register-EventSource 'HvPortal Agent'
Set-ServiceEnvironment -ServiceName $service -Variables @{ HVP_AGENT_DATA_DIR = $DataPath }

# Least privilege for Hyper-V: membership in the built-in "Hyper-V Administrators" group (SID S-1-5-32-578).
$hvAdmins = (New-Object System.Security.Principal.SecurityIdentifier 'S-1-5-32-578').Translate([System.Security.Principal.NTAccount]).Value.Split('\')[-1]
$member = "NT SERVICE\$service"
$already = Get-LocalGroupMember -SID 'S-1-5-32-578' -ErrorAction SilentlyContinue | Where-Object { $_.Name -eq $member }
if (-not $already) {
    & net.exe localgroup "$hvAdmins" "$member" /add | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not add $member to '$hvAdmins'." }
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
    Set-PrivateAcl -Path $DataPath -ExtraAccounts @("NT SERVICE\$service")
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
