#Requires -Version 7.0
<#
.SYNOPSIS
  Writes THIRD-PARTY-NOTICES.txt: the license texts of the third-party components distributed with HvPortal.
.DESCRIPTION
  Called by Build-Release.ps1. Sources:
   - .NET: the runtime packages listed in the published *.deps.json files (exactly what ships), with the license file
     of each NuGet package when it has one, else its license expression; the runtime packs add their own
     LICENSE.TXT and THIRD-PARTY-NOTICES.TXT;
   - web: the runtime npm dependencies of web\package.json, transitively, with their LICENSE and NOTICE files, plus the
     build-time packages whose code ends up in the bundle (-ExtraNpmPackages: vendored shadcn/ui sources, Tailwind CSS).
  Packages without a license file are listed with their license identifier; the standard texts of those identifiers
  are appended at the end. Nothing is downloaded: the texts come from node_modules and the local NuGet cache.
#>
param(
    [Parameter(Mandatory)][string]$WebDirectory,
    [Parameter(Mandatory)][string[]]$DepsJsonFiles,
    [Parameter(Mandatory)][string]$OutputFile,
    [string[]]$ExtraNpmPackages = @('tailwindcss', 'tw-animate-css')
)
$ErrorActionPreference = 'Stop'
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$sb = [System.Text.StringBuilder]::new()
$line = '=' * 79
$standard = @{}   # license identifier -> text found in some package (used for packages without a file)
$needed = [System.Collections.Generic.HashSet[string]]::new()   # identifiers of packages without a license file
$missing = [System.Collections.Generic.HashSet[string]]::new()

function Add-Text([string]$text) { [void]$sb.AppendLine($text) }
function Read-Text([string]$path) { (Get-Content -LiteralPath $path -Raw).TrimEnd() }
# Identical texts (the same notices file in 30 Microsoft.Extensions packages) are written once, then referenced.
$written = @{}
function Add-File([string]$path, [string]$owner) {
    $text = Read-Text $path
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text)))
    Add-Text ''
    if ($written.ContainsKey($hash)) { Add-Text "(Same text as $(Split-Path $path -Leaf) of $($written[$hash]), above.)"; return }
    $written[$hash] = $owner
    Add-Text $text
}
function Get-LicenseFiles([string]$dir) {
    Get-ChildItem -LiteralPath $dir -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^(licen[cs]e|copying)(\.(txt|md))?$' }
}
function Get-NoticeFiles([string]$dir) {
    Get-ChildItem -LiteralPath $dir -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^(notice|third-party-notices)(\.(txt|md))?$' }
}

Add-Text 'THIRD-PARTY SOFTWARE NOTICES AND LICENSES - HvPortal'
Add-Text ''
Add-Text 'HvPortal (Copyright (c) NEXQ S.r.l.) is licensed under the PolyForm Shield License 1.0.0 (LICENSE).'
Add-Text 'It includes the third-party components below, each licensed under its own license, reproduced here.'
Add-Text 'The PolyForm Shield License does not apply to them and does not restrict the rights their licenses grant.'
Add-Text 'Summary and attributions: NOTICE. Generated at build time by installer\New-ThirdPartyNotices.ps1.'
Add-Text ''

