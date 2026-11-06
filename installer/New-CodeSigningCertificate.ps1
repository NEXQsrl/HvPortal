#Requires -Version 5.1
<#
.SYNOPSIS
  Creates a self-signed Authenticode code-signing certificate for HvPortal builds (default publisher "NEXQ S.r.l.").
.DESCRIPTION
  The certificate (RSA 3072, SHA-256, code signing EKU only, CA=false) is created in Cert:\CurrentUser\My with its
  private key; only the PUBLIC certificate is exported (.cer), never the private key.
  Use the printed thumbprint with Build-Release.ps1 -SigningCertificateThumbprint.

  A self-signed signature is NOT trusted by Windows, SmartScreen or antivirus products by itself: import the .cer on
  every machine that must trust the binaries (Trusted Root Certification Authorities + Trusted Publishers, e.g. via
  GPO) or use a certificate from a public/enterprise CA. This script changes no trust store.
.EXAMPLE
  .\installer\New-CodeSigningCertificate.ps1 -OutputDirectory artifacts\signing
#>
param(
    [string]$Publisher = 'NEXQ S.r.l.',
    [string]$Country = 'IT',
    [ValidateRange(1, 10)][int]$Years = 5,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\signing')
)
$ErrorActionPreference = 'Stop'

$existing = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
    Where-Object { $_.Subject -like "CN=$Publisher,*" -and $_.NotAfter -gt (Get-Date).AddDays(30) -and $_.HasPrivateKey } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if ($existing) {
    Write-Warning "A valid code-signing certificate for '$Publisher' already exists; reusing it (delete it first to create a new one)."
    $cert = $existing
} else {
    $cert = New-SelfSignedCertificate -Type CodeSigningCert `
        -Subject "CN=$Publisher, O=$Publisher, C=$Country" -FriendlyName "$Publisher code signing (HvPortal, self-signed)" `
        -CertStoreLocation Cert:\CurrentUser\My -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 `
        -KeyUsage DigitalSignature -NotAfter (Get-Date).AddYears($Years) `
        -TextExtension @('2.5.29.19={critical}{text}ca=0')
}

New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$cer = Join-Path (Resolve-Path $OutputDirectory) ('{0}-CodeSigning.cer' -f ($Publisher -replace '[^A-Za-z0-9]+', ''))
Export-Certificate -Cert $cert -FilePath $cer -Type CERT | Out-Null

Write-Host "Subject:     $($cert.Subject)"
Write-Host "Thumbprint:  $($cert.Thumbprint)"
Write-Host "Valid until: $($cert.NotAfter.ToString('yyyy-MM-dd'))"
Write-Host "Public cert: $cer"
Write-Host ''
Write-Host 'Sign a release:'
Write-Host "  .\installer\Build-Release.ps1 -SigningCertificateThumbprint $($cert.Thumbprint)"
Write-Host 'Trust it on a machine (elevated; or distribute with a GPO):'
Write-Host "  Import-Certificate -FilePath <.cer> -CertStoreLocation Cert:\LocalMachine\Root"
Write-Host "  Import-Certificate -FilePath <.cer> -CertStoreLocation Cert:\LocalMachine\TrustedPublisher"
