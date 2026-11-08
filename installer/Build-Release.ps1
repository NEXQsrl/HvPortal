#Requires -Version 7.0
<#
.SYNOPSIS
  Builds a release layout: web UI, self-contained gateway and agent (win-x64), dependency inventory, SHA-256 checksums.
.DESCRIPTION
  Output: artifacts\release\<version>\{gateway,agent,installer,setup}, deps\*.json, SHA256SUMS.txt.
  setup\HvPortal.Gateway.Setup.exe and setup\HvPortal.Agent.Setup.exe are the graphical installers: one file each,
  carrying the (signed) binaries and the installer scripts they drive. The agent setup is also placed in
  gateway\packages so the portal can offer it for download.
  Signing: when -SigningCertificateThumbprint is given, executables and scripts are Authenticode-signed with that
  certificate (LocalMachine\My or CurrentUser\My) and an RFC 3161 timestamp server if -TimestampServer is set.
  Without a certificate the binaries are NOT signed and the script says so — it never fakes a signature.
  Nothing is uploaded anywhere, unless -PublishGitHub: then Publish-GitHubRelease.ps1 publishes the two setup files as
  the only release of the GitHub repository (older releases and their tags are deleted; see that script for its checks).
#>
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$SigningCertificateThumbprint,
    [string]$TimestampServer,
    [switch]$SkipTests,
    [switch]$PublishGitHub
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
Copy-Item (Join-Path $repo 'installer\*.ps1') -Destination (New-Item -ItemType Directory -Force "$out\installer") -Exclude 'Build-Release.ps1', 'New-AgentPackage.ps1', 'New-CodeSigningCertificate.ps1'

function Add-Signature($files) {
    foreach ($f in $files) {
        $sig = if ($TimestampServer) { Set-AuthenticodeSignature $f.FullName $cert -HashAlgorithm SHA256 -TimestampServer $TimestampServer }
               else { Set-AuthenticodeSignature $f.FullName $cert -HashAlgorithm SHA256 }
        $applied = $sig.SignerCertificate -and $sig.SignerCertificate.Thumbprint -eq $cert.Thumbprint
        if ($sig.Status -eq 'Valid') { continue }
        if ($selfSigned -and $applied -and $sig.Status -eq 'UnknownError') { $script:untrusted++; continue }
        throw "Signing failed for $($f.Name): $($sig.StatusMessage)"
    }
}

$cert = $null
if ($SigningCertificateThumbprint) {
    $cert = Get-ChildItem Cert:\LocalMachine\My, Cert:\CurrentUser\My | Where-Object Thumbprint -eq $SigningCertificateThumbprint | Select-Object -First 1
    if (-not $cert -or -not $cert.HasPrivateKey) { throw "Code signing certificate $SigningCertificateThumbprint not found or without private key." }
    # Self-signed certificate (New-CodeSigningCertificate.ps1): the signature is applied, but Windows reports the chain
    # as untrusted (UnknownError) until the certificate is imported as trusted. Accepted only in that exact case.
    $selfSigned = $cert.Subject -eq $cert.Issuer
    $script:untrusted = 0
    $targets = Get-ChildItem $out -Recurse -Include 'HvPortal.*.exe', 'HvPortal.*.dll', '*.ps1'
    Add-Signature $targets
    Write-Host "Signed $($targets.Count) files with $($cert.Subject)."
    # Public certificate shipped with the installer so hosts can trust the publisher (never the private key).
    Export-Certificate -Cert $cert -FilePath "$out\installer\HvPortal-CodeSigning.cer" -Type CERT | Out-Null
    if ($script:untrusted -gt 0) {
        Write-Warning ("Self-signed certificate not trusted on this machine: the signatures are present but Windows, " +
            "SmartScreen and antivirus products will treat them as untrusted until installer\HvPortal-CodeSigning.cer is imported " +
            "into LocalMachine\Root and LocalMachine\TrustedPublisher on each machine.")
    }
    if (-not $TimestampServer) {
        Write-Warning "No timestamp server: signatures stop validating when the certificate expires ($($cert.NotAfter.ToString('yyyy-MM-dd')))."
    }
} else {
    Write-Warning 'No signing certificate provided: binaries and scripts are UNSIGNED.'
}

