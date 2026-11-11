using HvPortal.Agent.Commands;
using HvPortal.Agent.HyperV;
using HvPortal.Contracts.Agent;
using HvPortal.Contracts.HyperV;

namespace HvPortal.Agent.Tests;

/// <summary>Protocol 10 (v0.8.0): resource use history of VMs (ADR-039): which commands need it, validation and script rules.</summary>
public sealed class Protocol10Tests
{
    private static string Script()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Ops", "HvPortal.Ops.ps1"));
        return string.Join('\n', System.Text.RegularExpressions.Regex.Replace(source, "(?s)<#.*?#>", "").Split('\n').Where(l => !l.TrimStart().StartsWith('#')));
    }

    private static string Branch(string script, string op)
    {
        var start = script.IndexOf($"'{op}' {{", StringComparison.Ordinal);
        Assert.True(start >= 0, op);
        var next = System.Text.RegularExpressions.Regex.Match(script[(start + 10)..], @"\n        '[A-Za-z]+' \{");
        return next.Success ? script.Substring(start, next.Index + 10) : script[start..];
    }

    private static AgentCommand Op(HvOperation op, object? args = null) => new()
    {
        CommandId = Guid.NewGuid(), Kind = AgentCommandKind.HyperVOperation, Operation = op, OperationArgs = args is null ? null : HvOperations.ToElement(args),
    };

    [Fact]
    public void Metering_operations_need_protocol_10_and_only_the_changes_are_mutating()
    {
        Assert.Equal(13, AgentProtocol.Version);
        Assert.Equal("0.8.0", AgentProtocol.MinimumAgentFor(10));
        Assert.Equal("0.7.0", AgentProtocol.MinimumAgentFor(9));
        foreach (var op in new[] { HvOperation.GetVmMetrics, HvOperation.SetVmResourceMetering, HvOperation.ResetVmResourceMetering })
        {
            Assert.Equal(10, AgentProtocol.RequiredVersion(Op(op)));
        }
        Assert.False(CommandExecutor.IsMutating(Op(HvOperation.GetVmMetrics)));
        Assert.True(CommandExecutor.IsMutating(Op(HvOperation.SetVmResourceMetering)));
        Assert.True(CommandExecutor.IsMutating(Op(HvOperation.ResetVmResourceMetering)));
    }

    [Fact]
    public void Metering_arguments_are_validated_by_the_agent()
    {
        var vm = Guid.NewGuid();
        Assert.Null(PowerShellOperationRunner.Validate(HvOperation.GetVmMetrics, HvOperations.ToElement(new VmMetricsRequest(null))));
        Assert.Null(PowerShellOperationRunner.Validate(HvOperation.GetVmMetrics, HvOperations.ToElement(new VmMetricsRequest(vm))));
        Assert.NotNull(PowerShellOperationRunner.Validate(HvOperation.GetVmMetrics, HvOperations.ToElement(new VmMetricsRequest(Guid.Empty))));
        Assert.Null(PowerShellOperationRunner.Validate(HvOperation.SetVmResourceMetering, HvOperations.ToElement(new VmMeteringChange(vm, true))));
        Assert.NotNull(PowerShellOperationRunner.Validate(HvOperation.SetVmResourceMetering, HvOperations.ToElement(new VmMeteringChange(Guid.Empty, true))));
        Assert.NotNull(PowerShellOperationRunner.Validate(HvOperation.SetVmResourceMetering, null));
        Assert.Null(PowerShellOperationRunner.Validate(HvOperation.ResetVmResourceMetering, HvOperations.ToElement(new VmRef(vm))));
        Assert.NotNull(PowerShellOperationRunner.Validate(HvOperation.ResetVmResourceMetering, HvOperations.ToElement(new VmRef(Guid.Empty))));
    }

    [Fact]
    public void The_operations_script_reads_metrics_without_changing_anything_and_changes_only_the_vm_by_id()
    {
        var script = Script();
        var read = Branch(script, "GetVmMetrics");
        Assert.Contains("Measure-VM -VM $vm", read, StringComparison.Ordinal);
        Assert.Contains("Get-VmById $a.vmId", read, StringComparison.Ordinal);
        // Only VMs whose metering is on are measured (Measure-VM fails otherwise) and one failing VM does not hide the others.
        Assert.Contains("if ($vm.ResourceMeteringEnabled) {", read, StringComparison.Ordinal);
        Assert.Contains("[void]$errors.Add(", read, StringComparison.Ordinal);
        foreach (var change in new[] { "Enable-VMResourceMetering", "Disable-VMResourceMetering", "Reset-VMResourceMetering", "Set-VM", "Stop-VM", "Remove-" })
        {
            Assert.DoesNotContain(change, read, StringComparison.OrdinalIgnoreCase);
        }

        var set = Branch(script, "SetVmResourceMetering");
        Assert.Contains("$vm = Get-VmById $a.vmId", set, StringComparison.Ordinal);
        Assert.Contains("Enable-VMResourceMetering -VM $vm", set, StringComparison.Ordinal);
        Assert.Contains("Disable-VMResourceMetering -VM $vm", set, StringComparison.Ordinal);
        // The result is what Hyper-V reports afterwards, not what was asked.
        Assert.Contains("still reports resource metering", set, StringComparison.Ordinal);

        var reset = Branch(script, "ResetVmResourceMetering");
        Assert.Contains("Reset-VMResourceMetering -VM $vm", reset, StringComparison.Ordinal);
        Assert.Contains("if (-not $vm.ResourceMeteringEnabled) { throw 'invalid_state:", reset, StringComparison.Ordinal);
    }
}
