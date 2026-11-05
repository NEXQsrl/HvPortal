#Requires -Version 5.1
<#
.SYNOPSIS
  Removes the HvPortal gateway and/or agent service, binaries, firewall rules and group membership.
.DESCRIPTION
  Data directories (database, keys, certificates, audit) are KEPT unless -RemoveData is given.
  Before removing gateway data, export the keys (HvPortal.Gateway.exe admin export-keys) if a restore may be needed.
  Remember to revoke the host in the portal when uninstalling an agent.
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [ValidateSet('Gateway', 'Agent')][string[]]$Component = @('Gateway', 'Agent'),
    [switch]$RemoveData
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
Assert-Administrator

foreach ($c in $Component) {
    $service = "HvPortal$c"
    $install = "$env:ProgramFiles\HvPortal\$c"
    $data = "$env:ProgramData\HvPortal\$c"
    if (-not $PSCmdlet.ShouldProcess($service, 'Uninstall')) { continue }
    if (Get-Service $service -ErrorAction SilentlyContinue) {
        Stop-Service $service -Force -ErrorAction SilentlyContinue
        if ($c -eq 'Agent') {
            & net.exe localgroup ((New-Object System.Security.Principal.SecurityIdentifier 'S-1-5-32-578').Translate([System.Security.Principal.NTAccount]).Value.Split('\')[-1]) "NT SERVICE\$service" /delete 2>$null | Out-Null
        }
        Invoke-Sc @('delete', $service)
    }
    Get-NetFirewallRule -DisplayName "HvPortal $c*" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    if (Test-Path $install) { Remove-Item $install -Recurse -Force }
    if ($RemoveData -and (Test-Path $data)) {
        Remove-Item $data -Recurse -Force
        Write-Host "Removed data directory $data."
    } elseif (Test-Path $data) {
        Write-Host "Kept data directory $data."
    }
    Write-Host "$service removed."
}
