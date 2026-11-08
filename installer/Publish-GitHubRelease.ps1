#Requires -Version 7.0
<#
.SYNOPSIS
  Publishes the setup files of a release built by Build-Release.ps1 as the only GitHub release of the repository.
.DESCRIPTION
  Uploads setup\HvPortal.Gateway.Setup.exe and setup\HvPortal.Agent.Setup.exe of artifacts\release\<version> to a
  GitHub release "v<version>" and then deletes every other release of the repository and its tag, so that only the
  latest release is on GitHub. Order: the new release is created as a draft, the files are uploaded and checked, the
  release is published, and only then the old ones are removed (a failed upload leaves the previous release in place).
  Publishing the same version again replaces its release (for example after committing, so that the tag points to the
  commit the files were built from).

  Checks before anything is sent:
   - both setup files exist and carry an Authenticode signature (-AllowUnsigned to publish unsigned files);
   - the working tree has no uncommitted changes and HEAD is on the remote (-AllowUncommitted to publish anyway: the
     release notes then say that the files contain changes not in the tagged commit);
   - the repository is private (-AllowPublic to publish to a public repository).

  Authentication (never printed, never stored by this script): -Token, else GITHUB_TOKEN / GH_TOKEN, else the GitHub
  CLI (gh auth token), else the credential Git already uses for github.com (Git Credential Manager).
  The token needs permission to write releases and to delete tags of the repository (classic: "repo"; fine-grained:
  Contents read and write).
.EXAMPLE
  .\installer\Publish-GitHubRelease.ps1                       # version from Directory.Build.props
  .\installer\Publish-GitHubRelease.ps1 -WhatIf               # shows what it would do, sends nothing that changes GitHub
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Version,
    [string]$Repository,
    [securestring]$Token,
    [switch]$AllowUnsigned,
    [switch]$AllowUncommitted,
    [switch]$AllowPublic
)
$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
if (-not $Version) { $Version = ([xml](Get-Content (Join-Path $repo 'Directory.Build.props'))).Project.PropertyGroup.Version }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid version '$Version'." }
$release = Join-Path $repo "artifacts\release\$Version"
$files = @('HvPortal.Gateway.Setup.exe', 'HvPortal.Agent.Setup.exe') | ForEach-Object { Join-Path $release "setup\$_" }
foreach ($f in $files) {
    if (-not (Test-Path -LiteralPath $f)) { throw "$f not found: run installer\Build-Release.ps1 first." }
    $sig = Get-AuthenticodeSignature -LiteralPath $f
    if ($sig.Status -eq 'NotSigned' -and -not $AllowUnsigned) { throw "$(Split-Path $f -Leaf) is not signed (use -AllowUnsigned to publish it anyway)." }
}

# ---- repository and source commit
if (-not $Repository) {
    $url = git -C $repo remote get-url origin
    if ($url -notmatch 'github\.com[:/]([^/]+)/([^/]+?)(\.git)?$') { throw "The remote 'origin' ($url) is not a GitHub repository: use -Repository owner/name." }
    $Repository = "$($Matches[1])/$($Matches[2])"
}
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw "Invalid repository '$Repository'." }
$commit = (git -C $repo rev-parse HEAD).Trim()
$dirty = @(git -C $repo status --porcelain).Count -gt 0
$pushed = @(git -C $repo branch -r --contains $commit 2>$null | Where-Object { $_ -match 'origin/' }).Count -gt 0
if (($dirty -or -not $pushed) -and -not $AllowUncommitted) {
    throw "The build may not match the commit that the tag would point to (uncommitted changes: $dirty, commit on the remote: $pushed). Commit and push first, or use -AllowUncommitted."
}
if (-not $pushed) { throw "Commit $commit is not on the remote: GitHub cannot tag it. Push it first." }

