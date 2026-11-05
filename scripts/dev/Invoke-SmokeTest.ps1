#Requires -Version 7.0
<#
.SYNOPSIS
  End-to-end smoke test against a running gateway (fresh data directory) and a locally built agent.
.DESCRIPTION
  Exercises the real HTTP surface: setup with the one-time token, MFA restriction, TOTP enrollment,
  TOTP replay rejection, CSRF and Origin enforcement, port separation, host creation, agent enrollment over
  pinned TLS, agent mTLS channel, inventory reporting, audit chain verification.
  It does NOT need Hyper-V rights: on a machine where the agent identity lacks them, the expected
  inventory access status is AccessDenied (this is verified, not hidden).
#>
param(
    [string]$GatewayDataDirectory = (Join-Path $env:LOCALAPPDATA 'HvPortal\dev-gateway'),
    [string]$AgentDataDirectory = (Join-Path $env:LOCALAPPDATA 'HvPortal\dev-agent'),
    [int]$UiPort = 18443,
    [int]$AgentPort = 18444,
    [int]$AgentSeconds = 20,
    [string]$AgentExe
)
$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$ui = "https://localhost:$UiPort"
$results = [System.Collections.Generic.List[object]]::new()
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    $results.Add([pscustomobject]@{ Test = $name; Result = $(if ($ok) { 'PASS' } else { 'FAIL' }); Detail = $detail })
}
function Call([string]$method, [string]$path, $body = $null, [hashtable]$headers = @{}, [string]$base = $ui) {
    $p = @{ Method = $method; Uri = "$base$path"; WebSession = $script:session; SkipCertificateCheck = $true; SkipHttpErrorCheck = $true; Headers = $headers }
    if ($null -ne $body) { $p.Body = ($body | ConvertTo-Json -Depth 8); $p.ContentType = 'application/json' }
    $r = Invoke-WebRequest @p
    $script:session.Headers.Clear()   # WebRequestSession would otherwise replay these headers on every later call
    $text = if ($r.Content -is [byte[]]) { [System.Text.Encoding]::UTF8.GetString($r.Content) } else { [string]$r.Content }
    $json = $null
    if ($text -and "$($r.Headers['Content-Type'])" -match 'json') { $json = $text | ConvertFrom-Json }
    [pscustomobject]@{ Status = [int]$r.StatusCode; Json = $json; Raw = $text }
}
function Csrf { (Call GET '/api/v1/auth/csrf').Json.token }
function Totp([string]$base32, [int64]$offsetSteps = 0) {
    $alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'; $bits = 0; $val = 0; $out = [System.Collections.Generic.List[byte]]::new()
    foreach ($c in $base32.ToUpperInvariant().ToCharArray()) { $val = ($val -shl 5) -bor $alphabet.IndexOf($c); $bits += 5; if ($bits -ge 8) { $out.Add([byte](($val -shr ($bits - 8)) -band 0xFF)); $bits -= 8 } }
    $step = [int64][Math]::Floor([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() / 30) + $offsetSteps
    $counter = [BitConverter]::GetBytes($step); [Array]::Reverse($counter)
    $h = [System.Security.Cryptography.HMACSHA1]::new($out.ToArray()).ComputeHash($counter)
    $o = $h[19] -band 0x0F
    $bin = (([int64]$h[$o] -band 0x7F) -shl 24) -bor ([int64]$h[$o + 1] -shl 16) -bor ([int64]$h[$o + 2] -shl 8) -bor [int64]$h[$o + 3]
    ($bin % 1000000).ToString('D6')
}

trap {
    $results | Format-Table -AutoSize | Out-String -Width 250 | Write-Host
    Write-Host "Aborted: $($_.Exception.Message) at line $($_.InvocationInfo.ScriptLineNumber)"
    exit 99
}
$script:session = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
$password = 'correct staple orbit lantern ' + [guid]::NewGuid().ToString('N').Substring(0, 8)

# 1. setup state, no default credentials
$state = Call GET '/api/v1/auth/state'
Check 'setup required on fresh install' ($state.Json.setupRequired -eq $true)
$tokenFile = Join-Path $GatewayDataDirectory 'bootstrap\setup-token.txt'
$setupToken = (Get-Content -LiteralPath $tokenFile)[1].Trim()
$acl = (Get-Acl -LiteralPath $tokenFile).Access | ForEach-Object { $_.IdentityReference.Value } | Sort-Object -Unique
Check 'setup token file ACL is restricted' (-not ($acl -match 'Everyone|Users|Authenticated')) ($acl -join ', ')

# 2. CSRF + Origin enforcement
$noCsrf = Call POST '/api/v1/setup' @{ setupToken = 'x'; userName = 'admin'; password = 'x' }
Check 'POST without anti-forgery token rejected' ($noCsrf.Status -eq 400 -and $noCsrf.Json.code -eq 'csrf_invalid') "$($noCsrf.Status)"
$t = Csrf
$badOrigin = Call POST '/api/v1/setup' @{ setupToken = 'x'; userName = 'admin'; password = 'x' } @{ 'X-CSRF-TOKEN' = $t; Origin = 'https://evil.example' }
Check 'cross-origin request rejected' ($badOrigin.Status -eq 403 -and $badOrigin.Json.code -eq 'origin_mismatch') "$($badOrigin.Status)"
$wrongToken = Call POST '/api/v1/setup' @{ setupToken = 'wrong'; userName = 'admin'; password = $password } @{ 'X-CSRF-TOKEN' = $t }
Check 'wrong setup token rejected' ($wrongToken.Status -eq 400) "$($wrongToken.Status)"
$weak = Call POST '/api/v1/setup' @{ setupToken = $setupToken; userName = 'admin'; password = 'passwordpassword' } @{ 'X-CSRF-TOKEN' = $t }
Check 'common password rejected' ($weak.Status -eq 400 -and $weak.Json.code -eq 'password_common') "$($weak.Json.code)"

# 3. setup -> session restricted to MFA enrollment (admins must use MFA by default)
$setup = Call POST '/api/v1/setup' @{ setupToken = $setupToken; userName = 'admin'; displayName = 'Admin'; password = $password } @{ 'X-CSRF-TOKEN' = $t }
Check 'setup completes' ($setup.Status -eq 200) "$($setup.Status) $($setup.Raw)"
Check 'setup token file removed' (-not (Test-Path -LiteralPath $tokenFile))
$again = Call POST '/api/v1/setup' @{ setupToken = $setupToken; userName = 'admin2'; password = $password } @{ 'X-CSRF-TOKEN' = (Csrf) }
Check 'setup cannot run twice' ($again.Status -eq 409) "$($again.Status)"
$me = Call GET '/api/v1/auth/me'
Check 'session restricted to MFA enrollment' ($me.Json.restriction -eq 'mfa_enrollment') "$($me.Json.restriction)"
$blocked = Call GET '/api/v1/vms'
Check 'management API blocked until MFA enrolled' ($blocked.Status -eq 403 -and $blocked.Json.code -eq 'mfa_enrollment_required') "$($blocked.Status) $($blocked.Json.code)"

# 4. TOTP enrollment
$t = Csrf
$enroll = Call POST '/api/v1/mfa/totp/enroll' $null @{ 'X-CSRF-TOKEN' = $t }
Check 'TOTP enrollment started (local QR as data URI)' ($enroll.Status -eq 200 -and $enroll.Json.qrSvgDataUri -like 'data:image/svg+xml;base64,*') "$($enroll.Status)"
$secret = $enroll.Json.secret
$badConfirm = Call POST '/api/v1/mfa/totp/confirm' @{ code = '000000' } @{ 'X-CSRF-TOKEN' = $t }
Check 'wrong first TOTP code rejected' ($badConfirm.Status -eq 400)
$code = Totp $secret
$confirm = Call POST '/api/v1/mfa/totp/confirm' @{ code = $code } @{ 'X-CSRF-TOKEN' = $t }
Check 'TOTP confirmed, 10 recovery codes returned' ($confirm.Status -eq 200 -and $confirm.Json.recoveryCodes.Count -eq 10) "$($confirm.Status)"
$me = Call GET '/api/v1/auth/me'
Check 'full session after enrollment' ($null -eq $me.Json.restriction -and $me.Json.mfaEnabled) "$($me.Json.restriction)"

# 5. logout + login with password + TOTP, replay of the same code rejected
$t = Csrf
Call POST '/api/v1/auth/logout' $null @{ 'X-CSRF-TOKEN' = $t } | Out-Null
$afterLogout = Call GET '/api/v1/auth/me'
Check 'session revoked on logout' ($afterLogout.Status -eq 401) "$($afterLogout.Status)"
$t = Csrf
$badLogin = Call POST '/api/v1/auth/login' @{ userName = 'admin'; password = 'wrong wrong wrong wrong' } @{ 'X-CSRF-TOKEN' = $t }
$unknown = Call POST '/api/v1/auth/login' @{ userName = 'nobody-here'; password = 'wrong wrong wrong wrong' } @{ 'X-CSRF-TOKEN' = $t }
Check 'login errors do not reveal user existence' ($badLogin.Status -eq $unknown.Status -and $badLogin.Json.code -eq $unknown.Json.code) "$($badLogin.Json.code) / $($unknown.Json.code)"
$login = Call POST '/api/v1/auth/login' @{ userName = 'admin'; password = $password } @{ 'X-CSRF-TOKEN' = $t }
Check 'password step requires MFA' ($login.Json.status -eq 'mfa_required') "$($login.Raw)"
$replay = Call POST '/api/v1/auth/login/mfa' @{ challenge = $login.Json.challenge; code = $code } @{ 'X-CSRF-TOKEN' = $t }
Check 'TOTP replay (code already used) rejected' ($replay.Status -eq 401) "$($replay.Status)"
$recovery = $confirm.Json.recoveryCodes[0]
$mfa = Call POST '/api/v1/auth/login/mfa' @{ challenge = $login.Json.challenge; recoveryCode = $recovery } @{ 'X-CSRF-TOKEN' = $t }
Check 'login with recovery code' ($mfa.Status -eq 200 -and $mfa.Json.recoveryCodesRemaining -eq 9) "$($mfa.Status) $($mfa.Raw)"
$t = Csrf
Call POST '/api/v1/auth/logout' $null @{ 'X-CSRF-TOKEN' = $t } | Out-Null
$t = Csrf
$login2 = Call POST '/api/v1/auth/login' @{ userName = 'admin'; password = $password } @{ 'X-CSRF-TOKEN' = $t }
$reuse = Call POST '/api/v1/auth/login/mfa' @{ challenge = $login2.Json.challenge; recoveryCode = $recovery } @{ 'X-CSRF-TOKEN' = $t }
Check 'recovery code is single use' ($reuse.Status -eq 401) "$($reuse.Status)"
$mfa2 = Call POST '/api/v1/auth/login/mfa' @{ challenge = $login2.Json.challenge; recoveryCode = $confirm.Json.recoveryCodes[1] } @{ 'X-CSRF-TOKEN' = $t }
Check 'second recovery code works' ($mfa2.Status -eq 200)

# 6. port separation
$agentOnUi = Invoke-WebRequest -Uri "$ui/agent/v1/ca" -SkipCertificateCheck -SkipHttpErrorCheck
$apiOnAgent = Invoke-WebRequest -Uri "https://localhost:$AgentPort/api/v1/auth/state" -SkipCertificateCheck -SkipHttpErrorCheck
Check 'agent endpoints not served on UI port' ([int]$agentOnUi.StatusCode -eq 404)
Check 'UI/API not served on agent port' ([int]$apiOnAgent.StatusCode -eq 404)

# 7. create host -> enrollment token
$t = Csrf
$hostResp = Call POST '/api/v1/hosts' @{ displayName = 'local-dev-host' } @{ 'X-CSRF-TOKEN' = $t }
Check 'host created with one-time enrollment token' ($hostResp.Status -eq 200 -and $hostResp.Json.enrollment.token) "$($hostResp.Status) $($hostResp.Json.code)"
$enrollToken = $hostResp.Json.enrollment.token
$fp = $hostResp.Json.enrollment.caFingerprintSha256

# 8. agent enrollment (pinned CA) and token single use
$agentExe = if ($AgentExe) { $AgentExe } else { Join-Path $repo 'src\HvPortal.Agent\bin\Debug\net10.0-windows10.0.17763.0\HvPortal.Agent.exe' }
if (Test-Path -LiteralPath $AgentDataDirectory) { Remove-Item -LiteralPath $AgentDataDirectory -Recurse -Force }
$env:HVP_AGENT_DATA_DIR = $AgentDataDirectory
$env:HVP_ENROLLMENT_TOKEN = $enrollToken
$wrongPin = & $agentExe enroll --gateway "https://localhost:$AgentPort" --ca-sha256 ('00' * 32) 2>&1
Check 'enrollment refused with wrong CA fingerprint' ($LASTEXITCODE -ne 0 -and ($wrongPin -join ' ') -match 'does not match') ($wrongPin -join ' ')
$enrollOut = & $agentExe enroll --gateway "https://localhost:$AgentPort" --ca-sha256 $fp 2>&1
Check 'agent enrolled' ($LASTEXITCODE -eq 0) ($enrollOut -join ' ')
$reuseOut = & $agentExe enroll --gateway "https://localhost:$AgentPort" --ca-sha256 $fp --force 2>&1
Check 'enrollment token cannot be reused' ($LASTEXITCODE -ne 0) ($reuseOut -join ' ')
Remove-Item Env:\HVP_ENROLLMENT_TOKEN

# 9. agent channel (mTLS) + inventory
$agentProc = Start-Process -FilePath $agentExe -ArgumentList 'run' -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $env:TEMP 'hvp-agent-smoke.log')
Start-Sleep -Seconds $AgentSeconds
$hosts = Call GET '/api/v1/hosts'
$h = $hosts.Json | Where-Object { $_.displayName -eq 'local-dev-host' }
Check 'agent connected over mTLS' ($h.connected -eq $true) "connected=$($h.connected)"
Check 'inventory received from agent' ($null -ne $h.lastInventoryAt) "access=$($h.access) message=$($h.accessMessage)"
Check 'Hyper-V access status reported truthfully' ($h.access -in @('Ok', 'AccessDenied', 'NotInstalled', 'ServiceStopped')) "$($h.access)"

