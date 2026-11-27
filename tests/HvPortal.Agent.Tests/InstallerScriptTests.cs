using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace HvPortal.Agent.Tests;

/// <summary>
/// 0.9.6: the installer scripts on a server where a security product refuses programs started by a script
/// ("Program 'sc.exe' failed to run: Access is denied", reported from a real host). What an upgrade needs is done in
/// process and nothing is changed when the service is already registered; a Windows tool that cannot be started is told
/// apart from a tool that failed; a failure after the files were replaced puts the previous version back.
/// The scripts run in Windows PowerShell 5.1 as on a host; whatever needs administrator rights (services, ACLs, the
/// event log) is replaced by stand-ins, so nothing is installed. Not covered here: the real Service Control Manager
/// (docs/verification/real-host-test-plan.md, V95).
/// </summary>
public sealed class InstallerScriptTests
{
    private static readonly string InstallerDir = Path.Combine(AppContext.BaseDirectory, "Installer");

    private static readonly string[] ForbiddenInInstallers = ["Add-Type", "Invoke-Expression", "Start-Process", "[ScriptBlock]::Create"];

    private static readonly string[] CheckedScripts = ["Common.ps1", "Install-Agent.ps1", "Install-Gateway.ps1", "Uninstall-HvPortal.ps1"];

    private static readonly string[] InstallScripts = ["Install-Agent.ps1", "Install-Gateway.ps1"];

    /// <summary>Stand-ins for the service of a host; the state is the one of a service registered by an earlier installer.</summary>
    private const string Stubs = """
        $script:tools = New-Object System.Collections.Generic.List[string]
        $script:wmi = New-Object System.Collections.Generic.List[string]
        $script:created = New-Object System.Collections.Generic.List[string]
        $script:exists = $true
        $script:toolStarts = $false
        $script:wmiReturns = 0
        $script:refuseCredential = $false
        $script:state = [pscustomobject]@{ Path = '"C:\Program Files\HvPortal\Agent\HvPortal.Agent.exe"'; Account = 'NT Service\HvPortalAgent'; Start = 2; DelayedStart = $true; SidType = 1
            FailureResetSeconds = 86400; FailureActions = @('restart/10000', 'restart/30000', 'restart/60000'); FailureFlag = $true }
        function Get-Service { [CmdletBinding()] param([Parameter(Position = 0)]$Name) if ($script:exists) { [pscustomobject]@{ Name = $Name } } }
        function Get-HvPortalServiceState([string]$ServiceName) { if ($script:exists) { $script:state } }
        function Invoke-SystemTool([string]$Name, [string[]]$Arguments) {
            $script:tools.Add("$Name $($Arguments -join ' ')")
            if (-not $script:toolStarts) { return [pscustomobject]@{ Tool = $Name; Started = $false; ExitCode = $null; Lines = [string[]]@(); Output = ''; Reason = 'Access is denied' } }
            if ($Arguments -contains 'obj=') { $script:state.Account = $Arguments[[Array]::IndexOf($Arguments, 'obj=') + 1] }
            return [pscustomobject]@{ Tool = $Name; Started = $true; ExitCode = 0; Lines = [string[]]@(); Output = ''; Reason = '' }
        }
        function Get-CimInstance { [CmdletBinding()] param($ClassName, $Filter) [pscustomobject]@{ Name = 'HvPortalAgent' } }
        function Invoke-CimMethod {
            [CmdletBinding()] param($InputObject, $MethodName, $Arguments)
            $script:wmi.Add("$MethodName " + (($Arguments.Keys | Sort-Object | ForEach-Object { "$_=$($Arguments[$_])" }) -join ';'))
            if ($script:wmiReturns -eq 0 -and $Arguments.StartName) { $script:state.Account = $Arguments.StartName }
            [pscustomobject]@{ ReturnValue = $script:wmiReturns }
        }
        function New-Service {
            [CmdletBinding()] param($Name, $DisplayName, $BinaryPathName, $StartupType, $Description, $Credential)
            $script:created.Add($(if ($Credential) { "$($Credential.UserName)|$($Credential.Password.Length)" } else { 'LocalSystem' }))
            if ($Credential -and $script:refuseCredential) { throw 'The account name is invalid.' }
            $script:exists = $true
            $script:state.Account = if ($Credential) { $Credential.UserName } else { 'LocalSystem' }
        }
        function Register { Register-HvPortalService -ServiceName HvPortalAgent -DisplayName 'HvPortal Agent' -Description 'd' -Exe 'C:\Program Files\HvPortal\Agent\HvPortal.Agent.exe' @args }
        function Show([string]$Name) { "$Name tools=$($script:tools.Count) wmi=$($script:wmi.Count) created=$($script:created.Count)" }

        """;

