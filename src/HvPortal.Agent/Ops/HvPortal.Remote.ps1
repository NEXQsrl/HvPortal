#Requires -Version 5.1
<#
  HvPortal management agent: runs the fixed operations script (HvPortal.Ops.ps1, next to this file) on a REMOTE Hyper-V
  host over WinRM. Invoked ONLY by HvPortal.Agent: one JSON document on stdin -> one JSON response on stdout.
  Input: { target = { computerName, userName, password, useHttps }, request = "<the JSON request for HvPortal.Ops.ps1>" }
  Security rules:
   - the only remote code is the operations script file itself (Invoke-Command -FilePath, fixed path); the request is
     passed as a plain string argument, never evaluated;
   - the computer name is re-validated here; the password arrives on stdin (never on a command line, never logged)
     and is used only to build the PSCredential;
   - no Invoke-Expression, no script blocks built from input, no Start-Process, no session options that disable
     certificate checks.
#>
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$WarningPreference = 'SilentlyContinue'
$VerbosePreference = 'SilentlyContinue'
[Console]::InputEncoding = New-Object System.Text.UTF8Encoding $false
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false

function Write-Failure([string]$text) {
    $obj = @{ ok = $false; result = $null; error = $text; applied = @() }
    [Console]::Out.Write((ConvertTo-Json -InputObject $obj -Compress))
}

try {
    $raw = [Console]::In.ReadToEnd()
    if ($raw.Length -gt 131072) { throw 'Request too large.' }
    $in = ConvertFrom-Json -InputObject $raw
    $t = $in.target
    $computer = [string]$t.computerName
    if ($computer -notmatch '^[A-Za-z0-9][A-Za-z0-9.-]{0,252}$') { throw 'Invalid computer name.' }
    $user = [string]$t.userName
    if ([string]::IsNullOrWhiteSpace($user) -or $user.Length -gt 256) { throw 'Invalid user name.' }
    $request = [string]$in.request
    if ([string]::IsNullOrEmpty($request) -or $request.Length -gt 65536) { throw 'Invalid request.' }
    $secure = ConvertTo-SecureString -String ([string]$t.password) -AsPlainText -Force
    $credential = New-Object System.Management.Automation.PSCredential -ArgumentList $user, $secure
    $opsScript = Join-Path -Path $PSScriptRoot -ChildPath 'HvPortal.Ops.ps1'

    $params = @{
        ComputerName = $computer
        Credential   = $credential
        FilePath     = $opsScript
        ArgumentList = @($request)
        ErrorAction  = 'Stop'
    }
    if ([bool]$t.useHttps) { $params.UseSSL = $true }
    $output = @(Invoke-Command @params)
} catch {
    # Connection, authentication or transport failure: the operations script did not run (or its outcome is unknown when
    # the session dropped mid-way, which the message says).
    Write-Failure ("remote_error: " + $_.Exception.Message)
    exit 0
}

$text = ($output | Where-Object { $_ -is [string] }) -join ''
if ([string]::IsNullOrWhiteSpace($text)) {
    Write-Failure 'remote_error: no response from the operations script on the remote host (outcome unknown).'
} else {
    [Console]::Out.Write($text)
}