# ---------------------------------------------------------------- .NET (gateway, agent)
$nuget = [ordered]@{}
foreach ($deps in $DepsJsonFiles) {
    $j = Get-Content -LiteralPath $deps -Raw | ConvertFrom-Json
    foreach ($p in $j.libraries.PSObject.Properties) {
        if ($p.Value.type -notin 'package', 'runtimepack') { continue }
        $id, $ver = $p.Name -split '/', 2
        $id = $id -replace '^runtimepack\.', ''
        $nuget["$id/$ver"] = @{ Id = $id; Version = $ver }
    }
}
Add-Text $line
Add-Text 'PART 1 - .NET COMPONENTS (gateway and agent)'
Add-Text $line
foreach ($pkg in $nuget.Values) {
    $dir = Join-Path $nugetRoot "$($pkg.Id.ToLowerInvariant())\$($pkg.Version)"
    # Runtime packs and reference packs may come from the SDK's packs folder instead of the NuGet cache.
    $sdkPack = Join-Path $env:ProgramFiles "dotnet\packs\$($pkg.Id)\$($pkg.Version)"
    if (-not (Test-Path $dir) -and (Test-Path $sdkPack)) { $dir = $sdkPack }
    $nuspec = Get-ChildItem -LiteralPath $dir -Filter *.nuspec -ErrorAction SilentlyContinue | Select-Object -First 1
    $meta = if ($nuspec) { ([xml](Get-Content -LiteralPath $nuspec.FullName)).package.metadata } else { $null }
    $expr = if (-not $meta) { $null } elseif ($meta.license -is [string]) { $meta.license } elseif ($meta.license.type -eq 'expression') { $meta.license.'#text' } else { $null }
    Add-Text ''
    Add-Text "--- $($pkg.Id) $($pkg.Version)"
    if ($meta.copyright) { Add-Text "Copyright: $($meta.copyright)" }
    if ($expr) { Add-Text "License: $expr" } elseif ($meta.licenseUrl) { Add-Text "License: $($meta.licenseUrl)" }
    $files = @(Get-LicenseFiles $dir)
    if ($meta -and $meta.license.type -eq 'file') { $files = @(Get-Item -LiteralPath (Join-Path $dir $meta.license.'#text') -ErrorAction SilentlyContinue) + $files | Select-Object -Unique }
    if ($files.Count -eq 0 -and -not $expr -and -not $meta.licenseUrl) { Add-Text 'License: see the package (not found locally at build time).'; [void]$missing.Add("$($pkg.Id) $($pkg.Version)") }
    foreach ($f in $files) { Add-File $f.FullName $pkg.Id }
    if ($files.Count -eq 0 -and $expr) { Add-Text "(The package has no license file: the text of $expr is in the appendix.)"; [void]$needed.Add($expr) }
    foreach ($f in Get-NoticeFiles $dir) { Add-Text ''; Add-Text "Notices of $($pkg.Id):"; Add-File $f.FullName $pkg.Id }
}

# ---------------------------------------------------------------- web (bundled in the gateway)
$npm = [ordered]@{}
function Add-NpmPackage([string]$name, [string]$from, [switch]$WithoutDependencies) {
    $dir = @((Join-Path $from "node_modules\$name"), (Join-Path $WebDirectory "node_modules\$name")) | Where-Object { Test-Path (Join-Path $_ 'package.json') } | Select-Object -First 1
    if (-not $dir) { return }
    $key = (Resolve-Path $dir).Path
    if ($npm.Contains($key)) { return }
    $pj = Get-Content -LiteralPath (Join-Path $dir 'package.json') -Raw | ConvertFrom-Json -AsHashtable
    $npm[$key] = $pj
    if ($WithoutDependencies) { return }
    foreach ($d in @($pj['dependencies'].Keys) + @($pj['peerDependencies'].Keys)) { if ($d) { Add-NpmPackage $d $dir } }
}
$top = Get-Content -LiteralPath (Join-Path $WebDirectory 'package.json') -Raw | ConvertFrom-Json -AsHashtable
foreach ($d in $top['dependencies'].Keys) { Add-NpmPackage $d $WebDirectory }
# Build-time packages whose code is in the bundle: only the package itself (their own tools are not distributed).
foreach ($d in $ExtraNpmPackages) { Add-NpmPackage $d $WebDirectory -WithoutDependencies }