# ---- token
function Get-PlainToken {
    if ($Token) { return [Net.NetworkCredential]::new('', $Token).Password }
    foreach ($name in 'GITHUB_TOKEN', 'GH_TOKEN') { $v = [Environment]::GetEnvironmentVariable($name); if ($v) { return $v } }
    if (Get-Command gh -ErrorAction SilentlyContinue) { $v = (gh auth token 2>$null); if ($LASTEXITCODE -eq 0 -and $v) { return $v.Trim() } }
    # The credential Git uses for github.com (Git Credential Manager); asked without prompting the user.
    $env:GIT_TERMINAL_PROMPT = '0'
    $answer = "protocol=https`nhost=github.com`n`n" | git credential fill 2>$null
    $line = @($answer | Where-Object { $_ -like 'password=*' })[0]
    if ($line) { return $line.Substring(9) }
    throw 'No GitHub credential: set GITHUB_TOKEN, pass -Token, install the GitHub CLI (gh auth login) or sign in to GitHub with Git once.'
}
$plain = Get-PlainToken
$headers = @{ Authorization = "Bearer $plain"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28'; 'User-Agent' = 'HvPortal-release-script' }
$plain = $null
$api = "https://api.github.com/repos/$Repository"
function Invoke-GitHub([string]$Method, [string]$Uri, $Body) {
    $p = @{ Method = $Method; Uri = $Uri; Headers = $headers }
    if ($null -ne $Body) { $p.Body = ($Body | ConvertTo-Json -Depth 5); $p.ContentType = 'application/json' }
    Invoke-RestMethod @p
}

$info = Invoke-GitHub GET $api
if (-not $info.private -and -not $AllowPublic) { throw "$Repository is PUBLIC: the setup files would be downloadable by anyone. Use -AllowPublic to publish anyway." }
Write-Host "Repository: $Repository ($(if ($info.private) { 'private' } else { 'public' })), commit $($commit.Substring(0, 12))$(if ($dirty) { ' + uncommitted changes' })"

$tag = "v$Version"
$sums = $files | ForEach-Object { '{0}  {1}' -f (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path $_ -Leaf) }
$signer = (Get-AuthenticodeSignature -LiteralPath $files[0]).SignerCertificate
$notes = @(
    "HvPortal $Version - graphical setups of the gateway and of the agent.",
    '',
    '```',
    $sums,
    '```',
    '',
    $(if ($signer) { "Signed by $($signer.Subject). A self-signed certificate is trusted by Windows only after importing installer\HvPortal-CodeSigning.cer (in the installation package) into Trusted Root Certification Authorities and Trusted Publishers." } else { 'NOT signed.' }),
    '',
    "Built from commit $commit$(if ($dirty) { ' with changes not yet committed: the tag does not contain them all' })."
) -join "`n"

# Piped, so that the JSON array is enumerated (wrapping it in @() would turn an empty list into one empty element).
$existing = @((Invoke-GitHub GET "$api/releases?per_page=100") | Where-Object { $_ -and $_.id })
if (-not $PSCmdlet.ShouldProcess("$Repository", "publish release $tag with $($files.Count) setup files and delete these releases and their tags: $((@($existing | ForEach-Object { "'$($_.tag_name)' ('$($_.name)'$(if ($_.draft) { ', draft' }), created $($_.created_at), files: $((@($_.assets) | ForEach-Object { "$($_.name) $([math]::Round($_.size / 1MB)) MB" }) -join ' + '))" }) -join '; '))")) { return }

# A release of the same version is replaced (its tag is moved to the current commit).
foreach ($r in @($existing | Where-Object tag_name -eq $tag)) {
    Invoke-GitHub DELETE "$api/releases/$($r.id)" | Out-Null
    try { Invoke-GitHub DELETE "$api/git/refs/tags/$tag" | Out-Null } catch { }
}

$new = Invoke-GitHub POST "$api/releases" @{ tag_name = $tag; target_commitish = $commit; name = "HvPortal $Version"; body = $notes; draft = $true; prerelease = $false }
try {
    foreach ($f in $files) {
        $name = Split-Path $f -Leaf
        Write-Host "Uploading $name ($([math]::Round((Get-Item $f).Length / 1MB)) MB)..."
        $asset = Invoke-RestMethod -Method POST -Uri "https://uploads.github.com/repos/$Repository/releases/$($new.id)/assets?name=$name" -Headers $headers `
            -ContentType 'application/octet-stream' -InFile $f -TimeoutSec 3600
        if ($asset.size -ne (Get-Item $f).Length) { throw "Upload of $name incomplete ($($asset.size) bytes)." }
    }
    $published = Invoke-GitHub PATCH "$api/releases/$($new.id)" @{ draft = $false; make_latest = 'true' }
} catch {
    Invoke-GitHub DELETE "$api/releases/$($new.id)" | Out-Null
    throw "Publication failed, the new release was removed and the previous ones are unchanged: $($_.Exception.Message)"
}

# Only the latest stays: other releases and their tags are deleted.
foreach ($r in @($existing | Where-Object { $_.tag_name -ne $tag })) {
    Invoke-GitHub DELETE "$api/releases/$($r.id)" | Out-Null
    # A draft has no tag yet (or a tag that another release may use): only tags of published releases are deleted.
    if ($r.draft -or -not $r.tag_name) { Write-Host "Removed draft release '$($r.name)'."; continue }
    try { Invoke-GitHub DELETE "$api/git/refs/tags/$([uri]::EscapeDataString($r.tag_name))" | Out-Null; Write-Host "Removed release and tag $($r.tag_name)." }
    catch { Write-Warning "Release $($r.tag_name) removed, its tag could not be deleted: $($_.Exception.Message)" }
}
Write-Host "Published: $($published.html_url)"
