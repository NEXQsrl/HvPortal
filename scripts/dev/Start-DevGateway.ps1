#Requires -Version 7.0
<#
.SYNOPSIS
  Runs the gateway from source for development, as the current (non-elevated) user.
.DESCRIPTION
  Uses a private data directory (default: %LOCALAPPDATA%\HvPortal\dev-gateway), loopback binding and
  non-default ports so it does not collide with an installed instance. Not for production.
#>
param(
    [string]$DataDirectory = (Join-Path $env:LOCALAPPDATA 'HvPortal\dev-gateway'),
    [int]$UiPort = 18443,
    [int]$AgentPort = 18444,
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
$env:Gateway__BindAddress = '127.0.0.1'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
Write-Host "Data directory: $DataDirectory"
Write-Host "UI:    https://localhost:$UiPort"
Write-Host "Agent: https://localhost:$AgentPort"
dotnet run --project (Join-Path $repo 'src\HvPortal.Gateway') --no-launch-profile
