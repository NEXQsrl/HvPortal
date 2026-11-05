#Requires -Version 5.1
<#
.SYNOPSIS
  Installs or upgrades the HvPortal gateway as a Windows service running under a virtual account.
.DESCRIPTION
  - Service HvPortalGateway runs as NT SERVICE\HvPortalGateway (not LocalSystem, not administrator).
  - Binaries: read-only for the service. Data directory: private (SYSTEM, Administrators, service).
  - Firewall: inbound rules only for the UI port and the agent port, optionally restricted by remote address.
  - Upgrade: binaries are backed up and restored automatically if the new version does not start.
    The database is copied before the upgrade (service stopped) — see docs/backup-restore.md.
  - On first start the one-time setup token is written to <DataPath>\bootstrap\setup-token.txt.
.EXAMPLE
  .\Install-Gateway.ps1 -Source ..\gateway -PublicNames gw01.contoso.local -OpenFirewall -UiRemoteAddress 10.0.10.0/24 -AgentRemoteAddress 10.0.20.0/24
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string]$Source,
    [string]$InstallPath = "$env:ProgramFiles\HvPortal\Gateway",
    [string]$DataPath = "$env:ProgramData\HvPortal\Gateway",
    [int]$UiPort = 8443,
    [int]$AgentPort = 8444,
    [string]$BindAddress = '0.0.0.0',
    [string[]]$PublicNames = @(),
    [string]$UiCertificateThumbprint,
    [switch]$OpenFirewall,
    [string[]]$UiRemoteAddress = @('LocalSubnet'),
    [string[]]$AgentRemoteAddress = @('LocalSubnet')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
Assert-Administrator
$service = 'HvPortalGateway'
$exe = Join-Path $InstallPath 'HvPortal.Gateway.exe'
if (-not (Test-Path (Join-Path $Source 'HvPortal.Gateway.exe'))) { throw "HvPortal.Gateway.exe not found in $Source." }
if ($DataPath -match '^\\\\') { throw 'The data directory must be on local storage (SQLite must not be placed on SMB shares).' }

$upgrade = [bool](Get-Service $service -ErrorAction SilentlyContinue)
if ($upgrade) {
    Stop-Service $service -Force
    # Consistent copy of the SQLite database (main file + WAL + shared memory) while the service is stopped.
    $dbDir = Join-Path $DataPath 'db'
    if (Test-Path (Join-Path $dbDir 'hvportal.db')) {
        $copy = Join-Path $dbDir "pre-upgrade-$(Get-Date -Format yyyyMMddHHmmss)"
        New-Item -ItemType Directory -Force -Path $copy | Out-Null
        foreach ($f in 'hvportal.db', 'hvportal.db-wal', 'hvportal.db-shm') {
            if (Test-Path (Join-Path $dbDir $f)) { Copy-Item (Join-Path $dbDir $f) $copy }
        }
        Write-Host "Database copied to $copy before upgrade (rollback: restore these files with the previous version)."
    }
}

$backup = Install-ServiceBinaries -Source $Source -InstallPath $InstallPath -ServiceName $service
Register-HvPortalService -ServiceName $service -DisplayName 'HvPortal Gateway' -Description 'HvPortal web gateway and control plane' -Exe $exe
Set-PrivateAcl -Path $InstallPath -ExtraAccounts @("NT SERVICE\$service") -ReadOnlyForExtra
New-Item -ItemType Directory -Force -Path $DataPath | Out-Null
Set-PrivateAcl -Path $DataPath -ExtraAccounts @("NT SERVICE\$service")
Register-EventSource 'HvPortal Gateway'

$serviceEnv = @{
    HVP_DATA_DIR            = $DataPath
    ASPNETCORE_ENVIRONMENT  = 'Production'
    Gateway__UiPort         = "$UiPort"
    Gateway__AgentPort      = "$AgentPort"
    Gateway__BindAddress    = $BindAddress
}
for ($i = 0; $i -lt $PublicNames.Count; $i++) { $serviceEnv["Gateway__PublicNames__$i"] = $PublicNames[$i] }
if ($UiCertificateThumbprint) {
    $serviceEnv['Gateway__UiCertificateThumbprint'] = $UiCertificateThumbprint
    Write-Warning "Grant NT SERVICE\$service read access to the private key of certificate $UiCertificateThumbprint (certlm.msc > Manage Private Keys)."
}
Set-ServiceEnvironment -ServiceName $service -Variables $serviceEnv

if ($OpenFirewall) {
    foreach ($r in @(@{ Name = 'HvPortal Gateway UI'; Port = $UiPort; Remote = $UiRemoteAddress }, @{ Name = 'HvPortal Gateway Agents'; Port = $AgentPort; Remote = $AgentRemoteAddress })) {
        Get-NetFirewallRule -DisplayName $r.Name -ErrorAction SilentlyContinue | Remove-NetFirewallRule
        New-NetFirewallRule -DisplayName $r.Name -Direction Inbound -Action Allow -Protocol TCP -LocalPort $r.Port `
            -RemoteAddress $r.Remote -Program $exe -Profile Domain, Private | Out-Null
    }
}

try {
    Wait-ServiceRunning $service
} catch {
    if ($backup) {
        Write-Warning "New version failed to start: $_. Rolling back binaries."
        Restore-HvPortalBackup -ServiceName $service -InstallPath $InstallPath -Backup $backup
    }
    throw
}

Write-Host "HvPortal Gateway is running: https://$(if ($PublicNames) { $PublicNames[0] } else { $env:COMPUTERNAME }):$UiPort"
$token = Join-Path $DataPath 'bootstrap\setup-token.txt'
if (Test-Path $token) { Write-Host "First setup: open the portal and use the one-time token in $token (administrators only)." }
