#Requires -Version 5.1
<#
.SYNOPSIS
  Installs an agent update downloaded by the agent from the gateway (scheduled task "\HvPortal\HvPortal Agent Update",
  runs as SYSTEM). Registered by Install-Agent.ps1; the agent service account may only start the task.
.DESCRIPTION
  The package staged by the agent (<data>\update\package.zip) is writable by the agent service, so nothing in it is
  trusted as it is:
   1. it is copied to a folder only SYSTEM and Administrators can write, and its SHA-256 compared with the request;
   2. the package must contain HvPortal.Agent.cat, a catalog of every file, Authenticode-signed by the SAME certificate
      that signed the installed HvPortal.Agent.exe; Test-FileCatalog must report every file unchanged (none added);
   3. the new HvPortal.Agent.exe must carry the same signer, and its version must not be older than the installed one;
   4. the package's own Install-Agent.ps1 (covered by the catalog) upgrades the service, keeping identity, account,
      storage folders and data; it restores the previous binaries if the new version does not start.
  Unsigned installations are never updated this way. The outcome is written to <data>\update\last-result.json, which
  the agent reports to the gateway when it reconnects.
#>
param(
    [string]$DataPath = "$env:ProgramData\HvPortal\Agent",
    [string]$InstallPath = "$env:ProgramFiles\HvPortal\Agent"
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
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

function Assert-PackageEntries([string]$Path) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        if ($archive.Entries.Count -gt 5000) { throw 'The package has too many entries.' }
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName -replace '/', '\'
            $segments = $name.Split('\')
            if ($name.Length -eq 0 -or $name.Length -gt 240 -or $name.StartsWith('\') -or $name.Contains(':') -or
                ($segments | Where-Object { $_ -eq '..' -or $_ -eq '.' }) -or $name.IndexOfAny([IO.Path]::GetInvalidPathChars()) -ge 0) {
                throw "The package contains an entry with an unsafe path: '$($entry.FullName)'."
            }
        }
    } finally {
        $archive.Dispose()
    }
}

$work = Join-Path $env:ProgramData ('HvPortal\Updater-' + [guid]::NewGuid().ToString('N'))
try {
    $package = Join-Path $updateDir 'package.zip'
    $requestFile = Join-Path $updateDir 'request.json'
    if (-not (Test-Path -LiteralPath $package) -or -not (Test-Path -LiteralPath $requestFile)) { throw 'No update package staged by the agent.' }
    $request = Get-Content -LiteralPath $requestFile -Raw | ConvertFrom-Json
    $version = [string]$request.version

    New-Item -ItemType Directory -Path $work | Out-Null
    Set-PrivateAcl -Path $work -ExtraAccounts @()
    $zip = Join-Path $work 'package.zip'
    Copy-Item -LiteralPath $package -Destination $zip
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
    if ($hash -ne ([string]$request.sha256).ToUpperInvariant()) { throw 'The staged package does not match the SHA-256 checked by the agent.' }
    $root = Join-Path $work 'pkg'
    # The zip is not trusted yet (the signature is checked on the extracted files): before SYSTEM extracts anything,
    # every entry must be a plain relative path that stays inside the extraction folder (no "..", no rooted or drive
    # paths, no alternate data streams), so a crafted package cannot write anywhere else on the host.
    Assert-PackageEntries -Path $zip
    Expand-Archive -LiteralPath $zip -DestinationPath $root

    $installed = Get-Signer (Join-Path $InstallPath 'HvPortal.Agent.exe')
    if (-not $installed) { throw 'The installed agent is not signed: updates from the portal need a signed build. Update it by hand (Guide > Update).' }
    $catalog = Join-Path $root 'HvPortal.Agent.cat'
    if (-not (Test-Path -LiteralPath $catalog)) { throw 'The package has no signed catalog (unsigned build): update it by hand.' }
    $catalogSigner = Get-Signer $catalog
    if (-not $catalogSigner -or $catalogSigner.Thumbprint -ne $installed.Thumbprint) {
        throw "The package is not signed by the publisher of the installed agent ($($installed.Subject), $($installed.Thumbprint))."
    }
    $check = Test-FileCatalog -Path $root -CatalogFilePath $catalog -FilesToSkip 'HvPortal.Agent.cat' -Detailed
    if ("$($check.Status)" -ne 'Valid') { throw "The content of the package does not match its signed catalog ($($check.Status))." }
    $newExe = Join-Path $root 'agent\HvPortal.Agent.exe'
    $newSigner = Get-Signer $newExe
    if (-not $newSigner -or $newSigner.Thumbprint -ne $installed.Thumbprint) { throw 'HvPortal.Agent.exe of the package is not signed by the publisher of the installed agent.' }
    $newVersion = [version](Get-Item -LiteralPath $newExe).VersionInfo.FileVersion
    $oldVersion = [version](Get-Item -LiteralPath (Join-Path $InstallPath 'HvPortal.Agent.exe')).VersionInfo.FileVersion
    if ($newVersion -lt $oldVersion) { throw "The package ($newVersion) is older than the installed agent ($oldVersion)." }
    $version = "$newVersion"

    & (Join-Path $root 'installer\Install-Agent.ps1') -Source (Join-Path $root 'agent')
    Write-UpdateResult 'ok' "Installed $newVersion (was $oldVersion)."
} catch {
    Write-UpdateResult 'failed' $_.Exception.Message
    # The agent may have been stopped by the installer before the failure: make sure it runs again.
    Start-Service -Name HvPortalAgent -ErrorAction SilentlyContinue
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath (Join-Path $updateDir 'package.zip') -Force -ErrorAction SilentlyContinue
}
