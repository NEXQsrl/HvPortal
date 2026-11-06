#Requires -Version 5.1
<#
.SYNOPSIS
  Runs Install-Gateway.ps1 or Install-Agent.ps1 with the choices made in the graphical setup
  (HvPortal.Gateway.Setup.exe / HvPortal.Agent.Setup.exe).
.DESCRIPTION
  The setup writes its choices to a JSON file (parameter name -> value) and starts this script. Values never pass
  through a command line, so nothing typed in the wizard is ever parsed as PowerShell code. Only the two installer
  scripts next to this file can be run, and only with parameters they declare.
  The enrollment token of the agent is not in the file: it arrives in the HVP_ENROLLMENT_TOKEN environment variable.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Gateway', 'Agent')][string]$Component,
    [Parameter(Mandatory)][string]$ParametersFile
)
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot "Install-$Component.ps1"
if (-not (Test-Path -LiteralPath $script)) { throw "Install-$Component.ps1 not found next to Invoke-Setup.ps1." }
$declared = (Get-Command -Name $script).Parameters.Keys
$json = Get-Content -LiteralPath $ParametersFile -Raw -Encoding UTF8 | ConvertFrom-Json
$parameters = @{}
foreach ($p in $json.PSObject.Properties) {
    if ($declared -notcontains $p.Name) { throw "Unknown parameter '$($p.Name)' for Install-$Component.ps1." }
    $parameters[$p.Name] = $p.Value
}
& $script @parameters
