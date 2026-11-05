#Requires -Version 7.0
<#
.SYNOPSIS
  Runs the gateway from source for development/testing.
.DESCRIPTION
  Uses a private data directory (default: %LOCALAPPDATA%\HvPortal\dev-gateway) and non-default ports so it does not
  collide with an installed instance. By default it listens on loopback only.
  To let agents on other machines connect, pass -BindAddress 0.0.0.0 and -PublicNames with the DNS names/IPs the
  agents will use (they are written into the gateway certificate), and open the agent port in the firewall.
  Not for production: use installer\Install-Gateway.ps1.
.EXAMPLE
  .\Start-DevGateway.ps1 -BindAddress 0.0.0.0 -PublicNames gw01.contoso.local,10.0.0.5
#>
param(
    [string]$DataDirectory = (Join-Path $env:LOCALAPPDATA 'HvPortal\dev-gateway'),
    [int]$UiPort = 18443,
    [int]$AgentPort = 18444,
    [string]$BindAddress = '127.0.0.1',
    [string[]]$PublicNames = @(),
    [switch]$Reset
)
$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
if ($Reset -and (Test-Path -LiteralPath $DataDirectory)) {
    Remove-Item -LiteralPath $DataDirectory -Recurse -Force
}
$env:HVP_DATA_DIR = $DataDirectory
$env:Gateway__UiPort = "$UiPort"
$env:Gateway__AgentPort = "$AgentPort"
$env:Gateway__BindAddress = $BindAddress
for ($i = 0; $i -lt $PublicNames.Count; $i++) { Set-Item "Env:Gateway__PublicNames__$i" $PublicNames[$i] }
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$shown = if ($PublicNames) { $PublicNames[0] } else { 'localhost' }
Write-Host "Data directory: $DataDirectory"
Write-Host "Listening on:   $BindAddress"
Write-Host "UI:    https://${shown}:$UiPort"
Write-Host "Agent: https://${shown}:$AgentPort"
dotnet run --project (Join-Path $repo 'src\HvPortal.Gateway') --no-launch-profile
