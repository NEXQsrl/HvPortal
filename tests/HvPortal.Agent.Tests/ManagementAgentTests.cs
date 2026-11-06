using HvPortal.Agent.Channel;
using HvPortal.Agent.Commands;
using HvPortal.Agent.HyperV;
using HvPortal.Contracts.Agent;
using HvPortal.Contracts.HyperV;
using Microsoft.Extensions.Logging.Abstractions;

namespace HvPortal.Agent.Tests;

/// <summary>Management agent (protocol 6): remote target rules, command routing, path mapping, fixed remote launcher.</summary>
public class ManagementAgentTests
{
    private static RemoteTarget Target(Guid id, string computer = "hv01.lab.local", string user = "Administrator", string password = "p@ss") =>
        new() { HostId = id, ComputerName = computer, UserName = user, Password = password };

    [Theory]
    [InlineData("hv01", true)]
    [InlineData("hv01.lab.local", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("localhost", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.0.5", false)]
    [InlineData("fe80::1", false)]
    [InlineData(@"\\hv01\c$", false)]
    [InlineData("hv01:5985", false)]
    [InlineData("hv01/x", false)]
    [InlineData("-hv01", false)]
    [InlineData("", false)]
    public void Computer_names_are_dns_names_or_ipv4(string name, bool valid) => Assert.Equal(valid, RemoteHosts.IsValidComputerName(name));

    [Fact]
    public void User_names_are_qualified_for_local_accounts_and_ip_targets_need_an_explicit_host()
    {
        Assert.Equal(@"HV01\Administrator", RemoteHosts.QualifiedUserName("hv01.lab.local", "Administrator"));
        Assert.Equal(@"LAB\admin", RemoteHosts.QualifiedUserName("hv01", @"LAB\admin"));
        Assert.Equal("admin@lab.local", RemoteHosts.QualifiedUserName("hv01", "admin@lab.local"));
        Assert.Equal(("LAB", "admin"), RemoteHosts.SplitUserName(@"LAB\admin"));
        Assert.Equal(((string?)null, "admin@lab.local"), RemoteHosts.SplitUserName("admin@lab.local"));
        Assert.Equal("user_name_needs_host", RemoteHosts.Validate("10.0.0.5", "Administrator", "x", true));
        Assert.Null(RemoteHosts.Validate("10.0.0.5", @"HV01\Administrator", "x", true));
        Assert.Equal("invalid_user_name", RemoteHosts.Validate("hv01", "a;b", "x", true));
        Assert.Equal("invalid_password", RemoteHosts.Validate("hv01", "a", null, true));
        Assert.Null(RemoteHosts.Validate("hv01", "a", null, false));
        Assert.Throws<ArgumentException>(() => HostTarget.FromRemote(Target(Guid.NewGuid(), computer: "localhost")));
    }

    [Fact]
    public void Remote_target_never_prints_its_password()
    {
        var t = Target(Guid.NewGuid(), password: "TopSecret-123");
        Assert.DoesNotContain("TopSecret", t.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("TopSecret", new TargetsConfiguration { Targets = [t] }.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Host_and_management_roles_refuse_commands_for_the_other_kind()
    {
        var local = new WmiHyperVHost(NullLogger<WmiHyperVHost>.Instance);
        var executor = new CommandExecutor(local, NullLogger<CommandExecutor>.Instance);
        using var registry = new RemoteTargetRegistry(NullLoggerFactory.Instance);
        var hostA = Guid.NewGuid();
        registry.Apply(new TargetsConfiguration { Targets = [Target(hostA)] });

        var plain = new AgentCommand { CommandId = Guid.NewGuid(), Kind = AgentCommandKind.CollectInventory };
        var targeted = plain with { TargetHostId = hostA };

        Assert.Same(executor, GatewayChannelService.Route(AgentRole.Host, plain, executor, local, registry).Executor);
        Assert.NotNull(GatewayChannelService.Route(AgentRole.Host, targeted, executor, local, registry).Error);
        Assert.NotNull(GatewayChannelService.Route(AgentRole.Management, plain, executor, local, registry).Error);
        Assert.NotNull(GatewayChannelService.Route(AgentRole.Management, plain with { TargetHostId = Guid.NewGuid() }, executor, local, registry).Error);
        var routed = GatewayChannelService.Route(AgentRole.Management, targeted, executor, local, registry);
        Assert.Null(routed.Error);
        Assert.NotSame(executor, routed.Executor);
        Assert.Equal(hostA, routed.Target!.HostId);
        Assert.Equal(6, AgentProtocol.RequiredVersion(targeted));
    }

    [Fact]
    public void Registry_keeps_unchanged_targets_and_replaces_changed_credentials()
    {
        using var registry = new RemoteTargetRegistry(NullLoggerFactory.Instance);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        registry.Apply(new TargetsConfiguration { Targets = [Target(a), Target(b, "hv02")] });
        Assert.True(registry.TryGet(a, out var first));
        registry.Apply(new TargetsConfiguration { Targets = [Target(a), Target(b, "hv02", password: "changed")] });
        Assert.True(registry.TryGet(a, out var same));
        Assert.Same(first, same);
        Assert.True(registry.TryGet(b, out var bContext));
        Assert.True(bContext.Target.SameAs(Target(b, "hv02", password: "changed")));
        registry.Apply(new TargetsConfiguration { Targets = [Target(b, "hv02", password: "changed"), Target(Guid.NewGuid(), "bad host")] });
        Assert.False(registry.TryGet(a, out _));
        Assert.Single(registry.All);
        registry.Clear();
        Assert.Empty(registry.All);
    }

    [Fact]
    public void Remote_paths_map_to_administrative_shares_and_back()
    {
        using var target = HostTarget.FromRemote(Target(Guid.NewGuid(), "hv01.lab.local"));
        using var fs = new RemoteHostFileAccess(target);
        Assert.Equal(@"\\hv01.lab.local\D$\VMs\web01", fs.ToPhysical(@"d:\VMs\web01"));
        Assert.Equal(@"\\hv01.lab.local\D$\", fs.ToPhysical(@"D:\"));
        Assert.Equal(@"D:\VMs\web01", fs.ToHost(@"\\HV01.lab.local\D$\VMs\web01"));
        Assert.Equal(@"E:\", fs.ToHost(@"\\hv01.lab.local\e$\"));
        Assert.Throws<HostPathException>(() => fs.ToHost(@"\\other\D$\VMs"));
        Assert.Throws<HostPathException>(() => fs.ToHost(@"\\hv01.lab.local\share\VMs"));
        Assert.Throws<HostPathException>(() => fs.ToPhysical(@"\\x\y"));
        Assert.Equal(@"\\hv01.lab.local\root\cimv2", target.WmiPath(@"root\cimv2"));
        Assert.Equal(@"\\.\root\cimv2", HostTarget.Local.WmiPath(@"root\cimv2"));
        Assert.Equal(@"HV01\Administrator", target.UserName);
    }

    [Fact]
    public void Remote_launcher_runs_only_the_fixed_operations_script()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Ops", "HvPortal.Remote.ps1"));
        var code = string.Join('\n', System.Text.RegularExpressions.Regex.Replace(source, "(?s)<#.*?#>", "")
            .Split('\n').Where(l => !l.TrimStart().StartsWith('#')));
        foreach (var forbidden in new[] { "Invoke-Expression", "iex ", "Start-Process", "[ScriptBlock]::Create", "Add-Type", "-ScriptBlock", "-EncodedCommand",
                     "SkipCACheck", "SkipCNCheck", "New-PSSessionOption", "Set-Item", "TrustedHosts" })
        {
            Assert.DoesNotContain(forbidden, code, StringComparison.OrdinalIgnoreCase);
        }
        // Exactly one remote invocation, of the operations script file next to the launcher.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(code, "Invoke-Command", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        Assert.Contains("FilePath     = $opsScript", code, StringComparison.Ordinal);
        Assert.Contains("Join-Path -Path $PSScriptRoot -ChildPath 'HvPortal.Ops.ps1'", code, StringComparison.Ordinal);

        // The operations script accepts the request as its only parameter and still never evaluates input.
        var ops = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Ops", "HvPortal.Ops.ps1"));
        Assert.Contains("param([string]$RequestJson)", ops, StringComparison.Ordinal);
    }
}