# Downloadable agent package served by the gateway portal (built from the possibly signed agent).
$packageArgs = @{ AgentDirectory = (Join-Path $out 'agent'); InstallerDirectory = (Join-Path $out 'installer'); OutputDirectory = (Join-Path $out 'gateway\packages') }
# With a certificate the package also gets a signed catalog: agents can then be updated from the portal.
if ($cert) { $packageArgs.SigningCertificateThumbprint = $cert.Thumbprint; if ($TimestampServer) { $packageArgs.TimestampServer = $TimestampServer } }
& (Join-Path $PSScriptRoot 'New-AgentPackage.ps1') @packageArgs

# Graphical setups: a small .NET Framework 4.7.2 program (part of Windows Server 2019+) with the payload embedded.
# The payload is what was just built and signed, so the wizard installs exactly the files of this release.
function New-Setup([string]$Component, [string]$PayloadZip) {
    $build = Join-Path ([IO.Path]::GetTempPath()) ("hvp-setup-$Component-" + [guid]::NewGuid().ToString('N'))
    try {
        # Out-Host: the build output must not become part of the value returned by the function.
        dotnet build (Join-Path $repo 'installer\HvPortal.Setup') -c Release "-p:SetupComponent=$Component" "-p:PayloadZip=$PayloadZip" -o $build --nologo -v quiet | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Build of the $Component setup failed." }
        $exe = Join-Path $setupDir "HvPortal.$Component.Setup.exe"
        Copy-Item (Join-Path $build "HvPortal.$Component.Setup.exe") $exe -Force
        if ($cert) { Add-Signature (Get-Item $exe) }
        return $exe
    } finally {
        Remove-Item $build -Recurse -Force -ErrorAction SilentlyContinue
    }
}
$setupDir = (New-Item -ItemType Directory -Force "$out\setup").FullName
$agentSetup = New-Setup 'Agent' (Join-Path $out 'gateway\packages\HvPortal.Agent-win-x64.zip')
# Served by the portal next to the zip package (Add host > "Guided setup").
Copy-Item $agentSetup (Join-Path $out 'gateway\packages\HvPortal.Agent.Setup.exe') -Force
$gatewayPayload = Join-Path ([IO.Path]::GetTempPath()) ("hvp-gateway-payload-" + [guid]::NewGuid().ToString('N') + '.zip')
try {
    $zip = [IO.Compression.ZipFile]::Open($gatewayPayload, 'Create')
    try {
        foreach ($dir in 'gateway', 'installer') {
            Get-ChildItem (Join-Path $out $dir) -Recurse -File | ForEach-Object {
                # Packages are already compressed.
                $level = if ($_.Extension -in '.zip', '.exe' -and $_.DirectoryName -like '*\packages') { 'NoCompression' } else { 'Optimal' }
                [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, [IO.Path]::GetRelativePath($out, $_.FullName).Replace('\', '/'), $level) | Out-Null
            }
        }
    } finally { $zip.Dispose() }
    $gatewaySetup = New-Setup 'Gateway' $gatewayPayload
} finally {
    Remove-Item $gatewayPayload -Force -ErrorAction SilentlyContinue
}
Write-Host ("Setup files:   {0} ({1:N0} MB), {2} ({3:N0} MB)" -f (Split-Path $gatewaySetup -Leaf), ((Get-Item $gatewaySetup).Length / 1MB),
    (Split-Path $agentSetup -Leaf), ((Get-Item $agentSetup).Length / 1MB))

$sums = Get-ChildItem $out -Recurse -File | Where-Object Name -ne 'SHA256SUMS.txt' | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), [IO.Path]::GetRelativePath($out, $_.FullName)
}
$sums | Out-File -Encoding ascii "$out\SHA256SUMS.txt"
Write-Host "Release layout: $out"
if ($PublishGitHub) {
    & (Join-Path $PSScriptRoot 'Publish-GitHubRelease.ps1') -Version $version
}