# 9b. typed operations travel gateway -> agent -> fixed PowerShell script and back (no Hyper-V rights needed to verify the path)
$defaults = Call GET "/api/v1/hosts/$($h.id)/defaults"
if ($h.access -eq 'Ok') {
    Check 'host defaults read through the operations script' ($defaults.Status -eq 200 -and $defaults.Json.defaults.logicalProcessorCount -gt 0) "$($defaults.Status)"
} else {
    Check 'operations script reports Hyper-V errors (not a fake success)' ($defaults.Status -eq 502 -and $defaults.Json.code -eq 'host_operation_failed') "$($defaults.Status) $($defaults.Json.code) $($defaults.Json.detail)"
}
$noVm = Call POST "/api/v1/vms/$([guid]::NewGuid())/console/tickets" $null @{ 'X-CSRF-TOKEN' = (Csrf) }
Check 'console ticket for unknown VM is 404' ($noVm.Status -eq 404) "$($noVm.Status)"
$t = Csrf
$unc = Call PUT "/api/v1/hosts/$($h.id)/iso-library" @{ paths = @('\\server\share') } @{ 'X-CSRF-TOKEN' = $t }
Check 'ISO library refuses UNC paths' ($unc.Status -eq 400 -and $unc.Json.code -eq 'invalid_iso_library') "$($unc.Status)"
$lib = Call PUT "/api/v1/hosts/$($h.id)/iso-library" @{ paths = @('D:\ISO') } @{ 'X-CSRF-TOKEN' = $t }
Check 'ISO library accepts local folders' ($lib.Status -eq 200) "$($lib.Status)"