    [Fact]
    public void A_windows_tool_that_cannot_be_started_is_not_a_tool_that_failed()
    {
        using var work = new WorkFolder();
        var blocked = Path.Combine(work.Path, "blocked.exe");
        File.WriteAllText(blocked, "not a program");
        // The real sc.exe (a read-only query of a service that does not exist), then a file Windows refuses to start.
        var (code, output) = RunWithCommon(work, """
            $r = Invoke-SystemTool 'sc.exe' @('query', 'HvPortalNoSuchService')
            "real started=$($r.Started) exit=$($r.ExitCode)"
            "path=" + (Get-SystemTool 'sc.exe')
            function Get-SystemTool([string]$Name) { return $env:HVP_TEST_BLOCKED }
            $r = Invoke-SystemTool 'sc.exe' @('config', 'HvPortalAgent', 'start=', 'delayed-auto')
            "refused started=$($r.Started) exit=[$($r.ExitCode)] reason=$([bool]$r.Reason)"
            try { Invoke-Sc @('config', 'HvPortalAgent', 'obj=', 'NT SERVICE\HvPortalAgent') } catch { "error=$($_.Exception.Message)" }
            """, ("HVP_TEST_BLOCKED", blocked));
        Assert.True(code == 0, output);
        Has(output, "real started=True exit=1060");
        // By full path: an elevated installer never runs what PATH finds first.
        Has(output.ToUpperInvariant(), ("path=" + Path.Combine(Environment.SystemDirectory, "sc.exe")).ToUpperInvariant());
        Has(output, "refused started=False exit=[] reason=True");
        // The message says what happened and gives the command, instead of the PowerShell stack of the report.
        Has(output, "error=Windows did not start sc.exe (");
        Has(output, "antivirus / EDR or application control rule");
        Has(output, "Command: sc.exe config HvPortalAgent obj= \"NT SERVICE\\HvPortalAgent\"");
    }

    [Fact]
    public void The_registration_of_a_service_is_read_from_its_registry_key()
    {
        using var work = new WorkFolder();
        var (code, output) = RunWithCommon(work, """
            # As written by "sc.exe failure ... reset= 86400 actions= restart/10000/restart/30000/restart/60000".
            $bytes = [byte[]](0x80,0x51,0x01,0x00, 0,0,0,0, 0,0,0,0, 3,0,0,0, 0x14,0,0,0, 1,0,0,0, 0x10,0x27,0,0, 1,0,0,0, 0x30,0x75,0,0, 1,0,0,0, 0x60,0xea,0,0)
            $f = ConvertFrom-ServiceFailureActions $bytes
            "ours=$($f.ResetSeconds):$($f.Actions -join ',')"
            # One restart, then nothing (as Windows writes for some of its own services).
            $f = ConvertFrom-ServiceFailureActions ([byte[]](0x10,0x0e,0,0, 0,0,0,0, 1,0,0,0, 2,0,0,0, 0x14,0,0,0, 1,0,0,0, 0x88,0x13,0,0, 0,0,0,0, 0,0,0,0))
            "other=$($f.ResetSeconds):$($f.Actions -join ',')"
            "short=" + ($null -eq (ConvertFrom-ServiceFailureActions ([byte[]](1,2,3))))
            "truncated=" + ($null -eq (ConvertFrom-ServiceFailureActions ([byte[]](0,0,0,0, 0,0,0,0, 0,0,0,0, 3,0,0,0, 0x14,0,0,0, 1,0,0,0))))
            "missing=" + ($null -eq (ConvertFrom-ServiceFailureActions $null))
            # A service every Windows has, read only.
            $s = Get-HvPortalServiceState 'EventLog'
            "eventlog=$([bool]$s.Path)|$([bool]$s.Account)|$($s.Start)"
            "unknown=" + ($null -eq (Get-HvPortalServiceState 'HvPortalNoSuchService'))
            """);
        Assert.True(code == 0, output);
        Has(output, "ours=86400:restart/10000,restart/30000,restart/60000");
        Has(output, "other=3600:restart/5000,none/0");
        Has(output, "short=True");
        Has(output, "truncated=True");
        Has(output, "missing=True");
        Has(output, "eventlog=True|True|2");
        Has(output, "unknown=True");
    }

