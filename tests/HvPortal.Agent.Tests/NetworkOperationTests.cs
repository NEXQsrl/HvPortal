using HvPortal.Agent.Commands;
using HvPortal.Agent.HyperV;
using HvPortal.Contracts.Agent;
using HvPortal.Contracts.HyperV;

namespace HvPortal.Agent.Tests;

/// <summary>Protocol 7: virtual switches, folder of a new disk, checkpoint apply on a running VM (validation and script rules).</summary>
public class NetworkOperationTests
{
    private static string Script()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Ops", "HvPortal.Ops.ps1"));
        return string.Join('\n', System.Text.RegularExpressions.Regex.Replace(source, "(?s)<#.*?#>", "").Split('\n').Where(l => !l.TrimStart().StartsWith('#')));
    }

    [Fact]
    public void New_switch_arguments_are_validated_before_powershell_runs()
    {
        string? Check(NewSwitchRequest r) => PowerShellOperationRunner.Validate(HvOperation.NewSwitch, HvOperations.ToElement(r));
        Assert.Null(Check(new NewSwitchRequest("Tenant A (lab)", SwitchTypes.Private, null, false, null)));
        Assert.Null(Check(new NewSwitchRequest("Uplink", SwitchTypes.External, "Ethernet 2", true, "LAN")));
        Assert.Null(Check(new NewSwitchRequest("Mgmt", SwitchTypes.Internal, null, false, null)));
        Assert.Equal("invalid_switch_name", Check(new NewSwitchRequest("x'; Remove-VMSwitch *", SwitchTypes.Private, null, false, null)));
        Assert.Equal("invalid_switch_name", Check(new NewSwitchRequest("", SwitchTypes.Private, null, false, null)));
        Assert.Equal("invalid_switch_name", Check(new NewSwitchRequest(" padded", SwitchTypes.Private, null, false, null)));
        Assert.Equal("invalid_switch_name", Check(new NewSwitchRequest(new string('a', 65), SwitchTypes.Private, null, false, null)));
        Assert.Equal("invalid_switch_type", Check(new NewSwitchRequest("net", "Bridged", null, false, null)));
        Assert.Equal("invalid_net_adapter", Check(new NewSwitchRequest("net", SwitchTypes.External, null, true, null)));
        Assert.Equal("invalid_net_adapter", Check(new NewSwitchRequest("net", SwitchTypes.External, "Eth\u0000ernet", true, null)));
        Assert.Equal("net_adapter_only_for_external", Check(new NewSwitchRequest("net", SwitchTypes.Private, "Ethernet", false, null)));
        Assert.Equal("net_adapter_only_for_external", Check(new NewSwitchRequest("net", SwitchTypes.Internal, null, true, null)));
        Assert.Equal("notes_too_long", Check(new NewSwitchRequest("net", SwitchTypes.Private, null, false, new string('n', 1025))));

        Assert.Null(PowerShellOperationRunner.Validate(HvOperation.RemoveSwitch, HvOperations.ToElement(new SwitchRef(Guid.NewGuid().ToString("D"), "net"))));
        Assert.Equal("invalid_switch", PowerShellOperationRunner.Validate(HvOperation.RemoveSwitch, HvOperations.ToElement(new SwitchRef("not-a-guid", "net"))));
        Assert.Equal("invalid_switch", PowerShellOperationRunner.Validate(HvOperation.RemoveSwitch, HvOperations.ToElement(new SwitchRef(Guid.Empty.ToString("D"), "net"))));
        Assert.Null(PowerShellOperationRunner.Validate(HvOperation.ListNetAdapters, null));
    }

    [Fact]
    public void New_disk_folder_follows_the_location_rules()
    {
        var vm = Guid.NewGuid();
        string? Check(string? folder) => PowerShellOperationRunner.Validate(HvOperation.SetVmConfig,
            HvOperations.ToElement(new VmConfigChange { VmId = vm, NewDisk = new NewDiskSpec(10, false, folder) }));
        Assert.Null(Check(null));
        Assert.Equal("location_not_allowed", Check(@"C:\Windows\Temp"));
        Assert.Equal("location_not_allowed", Check(@"C:\Users\Public"));
        Assert.Equal("invalid_location", Check(@"\\nas\share\disks"));
        Assert.Equal("invalid_location", Check(@"D:\VMs\..\Windows"));
    }

    [Fact]
    public void Protocol_7_is_required_for_switch_operations_and_for_a_disk_folder()
    {
        AgentCommand Op(HvOperation op, object? args = null) =>
            new() { CommandId = Guid.NewGuid(), Kind = AgentCommandKind.HyperVOperation, Operation = op, OperationArgs = args is null ? null : HvOperations.ToElement(args) };
        Assert.Equal(10, AgentProtocol.Version);
        Assert.Equal(7, AgentProtocol.RequiredVersion(Op(HvOperation.ListNetAdapters)));
        Assert.Equal(7, AgentProtocol.RequiredVersion(Op(HvOperation.NewSwitch)));
        Assert.Equal(7, AgentProtocol.RequiredVersion(Op(HvOperation.RemoveSwitch)));
        var vm = Guid.NewGuid();
        // A disk in the folder chosen by the gateway must never reach an agent that would ignore the folder.
        Assert.Equal(7, AgentProtocol.RequiredVersion(Op(HvOperation.SetVmConfig, new VmConfigChange { VmId = vm, NewDisk = new NewDiskSpec(10, false, @"D:\Tenants\A\vm\Virtual Hard Disks") })));
        Assert.Equal(2, AgentProtocol.RequiredVersion(Op(HvOperation.SetVmConfig, new VmConfigChange { VmId = vm, NewDisk = new NewDiskSpec(10, false) })));
        Assert.Equal(2, AgentProtocol.RequiredVersion(Op(HvOperation.SetVmConfig, new VmConfigChange { VmId = vm, ProcessorCount = 2 })));
        Assert.True(CommandExecutor.IsMutating(Op(HvOperation.NewSwitch)));
        Assert.True(CommandExecutor.IsMutating(Op(HvOperation.RemoveSwitch)));
        Assert.False(CommandExecutor.IsMutating(Op(HvOperation.ListNetAdapters)));
    }

    [Fact]
    public void Script_handles_the_new_operations_with_fixed_cmdlets()
    {
        var script = Script();
        foreach (var branch in new[] { "'ListNetAdapters' {", "'NewSwitch' {", "'RemoveSwitch' {" })
        {
            Assert.Contains(branch, script, StringComparison.Ordinal);
        }
        // The switch is created with splatted, typed parameters and removed by id only when no VM uses it.
        Assert.Contains("New-VMSwitch @params", script, StringComparison.Ordinal);
        Assert.Contains("Disconnect the VMs first.", script, StringComparison.Ordinal);
        Assert.Contains("already exists on this host", script, StringComparison.Ordinal);
        // Apply checkpoint: a VM that is not off is turned off before Restore-VMSnapshot.
        var restore = script[script.IndexOf("'RestoreCheckpoint' {", StringComparison.Ordinal)..script.IndexOf("'RemoveCheckpoint' {", StringComparison.Ordinal)];
        Assert.True(restore.IndexOf("Stop-VM -VM $vm -TurnOff -Force", StringComparison.Ordinal) is var stop and > 0
                    && stop < restore.IndexOf("Restore-VMSnapshot", StringComparison.Ordinal));
        // A new disk goes to the folder decided by the gateway when one is given.
        Assert.Contains("$a.newDisk.folder", script, StringComparison.Ordinal);
    }
}