# 9c. groups: tree rules
$g1 = Call POST '/api/v1/groups' @{ name = 'Production' } @{ 'X-CSRF-TOKEN' = $t }
$g2 = Call POST '/api/v1/groups' @{ name = 'Web'; parentId = $g1.Json.id } @{ 'X-CSRF-TOKEN' = $t }
Check 'nested groups created' ($g1.Status -eq 200 -and $g2.Status -eq 200) "$($g1.Status) $($g2.Status)"
$cycle = Call PATCH "/api/v1/groups/$($g1.Json.id)" @{ parentId = $g2.Json.id } @{ 'X-CSRF-TOKEN' = $t }
Check 'moving a group below itself is refused' ($cycle.Status -eq 400 -and $cycle.Json.code -eq 'invalid_parent') "$($cycle.Status) $($cycle.Json.code)"
$notEmpty = Call DELETE "/api/v1/groups/$($g1.Json.id)" $null @{ 'X-CSRF-TOKEN' = $t }
Check 'group with subgroups cannot be deleted' ($notEmpty.Status -eq 409) "$($notEmpty.Status)"
$list = Call GET '/api/v1/groups'
Check 'group tree listed with parent links' (@($list.Json.groups | Where-Object { $_.parentId -eq $g1.Json.id }).Count -eq 1)
$d2 = Call DELETE "/api/v1/groups/$($g2.Json.id)" $null @{ 'X-CSRF-TOKEN' = $t }
$d1 = Call DELETE "/api/v1/groups/$($g1.Json.id)" $null @{ 'X-CSRF-TOKEN' = $t }
Check 'empty groups deleted' ($d2.Status -eq 204 -and $d1.Status -eq 204) "$($d2.Status) $($d1.Status)"

