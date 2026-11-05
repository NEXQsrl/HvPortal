#Requires -Version 7.0
<#
.SYNOPSIS
  Builds a release layout: web UI, self-contained gateway and agent (win-x64), dependency inventory, SHA-256 checksums.
.DESCRIPTION
  Output: artifacts\release\<version>\{gateway,agent,installer}, deps\*.json, SHA256SUMS.txt.
  Signing: when -SigningCertificateThumbprint is given, executables and scripts are Authenticode-signed with that
  certificate (LocalMachine\My or CurrentUser\My) and an RFC 3161 timestamp server if -TimestampServer is set.
  Without a certificate the binaries are NOT signed and the script says so — it never fakes a signature.
  Nothing is uploaded anywhere.
#>
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$SigningCertificateThumbprint,
    [string]$TimestampServer,
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
$version = ([xml](Get-Content (Join-Path $repo 'Directory.Build.props'))).Project.PropertyGroup.Version
$out = Join-Path $repo "artifacts\release\$version"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $out, "$out\deps" | Out-Null

Push-Location (Join-Path $repo 'web')
try {
    npm ci --no-fund --no-audit
    npm run build
    if (-not $SkipTests) { npx vitest run }
    npm ls --all --json | Out-File -Encoding utf8 "$out\deps\npm.json"
} finally { Pop-Location }

if (-not $SkipTests) {
    dotnet test (Join-Path $repo 'HvPortal.slnx') -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed; release aborted.' }
}

foreach ($p in @(@{ Name = 'gateway'; Project = 'src\HvPortal.Gateway' }, @{ Name = 'agent'; Project = 'src\HvPortal.Agent' })) {
    dotnet publish (Join-Path $repo $p.Project) -c $Configuration -r $Runtime --self-contained true `
        -p:PublishSingleFile=false -p:DebugType=none -o (Join-Path $out $p.Name)
    if ($LASTEXITCODE -ne 0) { throw "Publish of $($p.Name) failed." }
    dotnet list (Join-Path $repo $p.Project) package --include-transitive --format json |
        Out-File -Encoding utf8 "$out\deps\nuget-$($p.Name).json"
}
Copy-Item (Join-Path $repo 'installer\*.ps1') -Destination (New-Item -ItemType Directory -Force "$out\installer") -Exclude 'Build-Release.ps1', 'New-AgentPackage.ps1'

if ($SigningCertificateThumbprint) {
    $cert = Get-ChildItem Cert:\LocalMachine\My, Cert:\CurrentUser\My | Where-Object Thumbprint -eq $SigningCertificateThumbprint | Select-Object -First 1
    if (-not $cert -or -not $cert.HasPrivateKey) { throw "Code signing certificate $SigningCertificateThumbprint not found or without private key." }
    $targets = Get-ChildItem $out -Recurse -Include 'HvPortal.*.exe', 'HvPortal.*.dll', '*.ps1'
    foreach ($f in $targets) {
        $sig = if ($TimestampServer) { Set-AuthenticodeSignature $f.FullName $cert -HashAlgorithm SHA256 -TimestampServer $TimestampServer }
               else { Set-AuthenticodeSignature $f.FullName $cert -HashAlgorithm SHA256 }
        if ($sig.Status -ne 'Valid') { throw "Signing failed for $($f.Name): $($sig.StatusMessage)" }
    }
    Write-Host "Signed $($targets.Count) files with $($cert.Subject)."
} else {
    Write-Warning 'No signing certificate provided: binaries and scripts are UNSIGNED.'
}

# Downloadable agent package served by the gateway portal (built from the possibly signed agent).
& (Join-Path $PSScriptRoot 'New-AgentPackage.ps1') -AgentDirectory (Join-Path $out 'agent') -OutputDirectory (Join-Path $out 'gateway\packages')

$sums = Get-ChildItem $out -Recurse -File | Where-Object Name -ne 'SHA256SUMS.txt' | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), [IO.Path]::GetRelativePath($out, $_.FullName)
}
$sums | Out-File -Encoding ascii "$out\SHA256SUMS.txt"
Write-Host "Release layout: $out"
