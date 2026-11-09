using HvPortal.Agent.Commands;
using HvPortal.Agent.HyperV;
using HvPortal.Contracts.Agent;
using HvPortal.Contracts.HyperV;

namespace HvPortal.Agent.Tests;

/// <summary>
/// Protocol 11 (v0.9.0): failover clusters (ADR-041): which commands need it, validation in the agent, the rules of the
/// operations script and the exception for cluster shared volumes. What the cluster cmdlets really do is in the
/// real-host test plan (V72-V80).
/// </summary>
public sealed class Protocol11Tests
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
    public void Cluster_operations_need_protocol_11_and_only_the_changes_are_mutating()
    {
        Assert.Equal(11, AgentProtocol.Version);
        Assert.Equal("0.9.0", AgentProtocol.MinimumAgentFor(11));
        Assert.Equal("0.8.0", AgentProtocol.MinimumAgentFor(10));
        foreach (var op in new[] { HvOperation.GetClusterStatus, HvOperation.SetClusterNodeMaintenance, HvOperation.MoveClusterVm })
        {
            Assert.Equal(11, AgentProtocol.RequiredVersion(Op(op)));
        }
        // An older agent would create a VM that is not highly available: never sent to it.
        Assert.Equal(11, AgentProtocol.RequiredVersion(Op(HvOperation.NewVm, new NewVmRequest { Name = "vm", HighlyAvailable = true, Location = @"C:\ClusterStorage\Volume1" })));
        Assert.True(AgentProtocol.RequiredVersion(Op(HvOperation.NewVm, new NewVmRequest { Name = "vm" })) < 11);
        Assert.False(CommandExecutor.IsMutating(Op(HvOperation.GetClusterStatus)));
        Assert.True(CommandExecutor.IsMutating(Op(HvOperation.SetClusterNodeMaintenance)));
        Assert.True(CommandExecutor.IsMutating(Op(HvOperation.MoveClusterVm)));
        // Draining live-migrates every role of the node: allowed to run for hours.
        Assert.True(CommandExecutor.TimeoutFor(Op(HvOperation.SetClusterNodeMaintenance) with { TimeoutSeconds = 4 * 3600 }, HvOperation.SetClusterNodeMaintenance)
            > TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void Cluster_arguments_are_validated_by_the_agent()
    {
        Assert.Null(PowerShellOperationRunner.Validate(HvOperation.GetClusterStatus, HvOperations.ToElement(new { })));
        Assert.Null(PowerShellOperationRunner.Validate(HvOperation.SetClusterNodeMaintenance,
            HvOperations.ToElement(new ClusterNodeMaintenanceRequest { NodeName = "HV01", Action = MaintenanceActions.Enter })));
        Assert.Null(PowerShellOperationRunner.Validate(HvOperation.SetClusterNodeMaintenance,
            HvOperations.ToElement(new ClusterNodeMaintenanceRequest { NodeName = "hv-02", Action = MaintenanceActions.Exit, Failback = true })));
        foreach (var bad in new[] { "HV01; Remove-Item", "HV01\\x", "-HV01", "HV01-", "", new string('a', 64), "hv01.contoso.local" })
        {
            Assert.Equal("invalid_node_name", PowerShellOperationRunner.Validate(HvOperation.SetClusterNodeMaintenance,
                HvOperations.ToElement(new ClusterNodeMaintenanceRequest { NodeName = bad, Action = MaintenanceActions.Enter })));
        }
        Assert.Equal("invalid_maintenance_action", PowerShellOperationRunner.Validate(HvOperation.SetClusterNodeMaintenance,
            HvOperations.ToElement(new ClusterNodeMaintenanceRequest { NodeName = "HV01", Action = "Evict" })));
        // Failback only means something when the node comes back.
        Assert.Equal("invalid_maintenance_action", PowerShellOperationRunner.Validate(HvOperation.SetClusterNodeMaintenance,
            HvOperations.ToElement(new ClusterNodeMaintenanceRequest { NodeName = "HV01", Action = MaintenanceActions.Enter, Failback = true })));
        Assert.Null(PowerShellOperationRunner.Validate(HvOperation.MoveClusterVm, HvOperations.ToElement(new MoveClusterVmRequest { VmId = Guid.NewGuid(), NodeName = "HV02" })));
        Assert.Equal("invalid_vm", PowerShellOperationRunner.Validate(HvOperation.MoveClusterVm, HvOperations.ToElement(new MoveClusterVmRequest { VmId = Guid.Empty, NodeName = "HV02" })));
        Assert.Equal("invalid_node_name", PowerShellOperationRunner.Validate(HvOperation.MoveClusterVm, HvOperations.ToElement(new MoveClusterVmRequest { VmId = Guid.NewGuid(), NodeName = "x y" })));
    }

    [Fact]
    public void A_node_name_that_is_too_long_is_refused_quickly()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(ClusterNames.IsValidNodeName(new string('a', 60) + new string('-', 5000) + "!"));
        Assert.False(ClusterNames.IsValidNodeName(string.Concat(Enumerable.Repeat("a-", 20000)) + "$"));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void A_highly_available_vm_must_be_on_a_real_cluster_shared_volume_of_this_host()
    {
        // Text rule (gateway and agent): the location must be <drive>:\ClusterStorage\<volume>[\...].
        Assert.Equal("ha_requires_shared_volume", OperationValidation.Validate(new NewVmRequest { Name = "vm", HighlyAvailable = true, Location = @"D:\VMs" }));
        Assert.Equal("ha_requires_shared_volume", OperationValidation.Validate(new NewVmRequest { Name = "vm", HighlyAvailable = true }));
        Assert.Equal("ha_requires_shared_volume", OperationValidation.Validate(new NewVmRequest { Name = "vm", HighlyAvailable = true, Location = @"C:\ClusterStorage" }));
        Assert.Null(OperationValidation.Validate(new NewVmRequest { Name = "vm", HighlyAvailable = true, Location = @"C:\ClusterStorage\Volume1\VMs" }));
        // The agent also checks the file system: this machine has no CSV, so even a well-formed CSV path is refused.
        Assert.Equal("ha_requires_shared_volume", PowerShellOperationRunner.Validate(HvOperation.NewVm,
            HvOperations.ToElement(new NewVmRequest { Name = "vm", HighlyAvailable = true, Location = @"C:\ClusterStorage\Volume1" })));
        Assert.Null(PowerShellOperationRunner.Validate(HvOperation.NewVm, HvOperations.ToElement(new NewVmRequest { Name = "vm" })));
    }

    [Fact]
    public void Only_real_csv_mount_points_are_accepted_as_reparse_points()
    {
        Assert.Equal(@"C:\ClusterStorage\Volume1", ClusterNames.SharedVolumeRoot(@"c:\ClusterStorage\Volume1\VMs\a"));
        Assert.Null(ClusterNames.SharedVolumeRoot(@"C:\ClusterStorage"));
        Assert.Null(ClusterNames.SharedVolumeRoot(@"D:\Data\ClusterStorage\Volume1"));
        Assert.True(SharedVolumes.IsClusterStorageFolder(@"C:\ClusterStorage"));
        Assert.True(SharedVolumes.IsClusterStorageFolder(@"C:\ClusterStorage\"));
        Assert.False(SharedVolumes.IsClusterStorageFolder(@"C:\ClusterStorage\Volume1"));
        // Not a CSVFS volume (no cluster here): not a CSV root, whatever the name says.
        Assert.False(SharedVolumes.IsRoot(@"C:\ClusterStorage\Volume1"));
        Assert.False(SharedVolumes.IsRoot(Path.GetTempPath()));
        Assert.Null(SharedVolumes.RootOf(@"C:\ClusterStorage\Volume1\VMs"));
        // A junction is still refused by the folder checks.
        var dir = Directory.CreateTempSubdirectory("hvp-csv-");
        try
        {
            var target = Directory.CreateDirectory(Path.Combine(dir.FullName, "target"));
            var link = Path.Combine(dir.FullName, "link");
            Directory.CreateSymbolicLink(link, target.FullName);
            Assert.Throws<HostPathException>(() => HostFolderBrowser.CheckFolder(Path.Combine(link, "vm"), mustExist: false));
        }
        catch (IOException)
        {
            // Creating symbolic links needs a privilege the test account may not have: nothing to check then.
        }
        catch (UnauthorizedAccessException)
        {
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void Cluster_states_are_named_like_the_cluster_cmdlets()
    {
        Assert.Equal("Up", ClusterReader.NodeState(0));
        Assert.Equal("Down", ClusterReader.NodeState(1));
        Assert.Equal("Paused", ClusterReader.NodeState(2));
        Assert.Equal("Joining", ClusterReader.NodeState(3));
        Assert.Equal("Unknown", ClusterReader.NodeState(-1));
        Assert.Equal("Unknown", ClusterReader.NodeState(null));
        Assert.Equal("InProgress", ClusterReader.DrainStatus(1));
        Assert.Equal("Failed", ClusterReader.DrainStatus(3));
        Assert.Null(ClusterReader.DrainStatus(null));
    }

    [Fact]
    public void The_operations_script_reads_the_cluster_without_changing_it_and_maintains_nodes_the_documented_way()
    {
        var script = Script();
        var status = Branch(script, "GetClusterStatus");
        foreach (var verb in new[] { "Suspend-", "Resume-", "Move-", "Set-", "Remove-", "Add-", "Enable-", "Disable-", "Stop-", "Start-", "New-Cluster" })
        {
            Assert.DoesNotContain(verb, status, StringComparison.Ordinal);
        }
        // The cloud witness key is never read, only the account name.
        Assert.DoesNotContain("AccountKey", status, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Get-ClusterQuorum", status, StringComparison.Ordinal);
        Assert.Contains("Get-ClusterSharedVolume", status, StringComparison.Ordinal);
        Assert.Contains("Get-StorageJob", status, StringComparison.Ordinal);

        var maintenance = Branch(script, "SetClusterNodeMaintenance");
        Assert.Contains("Suspend-ClusterNode -Name $nodeName -Drain -Wait", maintenance, StringComparison.Ordinal);
        Assert.Contains("Resume-ClusterNode -Name $nodeName -Failback $failback", maintenance, StringComparison.Ordinal);
        Assert.Contains("Enable-StorageMaintenanceMode", maintenance, StringComparison.Ordinal);
        Assert.Contains("Disable-StorageMaintenanceMode", maintenance, StringComparison.Ordinal);
        // Storage maintenance is turned off BEFORE the node is resumed (Microsoft's order).
        Assert.True(maintenance.IndexOf("Disable-StorageMaintenanceMode", StringComparison.Ordinal) < maintenance.IndexOf("Resume-ClusterNode", StringComparison.Ordinal));
        foreach (var never in new[] { "Remove-ClusterNode", "Stop-ClusterNode", "Stop-Computer", "Restart-Computer", "-ForceDrain", "Clear-ClusterNode" })
        {
            Assert.DoesNotContain(never, maintenance, StringComparison.Ordinal);
        }

        var move = Branch(script, "MoveClusterVm");
        Assert.Contains("Move-ClusterVirtualMachineRole -VMId $vm.Id -Node $nodeName", move, StringComparison.Ordinal);
        Assert.Contains("IsClustered", move, StringComparison.Ordinal);

        var newVm = Branch(script, "NewVm");
        Assert.Contains("Add-ClusterVirtualMachineRole -VMId $vm.Id", newVm, StringComparison.Ordinal);
        Assert.Contains("NOT highly available", newVm, StringComparison.Ordinal);
    }
}
