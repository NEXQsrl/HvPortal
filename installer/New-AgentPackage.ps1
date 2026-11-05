#Requires -Version 7.0
<#
.SYNOPSIS
  Creates the downloadable agent package (HvPortal.Agent-win-x64.zip + manifest) served by the gateway portal.
.DESCRIPTION
  Layout inside the zip:
    agent\        self-contained agent (no .NET installation needed on the host)
    installer\    Install-Agent.ps1, Common.ps1, Uninstall-HvPortal.ps1
    LEGGIMI.txt / README.txt
  The manifest records version, build time and whether HvPortal.Agent.exe carries a valid Authenticode signature
  (checked with Get-AuthenticodeSignature, never assumed).
.PARAMETER AgentDirectory
  Existing published agent folder. If omitted, the agent is published from source.
.PARAMETER OutputDirectory
  Where to write the zip and manifest (e.g. <gateway>\packages).
#>
param(
    [string]$AgentDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64'
)
$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
$version = ([xml](Get-Content (Join-Path $repo 'Directory.Build.props'))).Project.PropertyGroup.Version
$staging = Join-Path ([IO.Path]::GetTempPath()) ("hvp-agent-pkg-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force "$staging\agent", "$staging\installer", $OutputDirectory | Out-Null
try {
    if ($AgentDirectory) {
        Copy-Item -Path (Join-Path $AgentDirectory '*') -Destination "$staging\agent" -Recurse
    } else {
        dotnet publish (Join-Path $repo 'src\HvPortal.Agent') -c $Configuration -r $Runtime --self-contained true -p:DebugType=none -o "$staging\agent"
        if ($LASTEXITCODE -ne 0) { throw 'Agent publish failed.' }
    }
    Copy-Item (Join-Path $repo 'installer\Common.ps1'), (Join-Path $repo 'installer\Install-Agent.ps1'), (Join-Path $repo 'installer\Uninstall-HvPortal.ps1') "$staging\installer"

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
"@
    Set-Content -Path "$staging\LEGGIMI.txt" -Value $readmeIt -Encoding utf8
    Set-Content -Path "$staging\README.txt" -Value $readmeEn -Encoding utf8

    $sig = Get-AuthenticodeSignature (Join-Path $staging 'agent\HvPortal.Agent.exe')
    $signed = $sig.Status -eq 'Valid'
    $zip = Join-Path $OutputDirectory 'HvPortal.Agent-win-x64.zip'
    $tmpZip = "$zip.tmp"
    if (Test-Path $tmpZip) { Remove-Item $tmpZip -Force }
    Compress-Archive -Path "$staging\*" -DestinationPath $tmpZip -CompressionLevel Optimal
    Move-Item $tmpZip $zip -Force
    [ordered]@{
        version    = $version
        builtAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        signed     = $signed
        signer     = if ($signed) { $sig.SignerCertificate.Subject } else { $null }
    } | ConvertTo-Json | Set-Content -Path (Join-Path $OutputDirectory 'HvPortal.Agent-win-x64.json') -Encoding utf8
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash
    Write-Host "Agent package: $zip"
    Write-Host "SHA-256:       $hash"
    Write-Host ("Signed:        {0}" -f $(if ($signed) { $sig.SignerCertificate.Subject } else { 'NO (unsigned build)' }))
} finally {
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
}
