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
        $startName = Get-ServiceStartName $service
        Stop-HvPortalService -ServiceName $service
        if ($c -eq 'Agent') {
            # Right granted by Install-Agent -FileRestore Enabled (the SID of the virtual account resolves while the service exists).
            try {
                $account = if ($startName -and $startName -ne 'LocalSystem') { $startName -replace '^\.\\', "$env:COMPUTERNAME\" } else { "NT SERVICE\$service" }
                $restoreSid = (New-Object System.Security.Principal.NTAccount $account).Translate([System.Security.Principal.SecurityIdentifier]).Value
                Set-AccountUserRights -Sid $restoreSid -Remove $HvPortalFileRestoreRight
            } catch { Write-Warning "Could not remove the file restore right of the agent account: $($_.Exception.Message)" }
            & net.exe localgroup ((New-Object System.Security.Principal.SecurityIdentifier 'S-1-5-32-578').Translate([System.Security.Principal.NTAccount]).Value.Split('\')[-1]) "NT SERVICE\$service" /delete 2>$null | Out-Null
        }
        Invoke-Sc @('delete', $service)
        if ($c -eq 'Agent') { Unregister-AgentUpdater } else { Unregister-GatewayUpdater }
        # Dedicated local account created by Install-Agent -ServiceAccount: remove its rights, profile and the user.
        if ($c -eq 'Agent' -and $startName -and $startName -notlike 'NT SERVICE\*' -and $startName -ne 'LocalSystem') {
            $name = $startName.Split('\')[-1]
            $user = Get-LocalUser -Name $name -ErrorAction SilentlyContinue
            if ($user -and $user.Description -eq $HvPortalServiceAccountMarker) {
                $sid = $user.SID.Value
                Set-AccountUserRights -Sid $sid -Remove $HvPortalServiceAccountRights
                Get-CimInstance Win32_UserProfile -Filter "SID='$sid'" -ErrorAction SilentlyContinue | Remove-CimInstance -ErrorAction SilentlyContinue
                Remove-LocalUser -Name $name
                Write-Host "Removed the service account $name."
            }
        }
    }
    Get-NetFirewallRule -DisplayName "HvPortal $c*" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    if (Test-Path $install) {
        Clear-DirectoryContent -Path $install
        Remove-Item $install -Recurse -Force
    }
    if ($RemoveData -and (Test-Path $data)) {
        Remove-Item $data -Recurse -Force
        Write-Host "Removed data directory $data."
    } elseif (Test-Path $data) {
        Write-Host "Kept data directory $data."
    }
    Write-Host "$service removed."
}
