#Requires -Version 7.0
<#
.SYNOPSIS
  Creates the downloadable agent package (HvPortal.Agent-win-x64.zip + manifest) served by the gateway portal.
.DESCRIPTION
  Layout inside the zip:
    agent\        self-contained agent (no .NET installation needed on the host)
    installer\    Install-Agent.ps1, Common.ps1, Uninstall-HvPortal.ps1, Invoke-Setup.ps1 (+ HvPortal-CodeSigning.cer for signed builds)
    LEGGIMI.txt / README.txt
  The manifest records version, build time and whether HvPortal.Agent.exe carries a valid Authenticode signature
  (checked with Get-AuthenticodeSignature, never assumed). A self-signed signature whose root is not trusted on the
  build machine is recorded as signed (signature present, hash intact) with selfSigned = true.
.PARAMETER InstallerDirectory
  Folder with the (possibly signed) installer scripts; default: the repository installer folder.
.PARAMETER AgentDirectory
  Existing published agent folder. If omitted, the agent is published from source.
.PARAMETER OutputDirectory
  Where to write the zip and manifest (e.g. <gateway>\packages).
#>
param(
    [string]$AgentDirectory,
    [string]$InstallerDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    # Signs HvPortal.Agent.cat, a catalog of every file of the package (Windows only). Agents install updates sent by
    # the portal only when this catalog is signed by the certificate of the installed agent.
    [string]$SigningCertificateThumbprint,
    [string]$TimestampServer
)
$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
$version = ([xml](Get-Content (Join-Path $repo 'Directory.Build.props'))).Project.PropertyGroup.Version
$staging = Join-Path ([IO.Path]::GetTempPath()) ("hvp-agent-pkg-" + [guid]::NewGuid().ToString('N'))
# Join-Path everywhere: the script also runs in PowerShell on Linux (Docker image build).
$agentStage = Join-Path $staging 'agent'
$installerStage = Join-Path $staging 'installer'
New-Item -ItemType Directory -Force $agentStage, $installerStage, $OutputDirectory | Out-Null
try {
    if ($AgentDirectory) {
        Copy-Item -Path (Join-Path $AgentDirectory '*') -Destination $agentStage -Recurse
    } else {
        dotnet publish (Join-Path (Join-Path $repo 'src') 'HvPortal.Agent') -c $Configuration -r $Runtime --self-contained true -p:DebugType=none -p:EnableWindowsTargeting=true -o $agentStage
        if ($LASTEXITCODE -ne 0) { throw 'Agent publish failed.' }
    }
    $inst = if ($InstallerDirectory) { $InstallerDirectory } else { Join-Path $repo 'installer' }
    # Invoke-Setup.ps1: used by the graphical setup (HvPortal.Agent.Setup.exe), whose payload is this same package.
    Copy-Item (Join-Path $inst 'Common.ps1'), (Join-Path $inst 'Install-Agent.ps1'), (Join-Path $inst 'Uninstall-HvPortal.ps1'), (Join-Path $inst 'Invoke-Setup.ps1'),
        (Join-Path $inst 'Update-HvPortalAgent.ps1') $installerStage
    $publisherCer = Join-Path $inst 'HvPortal-CodeSigning.cer'
    if (Test-Path $publisherCer) { Copy-Item $publisherCer $installerStage }

    $readmeIt = @"
HvPortal Agent $version
=======================
La guida completa, con i comandi già compilati, è nel portale: Host > Aggiungi host (oppure menu Guida).

In breve, sull'host Hyper-V, in PowerShell come amministratore:
  1. Verifica l'impronta SHA-256 dello zip con quella mostrata nel portale:  Get-FileHash .\HvPortal.Agent-win-x64.zip
  2. Sblocca ed estrai:  Unblock-File .\HvPortal.Agent-win-x64.zip; Expand-Archive .\HvPortal.Agent-win-x64.zip C:\HvPortal\agent-package
  3. Verifica la rete verso il gateway:  Test-NetConnection <gateway> -Port <porta agenti>
  4. cd C:\HvPortal\agent-package\installer
     `$env:HVP_ENROLLMENT_TOKEN = '<token monouso dal portale, valido 15 minuti>'
     .\Install-Agent.ps1 -Source ..\agent -Gateway https://<gateway>:<porta> -CaSha256 <impronta CA>

Aggiornamento di un agente già installato (nel portale: Guida > Aggiornare i servizi Windows):
  1. Verifica ed estrai in una nuova cartella:  Expand-Archive .\HvPortal.Agent-win-x64.zip C:\HvPortal\agent-package-$version -Force
  2. cd C:\HvPortal\agent-package-$version\installer
     .\Install-Agent.ps1 -Source ..\agent
     (senza token: l'identità dell'agente viene mantenuta; se il servizio non parte viene ripristinata la versione precedente)

Disinstallazione:  .\Uninstall-HvPortal.ps1 -Component Agent   (poi rimuovi l'host nel portale)
"@
    $readmeEn = @"
HvPortal Agent $version
=======================
The complete guide, with ready-to-paste commands, is in the portal: Hosts > Add host (or the Guide menu).

Summary, on the Hyper-V host, in an elevated PowerShell:
  1. Compare the zip SHA-256 with the value shown in the portal:  Get-FileHash .\HvPortal.Agent-win-x64.zip
  2. Unblock and extract:  Unblock-File .\HvPortal.Agent-win-x64.zip; Expand-Archive .\HvPortal.Agent-win-x64.zip C:\HvPortal\agent-package
  3. Check network access to the gateway:  Test-NetConnection <gateway> -Port <agent port>
  4. cd C:\HvPortal\agent-package\installer
     `$env:HVP_ENROLLMENT_TOKEN = '<one-time token from the portal, valid 15 minutes>'
     .\Install-Agent.ps1 -Source ..\agent -Gateway https://<gateway>:<port> -CaSha256 <CA fingerprint>

Updating an installed agent (in the portal: Guide > Update the Windows services):
  1. Verify and extract into a new folder:  Expand-Archive .\HvPortal.Agent-win-x64.zip C:\HvPortal\agent-package-$version -Force
  2. cd C:\HvPortal\agent-package-$version\installer
     .\Install-Agent.ps1 -Source ..\agent
     (no token: the agent identity is kept; if the service does not start, the previous version is restored)

Uninstall:  .\Uninstall-HvPortal.ps1 -Component Agent   (then remove the host in the portal)
"@
    Set-Content -Path (Join-Path $staging 'LEGGIMI.txt') -Value $readmeIt -Encoding utf8
    Set-Content -Path (Join-Path $staging 'README.txt') -Value $readmeEn -Encoding utf8

    # Authenticode can be verified only on Windows: elsewhere the package is reported as unsigned (never assumed).
    $sig = $null
    if (Get-Command Get-AuthenticodeSignature -ErrorAction SilentlyContinue) { $sig = Get-AuthenticodeSignature (Join-Path $agentStage 'HvPortal.Agent.exe') }
    # Valid = trusted chain. UnknownError with a self-signed signer = signature present and intact, root not trusted here.
    $selfSigned = $null -ne $sig -and $null -ne $sig.SignerCertificate -and $sig.SignerCertificate.Subject -eq $sig.SignerCertificate.Issuer
    $signed = $null -ne $sig -and ($sig.Status -eq 'Valid' -or ($selfSigned -and $sig.Status -eq 'UnknownError'))

    # Signed catalog of the whole package (updates from the portal check it on the host). Created last, after every
    # file is final; it lists every file except itself.
    $catalogSigned = $false
    if ($SigningCertificateThumbprint -and (Get-Command New-FileCatalog -ErrorAction SilentlyContinue)) {
        $cert = Get-ChildItem Cert:\LocalMachine\My, Cert:\CurrentUser\My | Where-Object Thumbprint -eq $SigningCertificateThumbprint | Select-Object -First 1
        if (-not $cert -or -not $cert.HasPrivateKey) { throw "Code signing certificate $SigningCertificateThumbprint not found or without private key." }
        $catTemp = Join-Path ([IO.Path]::GetTempPath()) ('hvp-agent-' + [guid]::NewGuid().ToString('N') + '.cat')
        New-FileCatalog -Path $staging -CatalogFilePath $catTemp -CatalogVersion 2.0 | Out-Null
        $catalog = Join-Path $staging 'HvPortal.Agent.cat'
        Move-Item $catTemp $catalog
        $catSig = if ($TimestampServer) { Set-AuthenticodeSignature $catalog $cert -HashAlgorithm SHA256 -TimestampServer $TimestampServer }
                  else { Set-AuthenticodeSignature $catalog $cert -HashAlgorithm SHA256 }
        $catSelf = $cert.Subject -eq $cert.Issuer
        if (-not ($catSig.Status -eq 'Valid' -or ($catSelf -and $catSig.Status -eq 'UnknownError'))) { throw "Signing the package catalog failed: $($catSig.StatusMessage)" }
        $check = Test-FileCatalog -Path $staging -CatalogFilePath $catalog -FilesToSkip 'HvPortal.Agent.cat'
        if ("$check" -ne 'Valid') { throw "The package catalog does not validate ($check)." }
        $catalogSigned = $true
    } elseif ($SigningCertificateThumbprint) {
        Write-Warning 'New-FileCatalog is not available here: the package has no signed catalog (updates from the portal will refuse it).'
    }
    $zip = Join-Path $OutputDirectory 'HvPortal.Agent-win-x64.zip'
    $tmpZip = "$zip.tmp"
    if (Test-Path $tmpZip) { Remove-Item $tmpZip -Force }
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $tmpZip -CompressionLevel Optimal
    Move-Item $tmpZip $zip -Force
    [ordered]@{
        version    = $version
        builtAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        signed     = $signed
        signer     = if ($signed) { $sig.SignerCertificate.Subject } else { $null }
        selfSigned = $signed -and $selfSigned
        catalogSigned = $catalogSigned
    } | ConvertTo-Json | Set-Content -Path (Join-Path $OutputDirectory 'HvPortal.Agent-win-x64.json') -Encoding utf8
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash
    Write-Host "Agent package: $zip"
    Write-Host "SHA-256:       $hash"
    Write-Host ("Signed:        {0}" -f $(if ($signed) { $sig.SignerCertificate.Subject + $(if ($selfSigned) { ' (self-signed)' } else { '' }) } else { 'NO (unsigned build)' }))
} finally {
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
}