# 9d. SPA shell: per-response style nonce, scripts without nonce
$shell = Invoke-WebRequest -Uri "$ui/" -SkipCertificateCheck -SkipHttpErrorCheck
$csp = "$($shell.Headers['Content-Security-Policy'])"
$nonce = if ($csp -match "'nonce-([^']+)'") { $Matches[1] } else { '' }
Check 'CSP has script-src self only and a style nonce' ($csp -match "script-src 'self';" -and $nonce.Length -ge 16 -and $csp -notmatch 'unsafe') $csp
Check 'SPA shell carries the same nonce' ($shell.Content -match [regex]::Escape("content=`"$nonce`"") -and $shell.Content -notmatch '__CSP_NONCE__')

# 10. revocation disconnects the agent
$t = Csrf
$rev = Call POST "/api/v1/hosts/$($h.id)/revoke" $null @{ 'X-CSRF-TOKEN' = $t }
Start-Sleep -Seconds 3
$h2 = (Call GET "/api/v1/hosts/$($h.id)").Json.host
Check 'host revocation disconnects agent' ($rev.Status -eq 204 -and $h2.connected -eq $false) "status=$($rev.Status) connected=$($h2.connected)"
Start-Sleep -Seconds 5
$h3 = (Call GET "/api/v1/hosts/$($h.id)").Json.host
Check 'revoked agent cannot reconnect' ($h3.connected -eq $false)
Stop-Process -Id $agentProc.Id -Force -ErrorAction SilentlyContinue

# 11. audit chain
$t = Csrf
$verify = Call POST '/api/v1/audit/verify' $null @{ 'X-CSRF-TOKEN' = $t }
Check 'audit hash chain verifies' ($verify.Json.valid -eq $true) "entries=$($verify.Json.entriesChecked)"
$denied = (Call GET '/api/v1/audit?outcome=Denied&take=100').Json.items | Select-Object -ExpandProperty action -Unique
Check 'denied actions are audited' ($denied -contains 'agent.connect' -or $denied -contains 'agent.enroll') ($denied -join ', ')

$results | Format-Table -AutoSize
$failed = @($results | Where-Object Result -eq 'FAIL').Count
Write-Host ("{0} passed, {1} failed" -f ($results.Count - $failed), $failed)
exit $failed