    [Fact]
    public void Only_the_settings_that_differ_are_listed_as_to_change()
    {
        using var work = new WorkFolder();
        var (code, output) = RunWithCommon(work, Stubs + """
            $exe = 'C:\Program Files\HvPortal\Agent\HvPortal.Agent.exe'
            $virtual = 'NT SERVICE\HvPortalAgent'
            function Gaps($change, $account = $virtual) {
                $s = $script:state.PSObject.Copy()
                foreach ($k in $change.Keys) { $s.$k = $change[$k] }
                '[' + ((Get-ServiceRegistrationGaps $s $exe $account) -join ',') + ']'
            }
            "registered=" + (Gaps @{})
            "none=[" + ((Get-ServiceRegistrationGaps $null $exe $virtual) -join ',') + "]"
            "unquoted=" + (Gaps @{ Path = 'c:\program files\hvportal\agent\HVPORTAL.AGENT.EXE' })
            "moved=" + (Gaps @{ Path = '"D:\Old\HvPortal.Agent.exe"' })
            "localsystem=" + (Gaps @{ Account = 'LocalSystem' })
            "dedicated-kept=" + (Gaps @{ Account = '.\svc-hvportal' } '')
            "manual=" + (Gaps @{ Start = 3; DelayedStart = $false })
            "sid=" + (Gaps @{ SidType = 0 })
            "restart-twice=" + (Gaps @{ FailureActions = @('restart/10000', 'restart/30000') })
            "reset=" + (Gaps @{ FailureResetSeconds = 3600 })
            "flag=" + (Gaps @{ FailureFlag = $false })
            """);
        Assert.True(code == 0, output);
        Has(output, "registered=[]");
        Has(output, "none=[Path,Account,AutomaticStart,DelayedStart,SidType,FailureActions,FailureFlag]");
        // Quotes and case of the path are not a difference; the account is compared without case.
        Has(output, "unquoted=[]");
        Has(output, "moved=[Path]");
        Has(output, "localsystem=[Account]");
        // A dedicated local account is not looked at (-KeepLogon).
        Has(output, "dedicated-kept=[]");
        Has(output, "manual=[AutomaticStart,DelayedStart]");
        Has(output, "sid=[SidType]");
        Has(output, "restart-twice=[FailureActions]");
        Has(output, "reset=[FailureActions]");
        Has(output, "flag=[FailureFlag]");
    }

    [Fact]
    public void A_service_already_registered_is_not_touched_and_no_program_is_started()
    {
        using var work = new WorkFolder();
        var (code, output) = RunWithCommon(work, Stubs + """
            Register
            Show 'upgrade'
            $script:state.Account = '.\svc-hvportal'
            Register -KeepLogon
            Show 'dedicated'
            """);
        Assert.True(code == 0, output);
        Has(output, "Service HvPortalAgent is already registered as required: nothing to change.");
        Has(output, "upgrade tools=0 wmi=0 created=0");
        Has(output, "dedicated tools=0 wmi=0 created=0");
    }

    [Fact]
    public void Settings_only_sc_can_write_are_reported_not_fatal_when_sc_cannot_be_started()
    {
        string[] commands =
        [
            "sc.exe config HvPortalAgent start= delayed-auto", "sc.exe sidtype HvPortalAgent unrestricted",
            "sc.exe failure HvPortalAgent reset= 86400 actions= restart/10000/restart/30000/restart/60000", "sc.exe failureflag HvPortalAgent 1",
        ];
        using var work = new WorkFolder();
        var (code, output) = RunWithCommon(work, Stubs + """
            $script:state.DelayedStart = $false; $script:state.SidType = 0; $script:state.FailureActions = @(); $script:state.FailureResetSeconds = -1; $script:state.FailureFlag = $false
            $warnings = Register 3>&1
            Show 'refused'
            $warnings | ForEach-Object { "W> $_" }
            # sc.exe starts: every setting is written with the command of the releases before 0.9.6.
            $script:tools.Clear(); $script:toolStarts = $true
            $warnings = Register 3>&1
            Show 'allowed'
            "warnings=$(@($warnings | Where-Object { $_ }).Count)"
            $script:tools | ForEach-Object { "T> $_" }
            """);
        Assert.True(code == 0, output);
        // Asked once: the other three settings are not tried after the first refusal.
        Has(output, "refused tools=1 wmi=0 created=0");
        Has(output, "W> Windows did not start sc.exe (Access is denied).");
        Has(output, "Service HvPortalAgent: 4 recommended setting(s) not applied");
        Has(output, "The service works without them. To apply them, type in an elevated prompt: " + string.Join(" ", commands));
        Has(output, "allowed tools=4 wmi=0 created=0");
        Has(output, "warnings=0");
        foreach (var command in commands)
        {
            Has(output, "T> " + command);
        }
    }

