using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Clusters;

namespace HvPortal.Gateway.Tests.Unit;

/// <summary>Rules of the cluster status, capacity (one node lost) and maintenance checks (ADR-041).</summary>
public sealed class ClusterRulesTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private static ClusterStatusReport Report(params ClusterNodeStatus[] nodes) => new()
    {
        CollectedUtc = DateTimeOffset.UtcNow,
        Name = "CL01",
        ReadByNode = "HV01",
        Nodes = nodes.Length > 0 ? nodes : [Node("HV01"), Node("HV02")],
        Quorum = new ClusterQuorumStatus { QuorumType = "NodeAndFileShareMajority", WitnessType = "FileShare", WitnessName = "File Share Witness", WitnessState = "Online" },
        Networks = [new ClusterNetworkStatus { Name = "Cluster Network 1", State = "Up", Role = "ClusterAndClient" }],
        SharedVolumes = [new ClusterSharedVolumeStatus { Name = "Cluster Virtual Disk (Volume1)", Path = @"C:\ClusterStorage\Volume1", State = "Online", SizeBytes = 1000 * Gb, FreeBytes = 600 * Gb, Access = "Direct" }],
        Storage = new ClusterStorageStatus
        {
            S2dEnabled = true,
            Pools = [new ClusterPoolStatus { Name = "S2D on CL01", HealthStatus = "Healthy", OperationalStatus = "OK", SizeBytes = 8000 * Gb, AllocatedBytes = 4000 * Gb }],
            VirtualDisks = [new ClusterVirtualDiskStatus { Name = "Volume1", HealthStatus = "Healthy", OperationalStatus = "OK", Resiliency = "Mirror" }],
            PhysicalDisks = [new ClusterPhysicalDiskStatus { Name = "Disk 1", HealthStatus = "Healthy", Usage = "Auto-Select", SizeBytes = 1000 * Gb }],
        },
    };

    private static ClusterNodeStatus Node(string name, string state = "Up") => new() { Name = name, State = state, NodeWeight = 1, DynamicWeight = 1 };

    private static NodeFacts Facts(string name, long freeGb = 100, long haVmGb = 0, bool connected = true, string state = "Up", int lp = 32, int vcpus = 0,
        IReadOnlyList<string>? notClustered = null) => new()
    {
        Name = name, HostId = Guid.NewGuid(), HostName = name.ToLowerInvariant(), AgentConnected = connected, AgentProtocol = connected ? 11 : 0, State = state,
        TotalMemoryBytes = 256 * Gb, FreeMemoryBytes = freeGb * Gb, LogicalProcessors = lp, ClusteredVmMemoryBytes = haVmGb * Gb, RunningVmMemoryBytes = haVmGb * Gb,
        RunningVcpus = vcpus, RunningVms = vcpus > 0 ? 1 : 0, NotClusteredRunningVms = notClustered ?? [],
    };

    private static List<ClusterIssue> Evaluate(ClusterStatusReport? report, IReadOnlyList<NodeFacts> nodes, string? error = null) =>
        ClusterRules.Evaluate(report, nodes, ClusterRules.Capacity(nodes, report), error);

    [Fact]
    public void A_healthy_cluster_is_ok_and_notes_do_not_change_the_status()
    {
        var nodes = new[] { Facts("HV01"), Facts("HV02") };
        var issues = Evaluate(Report(), nodes);
        Assert.Empty(issues);
        Assert.Equal(HealthLevel.Ok, ClusterRules.Overall(issues));
        // Three nodes without a witness: only a recommendation.
        var three = Report(Node("HV01"), Node("HV02"), Node("HV03")) with { Quorum = new ClusterQuorumStatus { QuorumType = "NodeMajority", WitnessType = "None" } };
        var notes = Evaluate(three, [Facts("HV01"), Facts("HV02"), Facts("HV03")]);
        Assert.Contains(notes, i => i.Code == "witness_recommended" && i.Level == ClusterRules.Info);
        Assert.Equal(HealthLevel.Ok, ClusterRules.Overall(notes));
    }

    [Fact]
    public void Problems_are_found_and_the_worst_one_decides_the_status()
    {
        var report = Report(Node("HV01"), Node("HV02", "Down")) with
        {
            Quorum = new ClusterQuorumStatus { WitnessType = "FileShare", WitnessName = "Witness", WitnessState = "Failed" },
            SharedVolumes = [new ClusterSharedVolumeStatus { Name = "v1", Path = @"C:\ClusterStorage\Volume1", State = "Online", SizeBytes = 100 * Gb, FreeBytes = 4 * Gb, Access = "FileSystemRedirected" }],
            Errors = ["networks: access denied"],
        };
        var issues = Evaluate(report, [Facts("HV01"), Facts("HV02", connected: false, state: "Down")]);
        Assert.Contains(issues, i => i.Code == "node_down" && i.Subject == "HV02" && i.Level == HealthLevel.Critical);
        Assert.Contains(issues, i => i.Code == "nodes_majority_down");
        Assert.Contains(issues, i => i.Code == "witness_not_online" && i.Level == HealthLevel.Critical);
        Assert.Contains(issues, i => i.Code == "csv_full");
        Assert.Contains(issues, i => i.Code == "csv_redirected");
        Assert.Contains(issues, i => i.Code == "section_unreadable" && i.Level == HealthLevel.Unknown);
        // The agent of a node that is down is not reported again as "disconnected".
        Assert.DoesNotContain(issues, i => i.Code == "agent_disconnected");
        Assert.Equal(HealthLevel.Critical, ClusterRules.Overall(issues));
    }

    [Fact]
    public void Storage_spaces_direct_problems_are_reported()
    {
        var report = Report() with
        {
            Storage = Report().Storage! with
            {
                VirtualDisks = [new ClusterVirtualDiskStatus { Name = "Volume1", HealthStatus = "Warning", OperationalStatus = "Degraded" }],
                PhysicalDisks = [new ClusterPhysicalDiskStatus { Name = "Disk 7", SerialNumber = "S7", HealthStatus = "Unhealthy", Usage = "Retired", SizeBytes = 1000 * Gb }],
                Jobs = [new ClusterStorageJobStatus { Name = "Repair", State = "Running", PercentComplete = 40 }],
                Pools = [new ClusterPoolStatus { Name = "S2D", HealthStatus = "Warning", OperationalStatus = "Degraded", SizeBytes = 8000 * Gb, AllocatedBytes = 7500 * Gb }],
            },
        };
        var issues = Evaluate(report, [Facts("HV01"), Facts("HV02")]);
        Assert.Contains(issues, i => i.Code == "virtual_disk_not_healthy" && i.Level == HealthLevel.Warning);
        Assert.Contains(issues, i => i.Code == "physical_disk_problem" && i.Subject == "Disk 7");
        Assert.Contains(issues, i => i.Code == "storage_repair_running" && i.Args!["percent"] == "40");
        Assert.Contains(issues, i => i.Code == "pool_not_healthy");
        Assert.Equal(HealthLevel.Warning, ClusterRules.Overall(issues));
    }

    [Fact]
    public void Agents_of_the_nodes_are_checked()
    {
        var withoutAgent = new NodeFacts { Name = "HV02" };
        var issues = Evaluate(Report(), [Facts("HV01", connected: false), withoutAgent]);
        Assert.Contains(issues, i => i.Code == "agent_disconnected" && i.Subject == "HV01");
        Assert.Contains(issues, i => i.Code == "node_without_agent" && i.Subject == "HV02");
        var unreadable = Evaluate(null, [Facts("HV01")], "access_denied");
        Assert.Equal(HealthLevel.Unknown, ClusterRules.Overall(unreadable));
        Assert.Contains(unreadable, i => i.Code == "status_unreadable");
    }

    [Fact]
    public void Capacity_checks_that_the_vms_of_any_one_node_fit_on_the_others()
    {
        // HV01 runs 120 GB of highly available VMs; HV02 has 100 GB free (minus the host reserve): they would not fit.
        var nodes = new[] { Facts("HV01", freeGb: 100, haVmGb: 120, vcpus: 100, lp: 32), Facts("HV02", freeGb: 100, haVmGb: 20, vcpus: 40, lp: 32) };
        var c = ClusterRules.Capacity(nodes, Report());
        Assert.False(c.ToleratesNodeLoss);
        Assert.Equal("HV01", c.WorstNode);
        Assert.Equal(120 * Gb, c.FailoverNeededBytes);
        Assert.Equal(100 * Gb - ClusterRules.HostReserveBytes, c.FailoverAvailableBytes);
        Assert.Equal(140 / 64.0, c.VcpuRatio!.Value, 2);
        Assert.Equal(140 / 32.0, c.VcpuRatioN1!.Value, 2);
        var issues = ClusterRules.Evaluate(Report(), nodes, c, null);
        Assert.Contains(issues, i => i.Code == "capacity_memory_n1" && i.Subject == "HV01");
        Assert.Contains(issues, i => i.Code == "capacity_cpu_n1");
        // S2D pool: 4000 GB free, reserve = one 1000 GB capacity disk per node (2 nodes).
        Assert.Equal(4000 * Gb, c.PoolFreeBytes);
        Assert.Equal(2000 * Gb, c.PoolReserveBytes);

        var fits = ClusterRules.Capacity([Facts("HV01", freeGb: 100, haVmGb: 50), Facts("HV02", freeGb: 100, haVmGb: 50)], Report());
        Assert.True(fits.ToleratesNodeLoss);
        // Not highly available VMs do not move: they do not count.
        var notHa = Facts("HV01", freeGb: 10, haVmGb: 0) with { RunningVmMemoryBytes = 200 * Gb };
        Assert.True(ClusterRules.Capacity([notHa, Facts("HV02", freeGb: 10)], Report()).ToleratesNodeLoss);
        // Free memory not known: not computed (never guessed).
        Assert.Null(ClusterRules.Capacity([Facts("HV01", haVmGb: 10) with { FreeMemoryBytes = null }, Facts("HV02")], Report()).ToleratesNodeLoss);
    }

    [Fact]
    public void Maintenance_is_allowed_only_when_the_cluster_can_lose_the_node()
    {
        var ok = ClusterRules.CheckMaintenance(MaintenanceActions.Enter, "HV01", Report(), [Facts("HV01", haVmGb: 20), Facts("HV02", freeGb: 100)]);
        Assert.True(ok.Allowed);
        Assert.DoesNotContain(ok.Warnings, w => w.Level != ClusterRules.Info);

        var otherPaused = ClusterRules.CheckMaintenance(MaintenanceActions.Enter, "HV01", Report(Node("HV01"), Node("HV02", "Paused")), [Facts("HV01"), Facts("HV02")]);
        Assert.False(otherPaused.Allowed);
        Assert.Contains(otherPaused.Blocking, b => b.Code == "other_node_not_up");

        var repairing = Report() with { Storage = Report().Storage! with { Jobs = [new ClusterStorageJobStatus { Name = "Repair", State = "Running", PercentComplete = 10 }] } };
        Assert.Contains(ClusterRules.CheckMaintenance(MaintenanceActions.Enter, "HV01", repairing, [Facts("HV01"), Facts("HV02")]).Blocking,
            b => b.Code == "storage_repair_running");

        var full = ClusterRules.CheckMaintenance(MaintenanceActions.Enter, "HV01", Report(), [Facts("HV01", haVmGb: 200), Facts("HV02", freeGb: 50)]);
        Assert.Contains(full.Blocking, b => b.Code == "insufficient_memory");

        var notHa = ClusterRules.CheckMaintenance(MaintenanceActions.Enter, "HV01", Report(), [Facts("HV01", notClustered: ["sql01"]), Facts("HV02")]);
        Assert.True(notHa.Allowed);
        Assert.Contains(notHa.Warnings, w => w.Code == "vms_not_highly_available" && w.Args!["vms"] == "sql01");

        var witnessDown = Report() with { Quorum = new ClusterQuorumStatus { WitnessType = "Cloud", WitnessName = "Cloud Witness", WitnessState = "Failed" } };
        Assert.Contains(ClusterRules.CheckMaintenance(MaintenanceActions.Enter, "HV01", witnessDown, [Facts("HV01"), Facts("HV02")]).Blocking,
            b => b.Code == "witness_not_online");

        Assert.Contains(ClusterRules.CheckMaintenance(MaintenanceActions.Enter, "HV01", Report(Node("HV01", "Paused"), Node("HV02")), [Facts("HV01"), Facts("HV02")]).Blocking,
            b => b.Code == "already_in_maintenance");
        Assert.Contains(ClusterRules.CheckMaintenance(MaintenanceActions.Enter, "HV09", Report(), [Facts("HV01")]).Blocking, b => b.Code == "not_a_node");
        Assert.Contains(ClusterRules.CheckMaintenance(MaintenanceActions.Enter, "HV01", null, []).Blocking, b => b.Code == "status_unreadable");
    }

    [Fact]
    public void Leaving_maintenance_needs_a_paused_node()
    {
        Assert.Contains(ClusterRules.CheckMaintenance(MaintenanceActions.Exit, "HV01", Report(), [Facts("HV01"), Facts("HV02")]).Blocking,
            b => b.Code == "not_in_maintenance");
        var back = ClusterRules.CheckMaintenance(MaintenanceActions.Exit, "HV01", Report(Node("HV01", "Paused"), Node("HV02")), [Facts("HV01"), Facts("HV02")]);
        Assert.True(back.Allowed);
        Assert.Contains(back.Warnings, w => w.Code == "resync_after_exit" && w.Level == ClusterRules.Info);
    }

    [Fact]
    public void Shared_volume_paths_and_node_names_follow_the_cluster_rules()
    {
        Assert.True(ClusterNames.IsSharedVolumePath(@"C:\ClusterStorage\Volume1"));
        Assert.True(ClusterNames.IsSharedVolumePath(@"C:\ClusterStorage\Volume1\VMs\web01"));
        Assert.False(ClusterNames.IsSharedVolumePath(@"C:\ClusterStorage"));
        Assert.False(ClusterNames.IsSharedVolumePath(@"\\fs\share\VMs"));
        Assert.False(ClusterNames.IsSharedVolumePath(@"C:\ClusterStorage\..\Windows"));
        Assert.True(ClusterNames.IsValidNodeName("HV-01"));
        Assert.False(ClusterNames.IsValidNodeName("HV 01"));
        Assert.False(ClusterNames.IsValidNodeName(null));
        Assert.Equal("invalid_maintenance_action", OperationValidation.Validate(new ClusterNodeMaintenanceRequest { NodeName = "HV01", Action = "enter" }));
    }

    [Fact]
    public void Core_group_health_service_faults_and_replication_are_reported()
    {
        var report = Report() with
        {
            CoreGroupState = "PartialOnline",
            CoreResources = [new ClusterResourceStatus { Name = "Cluster IP Address", ResourceType = "IP Address", State = "Failed" }],
            HealthFaults = [new ClusterHealthFault { Severity = "Critical", Reason = "The physical disk has failed.", RecommendedAction = "Replace the disk.", FaultingObject = "Disk 7" }],
            Replication = [new StorageReplicaStatus { Name = "rg01", Status = "InitialBlockCopy", BytesRemaining = 5 * Gb },
                           new StorageReplicaStatus { Name = "rg02", Status = "ContinuouslyReplicating", BytesRemaining = 0 }],
        };
        var issues = Evaluate(report, [Facts("HV01"), Facts("HV02")]);
        Assert.Contains(issues, i => i.Code == "core_group_not_online" && i.Level == HealthLevel.Critical);
        Assert.Contains(issues, i => i.Code == "core_resource_not_online" && i.Subject == "Cluster IP Address");
        Assert.Contains(issues, i => i.Code == "health_fault" && i.Level == HealthLevel.Critical && i.Args!["action"] == "Replace the disk.");
        Assert.Contains(issues, i => i.Code == "replication_not_in_sync" && i.Subject == "rg01");
        Assert.DoesNotContain(issues, i => i.Code == "replication_not_in_sync" && i.Subject == "rg02");
        // Older agents send none of these: nothing is reported (and nothing guessed).
        Assert.DoesNotContain(Evaluate(Report(), [Facts("HV01"), Facts("HV02")]), i => i.Code is "core_group_not_online" or "health_fault" or "replication_not_in_sync");
    }
}