Add-Text ''
Add-Text $line
Add-Text 'PART 2 - WEB INTERFACE COMPONENTS (JavaScript, CSS and fonts bundled in the gateway)'
Add-Text $line
foreach ($e in ($npm.GetEnumerator() | Sort-Object { $_.Value['name'] })) {
    $pj = $e.Value
    $lic = if ($pj['license'] -is [string]) { $pj['license'] } elseif ($pj['license']) { $pj['license']['type'] } else { 'see the package' }
    Add-Text ''
    Add-Text "--- $($pj['name']) $($pj['version'])"
    Add-Text "License: $lic"
    $files = @(Get-LicenseFiles $e.Key)
    foreach ($f in $files) { Add-File $f.FullName $pj['name'] }
    if ($files.Count -eq 0) {
        $author = if ($pj['author'] -is [string]) { $pj['author'] } elseif ($pj['author']) { $pj['author']['name'] } else { $null }
        if ($author) { Add-Text "Author: $author" }
        Add-Text "(The package has no license file: the text of $lic is in the appendix.)"
        [void]$needed.Add($lic)
    } elseif (-not $standard.ContainsKey($lic)) {
        $standard[$lic] = Read-Text $files[0].FullName
    }
    foreach ($f in Get-NoticeFiles $e.Key) { Add-Text ''; Add-Text "Notices of $($pj['name']):"; Add-File $f.FullName $pj['name'] }
}

# Sources vendored in the repository (not npm dependencies).
$shadcn = Join-Path $WebDirectory 'src\components\ui\LICENSE-shadcn.md'
if (Test-Path -LiteralPath $shadcn) {
    Add-Text ''
    Add-Text '--- shadcn/ui (component sources in web\src\components\ui, partly modified, and web\src\styles\shadcn-tailwind.css)'
    Add-Text 'License: MIT'
    Add-File $shadcn 'shadcn/ui'
} else {
    [void]$missing.Add('license of the vendored shadcn/ui sources (web\src\components\ui\LICENSE-shadcn.md)')
}

# ---------------------------------------------------------------- appendix
$mit = @'
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
documentation files (the "Software"), to deal in the Software without restriction, including without limitation the
rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit
persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the
Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
'@
$unlicense = @'
This is free and unencumbered software released into the public domain.

Anyone is free to copy, modify, publish, use, compile, sell, or distribute this software, either in source code form or
as a compiled binary, for any purpose, commercial or non-commercial, and by any means.

In jurisdictions that recognize copyright laws, the author or authors of this software dedicate any and all copyright
interest in the software to the public domain. We make this dedication for the benefit of the public at large and to
the detriment of our heirs and successors. We intend this dedication to be an overt act of relinquishment in
perpetuity of all present and future rights to this software under copyright law.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS BE
LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

For more information, please refer to <https://unlicense.org>
'@
Add-Text ''
Add-Text $line
Add-Text 'APPENDIX - STANDARD LICENSE TEXTS (for the packages above without a license file)'
Add-Text $line
foreach ($id in ($needed | Sort-Object)) {
    Add-Text ''
    Add-Text "--- $id"
    if ($id -eq 'MIT') { Add-Text $mit.TrimEnd() }
    elseif ($id -eq 'Unlicense') { Add-Text $unlicense.TrimEnd() }
    elseif ($standard.ContainsKey($id)) { Add-Text "(Text as found in another package under $id; the copyright line of that package does not apply.)"; Add-Text $standard[$id] }
    else { Add-Text "See https://spdx.org/licenses/$id.html"; [void]$missing.Add("text of $id") }
}

New-Item -ItemType Directory -Force (Split-Path $OutputFile -Parent) | Out-Null
Set-Content -LiteralPath $OutputFile -Value $sb.ToString() -Encoding utf8
Write-Host ("Third-party notices: {0} .NET packages, {1} npm packages -> {2} ({3:N0} KB)" -f $nuget.Count, $npm.Count, $OutputFile, ((Get-Item $OutputFile).Length / 1KB))
foreach ($m in $missing) { Write-Warning "Third-party notices: not found locally: $m" }
