#Requires -Version 5.1
<#
.SYNOPSIS
  Installs a gateway update downloaded by the gateway from the GitHub releases of HvPortal (scheduled task
  "\HvPortal\HvPortal Gateway Update", runs as SYSTEM). Registered by Install-Gateway.ps1; the gateway service account
  may only start the task.
.DESCRIPTION
  The setup staged by the gateway (<data>\update\HvPortal.Gateway.Setup.exe) is writable by the gateway service, so
  nothing in it is trusted as it is:
   1. it is copied to a folder only SYSTEM and Administrators can write, and its SHA-256 compared with the request
      (the gateway compared it with the checksum published with the release);
   2. the setup must be Authenticode-signed by the SAME certificate that signed the installed HvPortal.Gateway.exe;
   3. its payload is extracted (--extract) and the new HvPortal.Gateway.exe and the installer scripts must carry the same
      signer; the new version must not be older than the installed one;
   4. the extracted Install-Gateway.ps1 upgrades the service with the settings it has now (ports, bind address, public
      names, certificate, data folder: read from the service environment); it restores the previous binaries if the new
      version does not start. Firewall rules are not touched.
  Unsigned installations are never updated this way. The outcome is written to <data>\update\last-result.json, which the
  gateway shows in Settings > System.
#>
param(
    [string]$DataPath = "$env:ProgramData\HvPortal\Gateway",
    [string]$InstallPath = "$env:ProgramFiles\HvPortal\Gateway"
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
$service = 'HvPortalGateway'
# The data folder of the running service, when it was installed elsewhere.
$envValues = @((Get-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$service" -Name Environment -ErrorAction SilentlyContinue).Environment)
$settings = @{}
foreach ($line in $envValues) {
    $eq = "$line".IndexOf('=')
    if ($eq -gt 0) { $settings["$line".Substring(0, $eq)] = "$line".Substring($eq + 1) }
}
if ($settings['HVP_DATA_DIR']) { $DataPath = $settings['HVP_DATA_DIR'] }
$updateDir = Join-Path $DataPath 'update'
$resultFile = Join-Path $updateDir 'last-result.json'
$version = $null

function Write-UpdateResult([string]$Status, [string]$Message) {
    @{ timeUtc = (Get-Date).ToUniversalTime().ToString('o'); status = $Status; version = $version; message = $Message } |
        ConvertTo-Json | Set-Content -LiteralPath $resultFile -Encoding UTF8
}

function Get-Signer([string]$Path) {
    # Valid = trusted chain; UnknownError with a self-signed signer = signature present and intact, root not trusted
    # (HvPortal-CodeSigning.cer not imported). Anything else (NotSigned, HashMismatch, ...) is refused.
    $sig = Get-AuthenticodeSignature -LiteralPath $Path
    $selfSigned = $sig.SignerCertificate -and $sig.SignerCertificate.Subject -eq $sig.SignerCertificate.Issuer
    if ($sig.Status -eq 'Valid' -or ($selfSigned -and $sig.Status -eq 'UnknownError')) { return $sig.SignerCertificate }
    return $null
}

$work = Join-Path $env:ProgramData ('HvPortal\GatewayUpdater-' + [guid]::NewGuid().ToString('N'))
try {
    $staged = Join-Path $updateDir 'HvPortal.Gateway.Setup.exe'
    $requestFile = Join-Path $updateDir 'request.json'
    if (-not (Test-Path -LiteralPath $staged) -or -not (Test-Path -LiteralPath $requestFile)) { throw 'No update staged by the gateway.' }
    $request = Get-Content -LiteralPath $requestFile -Raw | ConvertFrom-Json
    $version = [string]$request.version

    New-Item -ItemType Directory -Path $work | Out-Null
    Set-PrivateAcl -Path $work -ExtraAccounts @()
    $setup = Join-Path $work 'HvPortal.Gateway.Setup.exe'
    Copy-Item -LiteralPath $staged -Destination $setup
    $hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
    if ($hash -ne ([string]$request.sha256).ToUpperInvariant()) { throw 'The staged setup does not match the SHA-256 checked by the gateway.' }

    $installedExe = Join-Path $InstallPath 'HvPortal.Gateway.exe'
    $installed = Get-Signer $installedExe
    if (-not $installed) { throw 'The installed gateway is not signed: updates from the portal need a signed build. Update it by hand with the setup.' }
    $setupSigner = Get-Signer $setup
    if (-not $setupSigner -or $setupSigner.Thumbprint -ne $installed.Thumbprint) {
        throw "The setup is not signed by the publisher of the installed gateway ($($installed.Subject), $($installed.Thumbprint))."
    }

    # The setup unpacks its payload (binaries + installer scripts) without installing anything.
    $root = Join-Path $work 'pkg'
    $p = Start-Process -FilePath $setup -ArgumentList @('--extract', "`"$root`"") -Wait -PassThru -WindowStyle Hidden
    if ($p.ExitCode -ne 0) { throw "The setup could not extract its files (exit code $($p.ExitCode))." }
    $newExe = Join-Path $root 'gateway\HvPortal.Gateway.exe'
    $installer = Join-Path $root 'installer\Install-Gateway.ps1'
    foreach ($f in $newExe, $installer, (Join-Path $root 'installer\Common.ps1')) {
        if (-not (Test-Path -LiteralPath $f)) { throw "The setup does not contain $([IO.Path]::GetFileName($f))." }
        $s = Get-Signer $f
        if (-not $s -or $s.Thumbprint -ne $installed.Thumbprint) { throw "$([IO.Path]::GetFileName($f)) of the update is not signed by the publisher of the installed gateway." }
    }
    $newVersion = [version](Get-Item -LiteralPath $newExe).VersionInfo.FileVersion
    $oldVersion = [version](Get-Item -LiteralPath $installedExe).VersionInfo.FileVersion
    if ($newVersion -lt $oldVersion) { throw "The update ($newVersion) is older than the installed gateway ($oldVersion)." }
    $version = "$newVersion"

    # Same settings as now: nothing typed by anyone, all from the service environment written by the previous install.
    $params = @{ Source = (Join-Path $root 'gateway'); InstallPath = $InstallPath; DataPath = $DataPath }
    if ($settings['Gateway__UiPort']) { $params.UiPort = [int]$settings['Gateway__UiPort'] }
    if ($settings['Gateway__AgentPort']) { $params.AgentPort = [int]$settings['Gateway__AgentPort'] }
    if ($settings['Gateway__BindAddress']) { $params.BindAddress = $settings['Gateway__BindAddress'] }
    if ($settings['Gateway__UiCertificateThumbprint']) { $params.UiCertificateThumbprint = $settings['Gateway__UiCertificateThumbprint'] }
    $names = @($settings.Keys | Where-Object { $_ -like 'Gateway__PublicNames__*' } |
        Sort-Object { [int]($_.Substring('Gateway__PublicNames__'.Length)) } | ForEach-Object { $settings[$_] })
    if ($names.Count -gt 0) { $params.PublicNames = [string[]]$names }
    & $installer @params
    Write-UpdateResult 'ok' "Installed $newVersion (was $oldVersion)."
} catch {
    Write-UpdateResult 'failed' $_.Exception.Message
    # The gateway may have been stopped by the installer before the failure: make sure it runs again.
    Start-Service -Name $service -ErrorAction SilentlyContinue
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath (Join-Path $updateDir 'HvPortal.Gateway.Setup.exe') -Force -ErrorAction SilentlyContinue
}