    [Fact]
    public void Program_account_and_start_are_set_in_process_and_never_taken_on_trust()
    {
        using var work = new WorkFolder();
        var (code, output) = RunWithCommon(work, Stubs + """
            # A service left as LocalSystem (e.g. an installation finished by hand): WMI sets the account, sc.exe is not started.
            $script:state.Account = 'LocalSystem'
            Register
            Show 'wmi'; "wmi-account=$($script:state.Account)"; $script:wmi | ForEach-Object { "M> $_" }

            # WMI refuses (also with the empty password spelled out): sc.exe is the way of the earlier releases.
            $script:state.Account = 'LocalSystem'; $script:wmi.Clear(); $script:wmiReturns = 22; $script:toolStarts = $true
            Register
            Show 'sc'; "sc-account=$($script:state.Account)"; $script:tools | ForEach-Object { "T> $_" }

            # WMI refuses and sc.exe cannot be started: an error that names both, never a service left as LocalSystem in silence.
            $script:state.Account = 'LocalSystem'; $script:wmi.Clear(); $script:tools.Clear(); $script:toolStarts = $false
            try { Register; 'no error' } catch { "error=$($_.Exception.Message)" }

            # WMI answers 0 but the account is still the old one: the registration is read again, and refused.
            $script:wmiReturns = 0; $script:wmi.Clear(); $script:tools.Clear()
            function Invoke-CimMethod { [CmdletBinding()] param($InputObject, $MethodName, $Arguments) [pscustomobject]@{ ReturnValue = 0 } }
            try { Register; 'no error' } catch { "unchanged=$($_.Exception.Message)" }
            """);
        Assert.True(code == 0, output);
        Has(output, "wmi tools=0 wmi=1 created=0");
        Has(output, "wmi-account=NT SERVICE\\HvPortalAgent");
        Has(output, "M> Change StartName=NT SERVICE\\HvPortalAgent");
        Has(output, "sc tools=1 wmi=2 created=0");
        Has(output, "sc-account=NT SERVICE\\HvPortalAgent");
        Has(output, "T> sc.exe config HvPortalAgent obj= NT SERVICE\\HvPortalAgent");
        Has(output, "error=The service HvPortalAgent could not be configured (Account). WMI: Win32_Service.Change returned 22. sc.exe: Windows did not start sc.exe (Access is denied).");
        Has(output, "unchanged=The service HvPortalAgent is still not registered as required after the change: Account.");
        Assert.DoesNotContain("no error", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_new_service_is_created_with_its_virtual_account()
    {
        using var work = new WorkFolder();
        var (code, output) = RunWithCommon(work, Stubs + """
            $script:exists = $false; $script:toolStarts = $true
            Register
            Show 'new'; "new-as=$($script:created -join ',') account=$($script:state.Account)"

            # Windows refuses the account at creation: created as before 0.9.6, the account is set right after.
            $script:exists = $false; $script:created.Clear(); $script:wmi.Clear(); $script:tools.Clear(); $script:refuseCredential = $true
            Register
            Show 'fallback'; "fallback-as=$($script:created -join ',') account=$($script:state.Account)"
            """);
        Assert.True(code == 0, output);
        // No password, and never LocalSystem: nothing is left to set for the account.
        Has(output, "new tools=0 wmi=0 created=1");
        Has(output, "new-as=NT SERVICE\\HvPortalAgent|0 account=NT SERVICE\\HvPortalAgent");
        Has(output, "fallback tools=0 wmi=1 created=2");
        Has(output, "fallback-as=NT SERVICE\\HvPortalAgent|0,LocalSystem account=NT SERVICE\\HvPortalAgent");
    }

    [Fact]
    public void A_failed_upgrade_puts_the_previous_files_back_and_starts_the_service_without_hiding_the_error()
    {
        using var work = new WorkFolder();
        var install = Directory.CreateDirectory(Path.Combine(work.Path, "install")).FullName;
        var backup = Directory.CreateDirectory(Path.Combine(work.Path, "backup")).FullName;
        File.WriteAllText(Path.Combine(install, "new.txt"), "new");
        Directory.CreateDirectory(Path.Combine(backup, "Ops"));
        File.WriteAllText(Path.Combine(backup, "old.txt"), "old");
        File.WriteAllText(Path.Combine(backup, "Ops", "script.ps1"), "old");
        var (code, output) = RunWithCommon(work, """
            $script:started = 0
            $script:refuseStart = $false
            function Stop-HvPortalService([string]$ServiceName, [int]$Seconds = 120) { }
            function Start-Service { [CmdletBinding()] param($Name) $script:started++; if ($script:refuseStart) { throw 'The service did not respond.' } }
            $install = $env:HVP_TEST_INSTALL; $backup = $env:HVP_TEST_BACKUP
            function Files { (Get-ChildItem -LiteralPath $install -Recurse -File | ForEach-Object { $_.Name } | Sort-Object) -join ',' }

            # First installation: there is nothing to put back.
            Undo-HvPortalUpgrade -ServiceName HvPortalAgent -InstallPath $install -Backup $null
            "first started=$script:started files=$(Files)"

            try { throw 'the step that failed' } catch { $failure = $_; Undo-HvPortalUpgrade -ServiceName HvPortalAgent -InstallPath $install -Backup $backup 3>$null; "kept=$($failure.Exception.Message)" }
            "restored started=$script:started files=$(Files)"

            # An agent that was never enrolled is not started.
            Undo-HvPortalUpgrade -ServiceName HvPortalAgent -InstallPath $install -Backup $backup -Start $false 3>$null
            "not-enrolled started=$script:started"

            # The service does not start: reported, never thrown (the caller reports its own error).
            $script:refuseStart = $true
            $w = Undo-HvPortalUpgrade -ServiceName HvPortalAgent -InstallPath $install -Backup $backup 3>&1
            "no-start started=$script:started warned=$([bool](@($w) -match 'did not start'))"

            # The copy is missing: said so, and the installation folder is not emptied.
            $w = Undo-HvPortalUpgrade -ServiceName HvPortalAgent -InstallPath $install -Backup (Join-Path $backup 'missing') 3>&1
            "no-copy started=$script:started warned=$([bool](@($w) -match 'could NOT be put back')) files=$(Files)"
            """, ("HVP_TEST_INSTALL", install), ("HVP_TEST_BACKUP", backup));
        Assert.True(code == 0, output);
        Has(output, "first started=0 files=new.txt");
        Has(output, "kept=the step that failed");
        Has(output, "restored started=1 files=old.txt,script.ps1");
        Has(output, "not-enrolled started=1");
        Has(output, "no-start started=2 warned=True");
        Has(output, "no-copy started=2 warned=True files=old.txt,script.ps1");
    }

    [Fact]
    public void Group_membership_and_service_removal_use_cmdlets_and_wmi_before_any_windows_tool()
    {
        using var work = new WorkFolder();
        var (code, output) = RunWithCommon(work, """
            $script:tools = New-Object System.Collections.Generic.List[string]
            $script:cmdlet = 'ok'; $script:net = 'ok'; $script:listed = @(); $script:wmiReturns = 0
            function Get-LocalGroupName([string]$GroupSid) { 'Hyper-V Administrators' }
            function Fail($Cmdlet, [string]$Id, [string]$Text) { $Cmdlet.ThrowTerminatingError((New-Object System.Management.Automation.ErrorRecord (New-Object System.Exception $Text), $Id, 'InvalidOperation', $null)) }
            function Add-LocalGroupMember { [CmdletBinding()] param($SID, $Member) if ($script:cmdlet -eq 'exists') { Fail $PSCmdlet 'MemberExists' 'already a member' } elseif ($script:cmdlet -eq 'fail') { Fail $PSCmdlet 'Unexpected' 'Failed to compare two elements in the array.' } }
            function Remove-LocalGroupMember { [CmdletBinding()] param($SID, $Member) if ($script:cmdlet -eq 'exists') { Fail $PSCmdlet 'MemberNotFound' 'not a member' } elseif ($script:cmdlet -eq 'fail') { Fail $PSCmdlet 'Unexpected' 'Failed to compare two elements in the array.' } }
            function Invoke-SystemTool([string]$Name, [string[]]$Arguments) {
                $script:tools.Add("$Name $($Arguments -join ' ')")
                if ($script:net -eq 'refused') { return [pscustomobject]@{ Tool = $Name; Started = $false; ExitCode = $null; Lines = [string[]]@(); Output = ''; Reason = 'Access is denied' } }
                if ($Arguments.Count -eq 2) { return [pscustomobject]@{ Tool = $Name; Started = $true; ExitCode = 0; Lines = [string[]]$script:listed; Output = ''; Reason = '' } }
                return [pscustomobject]@{ Tool = $Name; Started = $true; ExitCode = $(if ($script:net -eq 'ok') { 0 } else { 2 }); Lines = [string[]]@(); Output = 'System error 1378'; Reason = '' }
            }
            function Get-CimInstance { [CmdletBinding()] param($ClassName, $Filter) [pscustomobject]@{ Name = 'HvPortalAgent' } }
            function Invoke-CimMethod { [CmdletBinding()] param($InputObject, $MethodName, $Arguments) [pscustomobject]@{ ReturnValue = $script:wmiReturns } }
            $hv = 'S-1-5-32-578'; $account = 'NT SERVICE\HvPortalAgent'
            function Add([string]$Name) { $script:tools.Clear(); try { Add-AccountToLocalGroup -GroupSid $hv -Account $account; "$Name tools=$($script:tools.Count)" } catch { "$Name tools=$($script:tools.Count) error=$($_.Exception.Message)" } }

            Add 'cmdlet'
            $script:cmdlet = 'exists'; Add 'member'
            $script:cmdlet = 'fail'; Add 'net'
            $script:net = 'error'; $script:listed = @('Members', '  NT SERVICE\HvPortalAgent  '); Add 'already'
            $script:listed = @('Members', 'Administrator'); Add 'missing'
            $script:net = 'refused'; Add 'refused'

            # Removing a membership is a clean-up: reported, never an error.
            $script:tools.Clear(); $script:cmdlet = 'ok'; Remove-AccountFromLocalGroup -GroupSid $hv -Account $account; "remove tools=$($script:tools.Count)"
            $script:cmdlet = 'exists'; Remove-AccountFromLocalGroup -GroupSid $hv -Account $account; "remove-absent tools=$($script:tools.Count)"
            $script:cmdlet = 'fail'; $w = Remove-AccountFromLocalGroup -GroupSid $hv -Account $account 3>&1; "remove-refused tools=$($script:tools.Count) W> $w"

            # The service: Win32_Service.Delete, sc.exe only when WMI refuses.
            $script:net = 'ok'
            foreach ($value in 0, 16, 5) { $script:tools.Clear(); $script:wmiReturns = $value; Remove-HvPortalService -ServiceName HvPortalAgent; "delete-$value tools=$($script:tools -join '|')" }
            $script:net = 'refused'; $script:tools.Clear()
            try { Remove-HvPortalService -ServiceName HvPortalAgent; 'no error' } catch { "delete-refused error=$($_.Exception.Message)" }
            """);
        Assert.True(code == 0, output);
        Has(output, "cmdlet tools=0");
        Has(output, "member tools=0");
        Has(output, "net tools=1");
        // net.exe says no, but its list shows the account: fine.
        Has(output, "already tools=2");
        Has(output, "missing tools=2 error=Could not add NT SERVICE\\HvPortalAgent to 'Hyper-V Administrators'. Add-LocalGroupMember: Failed to compare two elements in the array.. net.exe: System error 1378");
        Has(output, "refused tools=1 error=Could not add NT SERVICE\\HvPortalAgent to 'Hyper-V Administrators'. Add-LocalGroupMember: Failed to compare two elements in the array.. Windows did not start net.exe (Access is denied).");
        Has(output, "remove tools=0");
        Has(output, "remove-absent tools=0");
        Has(output, "remove-refused tools=1 W> NT SERVICE\\HvPortalAgent could not be removed from 'Hyper-V Administrators'. Windows did not start net.exe (Access is denied).");
        Has(output, "By hand: net.exe localgroup \"Hyper-V Administrators\" \"NT SERVICE\\HvPortalAgent\" /delete");
        // 0 and 16 (already marked for deletion): no tool; any other answer: sc.exe.
        Has(output, "delete-0 tools= delete-16 tools= delete-5 tools=sc.exe delete HvPortalAgent");
        Has(output, "delete-refused error=Windows did not start sc.exe (Access is denied).");
        Assert.DoesNotContain("no error", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The real Install-Agent.ps1 / Install-Gateway.ps1 as the updater task of the portal runs them (only -Source and the
    /// folders), on a server where no Windows tool can be started: the service is already registered, so the upgrade
    /// installs the new files and starts the service without starting any program of Windows.
    /// </summary>
    [Theory]
    [InlineData("Agent")]
    [InlineData("Gateway")]
    public void An_upgrade_from_the_portal_completes_where_no_windows_tool_can_be_started(string component)
    {
        using var work = new WorkFolder();
        var run = PrepareUpgrade(work, component, registered: true);
        var (code, output) = RunInstaller(work, component, run);
        Assert.True(code == 0, output);
        var calls = File.ReadAllLines(run.Calls);
        Assert.DoesNotContain(calls, c => c.StartsWith("tool ", StringComparison.Ordinal));
        Assert.DoesNotContain(calls, c => c.StartsWith("wmi ", StringComparison.Ordinal));
        Assert.DoesNotContain(calls, c => c.StartsWith("new-service", StringComparison.Ordinal));
        Assert.DoesNotContain(calls, c => c.StartsWith("add-member", StringComparison.Ordinal));
        Assert.Contains($"stop HvPortal{component}", calls);
        Assert.Contains($"run HvPortal{component}", calls);
        Has(output, $"Service HvPortal{component} is already registered as required: nothing to change.");
        Assert.Equal("new", File.ReadAllText(Path.Combine(run.Install, $"HvPortal.{component}.exe")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(Directory.GetDirectories(work.Path, "install.bak-*").Single(), $"HvPortal.{component}.exe")));
        if (component == "Agent")
        {
            // The status is shown by starting the agent executable: when Windows refuses it, the setup still succeeded.
            Has(output, "The agent runs, but its status could not be shown");
        }
    }

    /// <summary>
    /// Same upgrade, but the service is registered as LocalSystem and neither WMI nor sc.exe can change it: the installer
    /// stops with an error that says why, and the version that was running is back in place and started again.
    /// </summary>
    [Theory]
    [InlineData("Agent")]
    [InlineData("Gateway")]
    public void An_upgrade_that_cannot_register_the_service_leaves_the_previous_version_running(string component)
    {
        using var work = new WorkFolder();
        var run = PrepareUpgrade(work, component, registered: false);
        var (code, output) = RunInstaller(work, component, run);
        Assert.True(code != 0, output);
        Has(output, $"The service HvPortal{component} could not be configured (Account).");
        Has(output, "Windows did not start sc.exe (Access is denied).");
        Has(output, "The upgrade failed after the files were replaced: putting the previous version back");
        Has(output, $"The previous version was put back and HvPortal{component} runs again.");
        var calls = File.ReadAllLines(run.Calls);
        // The new version was never started; the previous one was, after its files were put back.
        Assert.DoesNotContain($"run HvPortal{component}", calls);
        Assert.Contains($"start-service HvPortal{component}", calls);
        Assert.Equal("old", File.ReadAllText(Path.Combine(run.Install, $"HvPortal.{component}.exe")));
    }

    [Fact]
    public void Installer_scripts_start_windows_tools_only_through_the_checked_helper()
    {
        var scripts = CheckedScripts.ToDictionary(n => n, n => File.ReadAllText(Path.Combine(InstallerDir, n)));
        foreach (var (name, text) in scripts)
        {
            // No "& sc.exe" / "& net.exe" by name (PATH), anywhere.
            Assert.False(Regex.IsMatch(text, @"&\s+(sc|net|secedit)\.exe", RegexOptions.IgnoreCase), name);
            // Nothing that compiles or starts something else to configure the service.
            foreach (var forbidden in ForbiddenInInstallers)
            {
                Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
            }
        }
        foreach (var name in InstallScripts)
        {
            // Everything between the replacement of the files and the start of the new version is undone on failure.
            var text = scripts[name];
            var replaced = text.IndexOf("$backup = Install-ServiceBinaries", StringComparison.Ordinal);
            var guarded = text.IndexOf("\ntry {", replaced, StringComparison.Ordinal);
            var register = text.IndexOf("Register-HvPortalService -ServiceName $service", StringComparison.Ordinal);
            var undo = text.IndexOf("Undo-HvPortalUpgrade -ServiceName $service -InstallPath $InstallPath -Backup $backup", StringComparison.Ordinal);
            var start = text.IndexOf("Wait-ServiceRunning $service", StringComparison.Ordinal);
            Assert.True(replaced > 0 && replaced < guarded && guarded < register && register < undo && undo < start, name);
            Assert.Equal(2, Regex.Count(text, @"Undo-HvPortalUpgrade -ServiceName \$service"));
            Assert.Equal(2, Regex.Count(text, @"throw \$failure"));
        }
        // The membership of "Hyper-V Administrators" goes through the cmdlets first, net.exe after.
        Assert.Contains("Add-AccountToLocalGroup -GroupSid $hvSid -Account $runAs", scripts["Install-Agent.ps1"], StringComparison.Ordinal);
        var common = scripts["Common.ps1"];
        var cmdlet = common.IndexOf("Add-LocalGroupMember -SID $GroupSid", StringComparison.Ordinal);
        var net = common.IndexOf("Invoke-SystemTool 'net.exe' @('localgroup', $group, $Account, '/add')", StringComparison.Ordinal);
        Assert.True(cmdlet > 0 && cmdlet < net);
        Assert.Contains("Remove-HvPortalService -ServiceName $service", scripts["Uninstall-HvPortal.ps1"], StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ helpers

    private sealed record UpgradeRun(string Installer, string Source, string Install, string Data, string Calls, bool Registered);

    /// <summary>
    /// A folder with the real installer script, an installation of the previous version and a Common.ps1 that loads the
    /// real one and then replaces what needs administrator rights. No Windows tool starts ("Access is denied") and WMI
    /// refuses every change.
    /// </summary>
    private static UpgradeRun PrepareUpgrade(WorkFolder work, string component, bool registered)
    {
        var installer = Directory.CreateDirectory(Path.Combine(work.Path, "installer")).FullName;
        var source = Directory.CreateDirectory(Path.Combine(work.Path, "source")).FullName;
        var install = Directory.CreateDirectory(Path.Combine(work.Path, "install")).FullName;
        var data = Directory.CreateDirectory(Path.Combine(work.Path, "data")).FullName;
        File.Copy(Path.Combine(InstallerDir, $"Install-{component}.ps1"), Path.Combine(installer, $"Install-{component}.ps1"));
        File.WriteAllText(Path.Combine(source, $"HvPortal.{component}.exe"), "new");
        File.WriteAllText(Path.Combine(install, $"HvPortal.{component}.exe"), "old");
        if (component == "Agent")
        {
            Directory.CreateDirectory(Path.Combine(source, "Ops"));
            File.WriteAllText(Path.Combine(source, "Ops", "HvPortal.Ops.ps1"), "# operations");
            File.WriteAllText(Path.Combine(data, "agent.json"), "{}");
        }
        var shim = ". '" + Path.Combine(InstallerDir, "Common.ps1").Replace("'", "''") + "'\r\n" + """
            function Note([string]$Text) { Add-Content -LiteralPath $env:HVP_TEST_CALLS -Value $Text }
            function Assert-Administrator { }
            function Get-Service { [CmdletBinding()] param([Parameter(Position = 0)]$Name) if ("$Name" -like 'HvPortal*') { [pscustomobject]@{ Name = $Name; Status = 'Running' } } }
            function Get-ServiceStartName([string]$ServiceName) { if ($env:HVP_TEST_REGISTERED -eq '1') { "NT SERVICE\$ServiceName" } else { 'LocalSystem' } }
            function Stop-HvPortalService([string]$ServiceName, [int]$Seconds = 120) { Note "stop $ServiceName" }
            function Get-HvPortalServiceState([string]$ServiceName) {
                [pscustomobject]@{ Path = "`"$env:HVP_TEST_EXE`""; Account = $(if ($env:HVP_TEST_REGISTERED -eq '1') { "NT SERVICE\$ServiceName" } else { 'LocalSystem' }); Start = 2; DelayedStart = $true; SidType = 1
                    FailureResetSeconds = 86400; FailureActions = @('restart/10000', 'restart/30000', 'restart/60000'); FailureFlag = $true }
            }
            function Invoke-SystemTool([string]$Name, [string[]]$Arguments) {
                Note "tool $Name $($Arguments -join ' ')"
                [pscustomobject]@{ Tool = $Name; Started = $false; ExitCode = $null; Lines = [string[]]@(); Output = ''; Reason = 'Access is denied' }
            }
            function Get-CimInstance { [CmdletBinding()] param($ClassName, $Filter) [pscustomobject]@{ Name = 'x' } }
            function Invoke-CimMethod { [CmdletBinding()] param($InputObject, $MethodName, $Arguments) Note "wmi $MethodName"; [pscustomobject]@{ ReturnValue = 22 } }
            function New-Service { [CmdletBinding()] param($Name, $DisplayName, $BinaryPathName, $StartupType, $Description, $Credential) Note 'new-service' }
            function Set-PrivateAcl([string]$Path, [string[]]$ExtraAccounts, [switch]$ReadOnlyForExtra) { }
            function Register-EventSource([string]$Source) { }
            function Set-ServiceEnvironment([string]$ServiceName, [hashtable]$Variables) { }
            function Unregister-AgentUpdater { }
            function Unregister-GatewayUpdater { }
            function Get-LocalGroupMember { [CmdletBinding()] param($SID) [pscustomobject]@{ Name = 'NT SERVICE\HvPortalAgent' } }
            function Add-LocalGroupMember { [CmdletBinding()] param($SID, $Member) Note 'add-member' }
            function Wait-ServiceRunning([string]$ServiceName, [int]$Seconds = 60) { Note "run $ServiceName" }
            function Start-Service { [CmdletBinding()] param($Name) Note "start-service $Name" }
            """;
        File.WriteAllText(Path.Combine(installer, "Common.ps1"), shim, new UTF8Encoding(true));
        var calls = Path.Combine(work.Path, "calls.log");
        File.WriteAllText(calls, "");
        return new UpgradeRun(installer, source, install, data, calls, registered);
    }

    private static (int Code, string Output) RunInstaller(WorkFolder work, string component, UpgradeRun run)
    {
        // -DisableRemoteUpdate: registering the updater task resolves the SID of the service account, which exists only
        // on a host where the service is installed. The task is registered through the ScheduledTasks cmdlets and COM
        // (no program started), so the question tested here is not affected.
        var arguments = new List<string> { "-Source", run.Source, "-InstallPath", run.Install, "-DataPath", run.Data, "-DisableRemoteUpdate" };
        if (component == "Gateway")
        {
            arguments.AddRange(["-UiPort", "58443", "-AgentPort", "58444"]);
        }
        return Run(work, Path.Combine(run.Installer, $"Install-{component}.ps1"), arguments,
            ("HVP_TEST_CALLS", run.Calls), ("HVP_TEST_EXE", Path.Combine(run.Install, $"HvPortal.{component}.exe")), ("HVP_TEST_REGISTERED", run.Registered ? "1" : "0"));
    }

    private static (int Code, string Output) RunWithCommon(WorkFolder work, string body, params (string Name, string Value)[] environment)
    {
        var script = Path.Combine(work.Path, "test-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(script, "$ErrorActionPreference = 'Stop'\r\n. '" + Path.Combine(InstallerDir, "Common.ps1").Replace("'", "''") + "'\r\n" + body + "\r\n", new UTF8Encoding(true));
        return Run(work, script, [], environment);
    }

    private static (int Code, string Output) Run(WorkFolder work, string script, IEnumerable<string> arguments, params (string Name, string Value)[] environment)
    {
        var ps = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        Assert.True(File.Exists(ps), ps);
        var psi = new ProcessStartInfo(ps)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = work.Path,
        };
        foreach (var a in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script }.Concat(arguments))
        {
            psi.ArgumentList.Add(a);
        }
        foreach (var (name, value) in environment)
        {
            psi.Environment[name] = value;
        }
        // As on a host: Windows PowerShell with its own modules. A test run started from PowerShell 7 would hand over
        // the module path of that version, and 5.1 then cannot load its own Microsoft.PowerShell.Security.
        psi.Environment.Remove("PSModulePath");
        // Errors are collected while the output is read, so that neither stream can fill up and block PowerShell.
        var errors = new StringBuilder();
        using var process = new Process { StartInfo = psi };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (errors)
                {
                    errors.AppendLine(e.Data);
                }
            }
        };
        process.Start();
        process.BeginErrorReadLine();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        lock (errors)
        {
            return (process.ExitCode, output + errors);
        }
    }

    /// <summary>
    /// The text is in the output. White space is not compared: Windows PowerShell wraps warnings and errors at the width
    /// of a console that is not there, in the middle of a sentence.
    /// </summary>
    private static void Has(string output, string expected)
    {
        static string Flat(string s) => Regex.Replace(s, @"\s+", "");
        Assert.True(Flat(output).Contains(Flat(expected), StringComparison.Ordinal), $"Not found: {expected}\n--- output ---\n{output}");
    }

    /// <summary>A folder next to the test binaries (programs in %TEMP% are refused by some antivirus products).</summary>
    private sealed class WorkFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateDirectory(System.IO.Path.Combine(AppContext.BaseDirectory, "installer-run-" + Guid.NewGuid().ToString("N"))).FullName;

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left next to the test binaries (a file still held for an instant by the antivirus).
            }
        }
    }
}
