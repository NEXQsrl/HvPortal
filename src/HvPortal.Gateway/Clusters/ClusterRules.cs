using HvPortal.Contracts.HyperV;

namespace HvPortal.Gateway.Clusters;

/// <summary>
/// A problem (or a note) found in a failover cluster (ADR-041). Code + Args are translated by the UI
/// (cluster.issue.&lt;code&gt;); Message is the English text for the API, notifications and logs.
/// Level: Critical, Warning, Unknown (could not be read) or Info (does not change the cluster status).
/// </summary>
public sealed record ClusterIssue(string Level, string Code, string Message, string? Subject = null, IReadOnlyDictionary<string, string>? Args = null);

/// <summary>What the gateway knows about one node: its portal host (when there is one) and its last inventory.</summary>
public sealed record NodeFacts
{
    public required string Name { get; init; }
    public Guid? HostId { get; init; }
    public string? HostName { get; init; }
    /// <summary>The agent of the node is connected now.</summary>
    public bool AgentConnected { get; init; }
    public int AgentProtocol { get; init; }
    /// <summary>State from the cluster report (or from the node's own inventory when no report could be read).</summary>
    public string? State { get; init; }
    public long? TotalMemoryBytes { get; init; }
    public long? FreeMemoryBytes { get; init; }
    public int? LogicalProcessors { get; init; }
    /// <summary>Memory of the running VMs of the node that are roles of the cluster (they move when the node stops).</summary>
    public long ClusteredVmMemoryBytes { get; init; }
    /// <summary>Memory of all running VMs of the node.</summary>
    public long RunningVmMemoryBytes { get; init; }
    public int RunningVcpus { get; init; }
    public int RunningVms { get; init; }
    /// <summary>Running VMs of the node that are not roles of the cluster: a drain does not move them.</summary>
    public IReadOnlyList<string> NotClusteredRunningVms { get; init; } = [];
}

public sealed record CapacityReport
{
    public int Nodes { get; init; }
    public int NodesUp { get; init; }
    public long TotalMemoryBytes { get; init; }
    public long FreeMemoryBytes { get; init; }
    public long VmMemoryBytes { get; init; }
    public int LogicalProcessors { get; init; }
    public int RunningVcpus { get; init; }
    /// <summary>vCPUs of the running VMs per logical processor of the nodes that are up.</summary>
    public double? VcpuRatio { get; init; }
    /// <summary>Same ratio when the node with the most logical processors is stopped.</summary>
    public double? VcpuRatioN1 { get; init; }
    /// <summary>Node whose loss needs the most memory elsewhere, the memory its clustered VMs need and what the other nodes have free.</summary>
    public string? WorstNode { get; init; }
    public long? FailoverNeededBytes { get; init; }
    public long? FailoverAvailableBytes { get; init; }
    /// <summary>True when the VMs of any one node fit on the others (memory), null when it cannot be computed.</summary>
    public bool? ToleratesNodeLoss { get; init; }
    public long? SharedVolumeBytes { get; init; }
    public long? SharedVolumeFreeBytes { get; init; }
    /// <summary>Storage Spaces Direct: unallocated pool capacity and the reserve Microsoft recommends for in-place repairs.</summary>
    public long? PoolFreeBytes { get; init; }
    public long? PoolReserveBytes { get; init; }
}

public sealed record MaintenanceCheck(bool Allowed, IReadOnlyList<ClusterIssue> Blocking, IReadOnlyList<ClusterIssue> Warnings);

/// <summary>
/// Rules of the cluster page, the monitor and the maintenance checks (pure functions, unit tested). Never better than
/// what was read: an unreadable section makes the status at least Unknown.
/// </summary>
public static class ClusterRules
{
    public const string Info = "Info";
    /// <summary>Memory left to every host for its own operating system and Hyper-V when VMs fail over to it.</summary>
    public const long HostReserveBytes = 2L * 1024 * 1024 * 1024;
    /// <summary>vCPU per logical processor above which the CPU of the cluster with one node stopped is called overcommitted.</summary>
    public const double VcpuRatioWarning = 4.0;
    /// <summary>Volume free space thresholds (same as host health, ADR-036).</summary>
    public const double FreeWarning = 0.10;
    public const double FreeCritical = 0.05;

    public static string Overall(IEnumerable<ClusterIssue> issues)
    {
        var levels = issues.Where(i => i.Level != Info).Select(i => i.Level).ToList();
        return levels.Count == 0 ? HealthLevel.Ok : HealthLevel.Worst(levels);
    }

    private static ClusterIssue Issue(string level, string code, string message, string? subject = null, params (string Key, object? Value)[] args) =>
        new(level, code, message, subject, args.Length == 0 ? null : args.ToDictionary(a => a.Key, a => Convert.ToString(a.Value, System.Globalization.CultureInfo.InvariantCulture) ?? ""));

    private static bool Is(string? value, string expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    private static bool Healthy(string? health) => Is(health, "Healthy");

    // ------------------------------------------------------------------ status

    /// <summary>Problems of the cluster from a status report and the gateway's view of the nodes (agents, capacity).</summary>
    public static List<ClusterIssue> Evaluate(ClusterStatusReport? report, IReadOnlyList<NodeFacts> nodes, CapacityReport capacity, string? readError)
    {
        var issues = new List<ClusterIssue>();
        if (report is null)
        {
            issues.Add(Issue(HealthLevel.Unknown, "status_unreadable", $"The cluster could not be read from any node: {readError}", null, ("error", readError)));
            AgentIssues(issues, nodes, null);
            return issues;
        }
        NodeIssues(issues, report);
        QuorumIssues(issues, report);
        foreach (var n in report.Networks)
        {
            if (Is(n.State, "Down") || Is(n.State, "Partitioned") || Is(n.State, "Unavailable"))
            {
                issues.Add(Issue(HealthLevel.Warning, "network_" + n.State!.ToLowerInvariant(), $"Cluster network {n.Name} is {n.State}.", n.Name));
            }
        }
        SharedVolumeIssues(issues, report);
        StorageIssues(issues, report, capacity);
        foreach (var r in report.Roles)
        {
            if (Is(r.State, "Failed"))
            {
                issues.Add(Issue(HealthLevel.Critical, "role_failed", $"Role {r.Name} has failed{(r.OwnerNode is { Length: > 0 } o ? " on " + o : "")}.", r.Name, ("node", r.OwnerNode)));
            }
            else if (Is(r.State, "PartialOnline"))
            {
                issues.Add(Issue(HealthLevel.Warning, "role_partial", $"Role {r.Name} is only partially online.", r.Name));
            }
        }
        var failedRoles = report.Roles.Where(r => Is(r.State, "Failed")).Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var r in report.FailedResources.Where(r => r.OwnerGroup is null || !failedRoles.Contains(r.OwnerGroup)))
        {
            issues.Add(Issue(HealthLevel.Warning, "resource_failed", $"Cluster resource {r.Name} ({r.ResourceType}) has failed.", r.Name, ("type", r.ResourceType)));
        }
        if (report.CoreGroupState is { Length: > 0 } core && !Is(core, "Online"))
        {
            issues.Add(Issue(HealthLevel.Critical, "core_group_not_online", $"The core cluster group (cluster name, IP address, witness) is {core}: the cluster cannot be managed by its name.",
                null, ("state", core)));
        }
        foreach (var r in report.CoreResources.Where(r => !Is(r.State, "Online") && !Is(report.CoreGroupState, "Offline")))
        {
            issues.Add(Issue(HealthLevel.Warning, "core_resource_not_online", $"Core resource {r.Name} ({r.ResourceType}) is {r.State}.", r.Name, ("type", r.ResourceType), ("state", r.State)));
        }
        foreach (var f in report.HealthFaults)
        {
            var level = Is(f.Severity, "Critical") || Is(f.Severity, "Fatal") ? HealthLevel.Critical : HealthLevel.Warning;
            issues.Add(Issue(level, "health_fault", $"Health Service: {f.Reason} ({f.FaultingObject}). Recommended: {f.RecommendedAction}",
                f.FaultingObject, ("reason", f.Reason), ("action", f.RecommendedAction), ("severity", f.Severity)));
        }
        foreach (var r in report.Replication.Where(r => r.Status is { Length: > 0 } st && !st.Equals("ContinuouslyReplicating", StringComparison.OrdinalIgnoreCase)))
        {
            issues.Add(Issue(HealthLevel.Warning, "replication_not_in_sync", $"Storage Replica group {r.Name} is {r.Status} ({r.BytesRemaining ?? 0} bytes still to copy).",
                r.Name, ("status", r.Status), ("remaining", r.BytesRemaining)));
        }
        foreach (var e in report.Errors)
        {
            issues.Add(Issue(HealthLevel.Unknown, "section_unreadable", $"Not read: {e}", e.Split(':')[0], ("error", e)));
        }
        AgentIssues(issues, nodes, report);
        CapacityIssues(issues, capacity);
        return issues;
    }

    private static void NodeIssues(List<ClusterIssue> issues, ClusterStatusReport report)
    {
        var down = 0;
        foreach (var n in report.Nodes)
        {
            if (Is(n.State, "Down"))
            {
                down++;
                issues.Add(Issue(HealthLevel.Critical, "node_down", $"Node {n.Name} is down: its highly available VMs were restarted on the other nodes, the others are stopped.", n.Name));
            }
            else if (Is(n.State, "Paused"))
            {
                issues.Add(Issue(HealthLevel.Warning, "node_paused", $"Node {n.Name} is paused (maintenance): the cluster has less spare capacity until it is resumed.", n.Name));
            }
            else if (Is(n.State, "Joining"))
            {
                issues.Add(Issue(HealthLevel.Warning, "node_joining", $"Node {n.Name} is joining the cluster.", n.Name));
            }
            else if (!Is(n.State, "Up"))
            {
                issues.Add(Issue(HealthLevel.Unknown, "node_unknown", $"The state of node {n.Name} is not known.", n.Name));
            }
            if (Is(n.DrainStatus, "Failed"))
            {
                issues.Add(Issue(HealthLevel.Warning, "drain_failed", $"The last drain of node {n.Name} failed: some roles could not be moved.", n.Name));
            }
            if (n.StorageMaintenance == true && !Is(n.State, "Paused"))
            {
                issues.Add(Issue(HealthLevel.Warning, "storage_maintenance_left_on", $"The disks of node {n.Name} are still in storage maintenance mode although the node is not paused.", n.Name));
            }
        }
        if (report.Nodes.Count > 0 && down * 2 >= report.Nodes.Count)
        {
            issues.Add(Issue(HealthLevel.Critical, "nodes_majority_down", $"{down} of {report.Nodes.Count} nodes are down: the cluster runs only thanks to the witness and dynamic quorum; one more failure can stop it.",
                null, ("down", down), ("total", report.Nodes.Count)));
        }
    }

    private static void QuorumIssues(List<ClusterIssue> issues, ClusterStatusReport report)
    {
        if (report.Quorum is not { } q)
        {
            return;
        }
        var nodes = report.Nodes.Count;
        if (q.WitnessType is null || Is(q.WitnessType, "None"))
        {
            if (nodes == 2)
            {
                issues.Add(Issue(HealthLevel.Warning, "no_witness_two_nodes", "A two-node cluster has no witness: configure a file share or cloud witness, otherwise the loss of one node can stop the cluster."));
            }
            else if (nodes > 2)
            {
                issues.Add(Issue(Info, "witness_recommended", "The cluster has no witness: Microsoft recommends one for every cluster (dynamic witness)."));
            }
            return;
        }
        if (!Is(q.WitnessState, "Online"))
        {
            issues.Add(Issue(nodes <= 2 ? HealthLevel.Critical : HealthLevel.Warning, "witness_not_online",
                $"The {q.WitnessType} witness {q.WitnessName} is {q.WitnessState ?? "not online"}: the cluster tolerates fewer failures until it is back.",
                q.WitnessName, ("type", q.WitnessType), ("state", q.WitnessState)));
        }
    }

    private static void SharedVolumeIssues(List<ClusterIssue> issues, ClusterStatusReport report)
    {
        foreach (var v in report.SharedVolumes)
        {
            var subject = v.Path is { Length: > 0 } p ? p : v.Name;
            if (!Is(v.State, "Online"))
            {
                issues.Add(Issue(HealthLevel.Critical, "csv_not_online", $"Cluster shared volume {subject} is {v.State}: the VMs stored there cannot run.", subject, ("state", v.State)));
                continue;
            }
            if (v.Access is { Length: > 0 } access && access.Contains("Redirected", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(Issue(HealthLevel.Warning, "csv_redirected", $"Cluster shared volume {subject} is in redirected access ({access}) on {report.ReadByNode}: I/O goes through the owner node over the network (slow).",
                    subject, ("access", access), ("node", report.ReadByNode)));
            }
            if (v.MaintenanceMode == true)
            {
                issues.Add(Issue(HealthLevel.Warning, "csv_maintenance", $"Cluster shared volume {subject} is in maintenance mode.", subject));
            }
            if (v.SizeBytes is > 0 and var size && v.FreeBytes is { } free)
            {
                var ratio = (double)free / size;
                if (ratio < FreeCritical)
                {
                    issues.Add(Issue(HealthLevel.Critical, "csv_full", $"Cluster shared volume {subject} is almost full ({ratio:P0} free).", subject, ("free", free), ("size", size)));
                }
                else if (ratio < FreeWarning)
                {
                    issues.Add(Issue(HealthLevel.Warning, "csv_low_space", $"Cluster shared volume {subject} has little free space ({ratio:P0} free).", subject, ("free", free), ("size", size)));
                }
            }
        }
    }

    private static void StorageIssues(List<ClusterIssue> issues, ClusterStatusReport report, CapacityReport capacity)
    {
        if (report.Storage is not { S2dEnabled: true } s)
        {
            return;
        }
        foreach (var p in s.Pools)
        {
            if (p.IsReadOnly == true)
            {
                issues.Add(Issue(HealthLevel.Critical, "pool_read_only", $"Storage pool {p.Name} is read-only: too many disks or nodes are missing.", p.Name));
            }
            else if (!Healthy(p.HealthStatus))
            {
                issues.Add(Issue(Is(p.HealthStatus, "Unhealthy") ? HealthLevel.Critical : HealthLevel.Warning, "pool_not_healthy",
                    $"Storage pool {p.Name} is {p.HealthStatus} ({p.OperationalStatus}).", p.Name, ("health", p.HealthStatus), ("status", p.OperationalStatus)));
            }
        }
        foreach (var v in s.VirtualDisks)
        {
            if (!Healthy(v.HealthStatus))
            {
                issues.Add(Issue(Is(v.HealthStatus, "Unhealthy") ? HealthLevel.Critical : HealthLevel.Warning, "virtual_disk_not_healthy",
                    $"Virtual disk {v.Name} is {v.HealthStatus} ({v.OperationalStatus}): the data is not fully resilient.", v.Name,
                    ("health", v.HealthStatus), ("status", v.OperationalStatus)));
            }
        }
        foreach (var d in s.PhysicalDisks.Where(d => !Healthy(d.HealthStatus) || Is(d.Usage, "Retired")))
        {
            issues.Add(Issue(HealthLevel.Warning, "physical_disk_problem", $"Physical disk {d.Name} ({d.SerialNumber}) is {d.HealthStatus}, {d.OperationalStatus}, usage {d.Usage}: replace it.",
                d.Name, ("serial", d.SerialNumber), ("health", d.HealthStatus), ("status", d.OperationalStatus), ("usage", d.Usage)));
        }
        if (s.Jobs.Count > 0)
        {
            var percent = s.Jobs.Where(j => j.PercentComplete is not null).Select(j => j.PercentComplete!.Value).DefaultIfEmpty(0).Min();
            issues.Add(Issue(HealthLevel.Warning, "storage_repair_running", $"{s.Jobs.Count} storage repair job(s) running ({percent}% for the slowest): until they end the data is not fully resilient; do not take another node down.",
                null, ("jobs", s.Jobs.Count), ("percent", percent)));
        }
        if (capacity is { PoolFreeBytes: { } free, PoolReserveBytes: { } reserve } && free < reserve)
        {
            issues.Add(Issue(HealthLevel.Warning, "pool_reserve_low", "The storage pool has less unallocated capacity than one capacity disk per node (up to 4): after a disk failure the data cannot be repaired in place.",
                null, ("free", free), ("reserve", reserve)));
        }
    }

    private static void AgentIssues(List<ClusterIssue> issues, IReadOnlyList<NodeFacts> nodes, ClusterStatusReport? report)
    {
        foreach (var n in nodes)
        {
            if (n.HostId is null)
            {
                issues.Add(Issue(HealthLevel.Warning, "node_without_agent", $"Node {n.Name} has no HvPortal agent in the portal: install the agent on every node so that the portal keeps working when a node is down.", n.Name));
                continue;
            }
            if (report is not null && !report.Nodes.Any(r => Is(r.Name, n.Name)))
            {
                issues.Add(Issue(HealthLevel.Warning, "host_not_a_node", $"Host {n.HostName} is in this cluster in the portal but Windows does not list it as a node.", n.HostName));
                continue;
            }
            if (!n.AgentConnected && !Is(n.State, "Down"))
            {
                issues.Add(Issue(HealthLevel.Warning, "agent_disconnected", $"The agent of node {n.Name} is not connected: the cluster is managed through the other nodes; that node's VMs are not updated.", n.Name));
            }
            else if (n.AgentConnected && n.AgentProtocol < ClusterService.Protocol)
            {
                issues.Add(Issue(HealthLevel.Warning, "agent_outdated", $"The agent of node {n.Name} is older than 0.9.0 and cannot read the cluster: update it.", n.Name));
            }
        }
    }

    private static void CapacityIssues(List<ClusterIssue> issues, CapacityReport c)
    {
        if (c.ToleratesNodeLoss == false)
        {
            issues.Add(Issue(HealthLevel.Warning, "capacity_memory_n1",
                $"If node {c.WorstNode} stops, its highly available VMs need {Gb(c.FailoverNeededBytes)} GB of memory but the other nodes have {Gb(c.FailoverAvailableBytes)} GB free: some VMs would not restart.",
                c.WorstNode, ("needed", c.FailoverNeededBytes), ("available", c.FailoverAvailableBytes)));
        }
        if (c.VcpuRatioN1 is > VcpuRatioWarning and var ratio)
        {
            issues.Add(Issue(HealthLevel.Warning, "capacity_cpu_n1",
                $"With one node stopped the running VMs would have {ratio:0.0} vCPU per logical processor (more than {VcpuRatioWarning:0}): expect CPU contention.",
                null, ("ratio", ratio.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)), ("limit", VcpuRatioWarning)));
        }
    }

    private static string Gb(long? bytes) => bytes is { } b ? Math.Round(b / 1073741824.0, 1).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "?";

    // ------------------------------------------------------------------ capacity

    /// <summary>
    /// Memory and CPU of the cluster and whether it survives the loss of any one node: the memory of the running
    /// highly available VMs of each node must fit in what the other nodes that are up have free, minus a reserve for
    /// their own operating system (HostReserveBytes). Storage: shared volumes and, with S2D, the pool reserve.
    /// </summary>
    public static CapacityReport Capacity(IReadOnlyList<NodeFacts> nodes, ClusterStatusReport? report)
    {
        var up = nodes.Where(n => Is(n.State, "Up")).ToList();
        var known = nodes.Where(n => n.TotalMemoryBytes is not null).ToList();
        long? worstNeed = null, worstAvailable = null;
        string? worstNode = null;
        bool? tolerates = null;
        if (up.Count >= 1 && up.All(n => n.FreeMemoryBytes is not null))
        {
            tolerates = true;
            foreach (var n in nodes.Where(n => Is(n.State, "Up") || Is(n.State, "Paused")))
            {
                var others = up.Where(o => o != n).ToList();
                var available = others.Sum(o => Math.Max(0, o.FreeMemoryBytes!.Value - HostReserveBytes));
                var need = n.ClusteredVmMemoryBytes;
                if (need == 0)
                {
                    continue;
                }
                var deficit = need - available;
                if (worstNeed is null || deficit > worstNeed - worstAvailable)
                {
                    (worstNeed, worstAvailable, worstNode) = (need, available, n.Name);
                }
                if (need > available)
                {
                    tolerates = false;
                }
            }
        }
        var lpUp = up.Sum(n => n.LogicalProcessors ?? 0);
        var vcpus = nodes.Sum(n => n.RunningVcpus);
        var largest = up.Select(n => n.LogicalProcessors ?? 0).DefaultIfEmpty(0).Max();
        long? poolFree = null, poolReserve = null;
        if (report?.Storage is { S2dEnabled: true } s && s.Pools.Count > 0)
        {
            poolFree = s.Pools.Sum(p => Math.Max(0, (p.SizeBytes ?? 0) - (p.AllocatedBytes ?? 0)));
            var capacityDisk = s.PhysicalDisks.Where(d => !Is(d.Usage, "Journal") && !Is(d.Usage, "Retired")).Select(d => d.SizeBytes ?? 0).DefaultIfEmpty(0).Max();
            var nodeCount = report.Nodes.Count;
            poolReserve = capacityDisk > 0 && nodeCount > 0 ? capacityDisk * Math.Min(nodeCount, 4) : null;
        }
        var csvs = report?.SharedVolumes.Where(v => v.SizeBytes is not null).ToList() ?? [];
        return new CapacityReport
        {
            Nodes = nodes.Count,
            NodesUp = up.Count,
            TotalMemoryBytes = known.Sum(n => n.TotalMemoryBytes ?? 0),
            FreeMemoryBytes = known.Sum(n => n.FreeMemoryBytes ?? 0),
            VmMemoryBytes = nodes.Sum(n => n.RunningVmMemoryBytes),
            LogicalProcessors = lpUp,
            RunningVcpus = vcpus,
            VcpuRatio = lpUp > 0 ? Math.Round((double)vcpus / lpUp, 2) : null,
            VcpuRatioN1 = up.Count > 1 && lpUp - largest > 0 ? Math.Round((double)vcpus / (lpUp - largest), 2) : null,
            WorstNode = worstNode,
            FailoverNeededBytes = worstNeed,
            FailoverAvailableBytes = worstAvailable,
            ToleratesNodeLoss = up.Count <= 1 && nodes.Any(n => n.ClusteredVmMemoryBytes > 0) ? false : tolerates,
            SharedVolumeBytes = csvs.Count > 0 ? csvs.Sum(v => v.SizeBytes ?? 0) : null,
            SharedVolumeFreeBytes = csvs.Count > 0 ? csvs.Sum(v => v.FreeBytes ?? 0) : null,
            PoolFreeBytes = poolFree,
            PoolReserveBytes = poolReserve,
        };
    }

    // ------------------------------------------------------------------ storage

    /// <summary>
    /// Whether node <paramref name="nodeName"/> may become the owner of the cluster shared volume <paramref name="volume"/>
    /// now: the volume exists and is online, the node is a node that is up and does not own it already. Null = allowed.
    /// </summary>
    public static ClusterIssue? CheckSharedVolumeMove(string volume, string nodeName, ClusterStatusReport? report)
    {
        if (report is null)
        {
            return Issue(HealthLevel.Unknown, "status_unreadable", "The cluster could not be read from any node: its state cannot be checked.", null, ("error", "—"));
        }
        var csv = report.SharedVolumes.FirstOrDefault(v => Is(v.Name, volume));
        if (csv is null)
        {
            return Issue(HealthLevel.Warning, "csv_not_found", $"There is no cluster shared volume named {volume}.", volume);
        }
        if (!Is(csv.State, "Online"))
        {
            return Issue(HealthLevel.Critical, "csv_not_online", $"Cluster shared volume {csv.Path ?? volume} is {csv.State}.", csv.Path ?? volume, ("state", csv.State));
        }
        var node = report.Nodes.FirstOrDefault(n => Is(n.Name, nodeName));
        if (node is null || !Is(node.State, "Up"))
        {
            return Issue(HealthLevel.Warning, "csv_owner_not_up", $"Node {nodeName} is {node?.State ?? "not a node of the cluster"}: only a node that is up can own the volume.",
                nodeName, ("state", node?.State));
        }
        return Is(csv.OwnerNode, nodeName)
            ? Issue(HealthLevel.Warning, "csv_already_owner", $"{nodeName} already owns {csv.Path ?? volume}.", nodeName)
            : null;
    }

    // ------------------------------------------------------------------ maintenance

    /// <summary>
    /// Whether node <paramref name="nodeName"/> may enter or leave maintenance now. Blocking problems cannot be overridden
    /// in the portal (Failover Cluster Manager stays available to administrators); warnings must be acknowledged.
    /// </summary>
    public static MaintenanceCheck CheckMaintenance(string action, string nodeName, ClusterStatusReport? report, IReadOnlyList<NodeFacts> nodes)
    {
        var blocking = new List<ClusterIssue>();
        var warnings = new List<ClusterIssue>();
        if (report is null)
        {
            blocking.Add(Issue(HealthLevel.Unknown, "status_unreadable", "The cluster could not be read from any node: its state cannot be checked.", null, ("error", "—")));
            return new MaintenanceCheck(false, blocking, warnings);
        }
        var node = report.Nodes.FirstOrDefault(n => Is(n.Name, nodeName));
        if (node is null)
        {
            blocking.Add(Issue(HealthLevel.Critical, "not_a_node", $"{nodeName} is not a node of cluster {report.Name}.", nodeName));
            return new MaintenanceCheck(false, blocking, warnings);
        }
        if (action == MaintenanceActions.Exit)
        {
            if (!Is(node.State, "Paused") && node.StorageMaintenance != true)
            {
                blocking.Add(Issue(HealthLevel.Warning, "not_in_maintenance", $"Node {nodeName} is not in maintenance ({node.State}).", nodeName, ("state", node.State)));
            }
            if (report.Storage is { S2dEnabled: true })
            {
                warnings.Add(Issue(Info, "resync_after_exit", "After the node is back, Storage Spaces Direct copies the changes made meanwhile to its disks: wait for the repair jobs to end before maintaining another node."));
            }
            return new MaintenanceCheck(blocking.Count == 0, blocking, warnings);
        }

        if (Is(node.State, "Down"))
        {
            blocking.Add(Issue(HealthLevel.Critical, "node_down", $"Node {nodeName} is down: there is nothing to drain.", nodeName));
        }
        else if (Is(node.State, "Paused"))
        {
            blocking.Add(Issue(HealthLevel.Warning, "already_in_maintenance", $"Node {nodeName} is already paused.", nodeName));
        }
        foreach (var other in report.Nodes.Where(n => !Is(n.Name, nodeName) && !Is(n.State, "Up")))
        {
            blocking.Add(Issue(HealthLevel.Critical, "other_node_not_up", $"Node {other.Name} is {other.State}: only one node at a time may be out of service.", other.Name, ("state", other.State)));
        }
        if (report.Nodes.Count < 2)
        {
            blocking.Add(Issue(HealthLevel.Critical, "single_node", "The cluster has a single node: its VMs have nowhere to go."));
        }
        if (report.Storage is { S2dEnabled: true } s)
        {
            if (s.Jobs.Count > 0)
            {
                var slowest = s.Jobs.Where(j => j.PercentComplete is not null).Select(j => j.PercentComplete!.Value).DefaultIfEmpty(0).Min();
                blocking.Add(Issue(HealthLevel.Warning, "storage_repair_running", "Storage Spaces Direct is still repairing data (storage jobs running): wait until they end, otherwise the data could become unavailable.",
                    null, ("jobs", s.Jobs.Count), ("percent", slowest)));
            }
            foreach (var v in s.VirtualDisks.Where(v => !Healthy(v.HealthStatus)))
            {
                blocking.Add(Issue(HealthLevel.Warning, "virtual_disk_not_healthy", $"Virtual disk {v.Name} is {v.HealthStatus}: taking a node down now could make it unavailable.", v.Name,
                    ("health", v.HealthStatus), ("status", v.OperationalStatus)));
            }
            foreach (var p in s.Pools.Where(p => p.IsReadOnly == true || Is(p.HealthStatus, "Unhealthy")))
            {
                blocking.Add(Issue(HealthLevel.Critical, "pool_not_healthy", $"Storage pool {p.Name} is {p.HealthStatus}.", p.Name, ("health", p.HealthStatus), ("status", p.OperationalStatus)));
            }
        }
        foreach (var v in report.SharedVolumes.Where(v => !Is(v.State, "Online")))
        {
            blocking.Add(Issue(HealthLevel.Critical, "csv_not_online", $"Cluster shared volume {v.Path ?? v.Name} is {v.State}.", v.Path ?? v.Name, ("state", v.State)));
        }
        if (report.Quorum is { } q && q.WitnessType is { } wt && !Is(wt, "None") && !Is(q.WitnessState, "Online"))
        {
            (report.Nodes.Count <= 2 ? blocking : warnings).Add(Issue(HealthLevel.Warning, "witness_not_online",
                $"The witness {q.WitnessName} is {q.WitnessState}: with a node in maintenance the cluster tolerates no other failure.", q.WitnessName, ("type", q.WitnessType), ("state", q.WitnessState)));
        }
        else if (report.Quorum is { } q2 && (q2.WitnessType is null || Is(q2.WitnessType, "None")) && report.Nodes.Count == 2)
        {
            warnings.Add(Issue(HealthLevel.Warning, "no_witness_two_nodes", "A two-node cluster without a witness: while this node is in maintenance the cluster depends on the other node alone."));
        }

        // Memory: the highly available VMs of the node must fit on the other nodes that are up.
        var facts = nodes.FirstOrDefault(n => Is(n.Name, nodeName));
        var others = nodes.Where(n => !Is(n.Name, nodeName) && Is(n.State, "Up")).ToList();
        if (facts is not null && facts.ClusteredVmMemoryBytes > 0)
        {
            if (others.Count == 0 || others.Any(o => o.FreeMemoryBytes is null))
            {
                warnings.Add(Issue(HealthLevel.Unknown, "memory_unknown", "The free memory of the other nodes is not known (their agents did not report it): the portal cannot check that the VMs fit."));
            }
            else
            {
                var available = others.Sum(o => Math.Max(0, o.FreeMemoryBytes!.Value - HostReserveBytes));
                if (facts.ClusteredVmMemoryBytes > available)
                {
                    blocking.Add(Issue(HealthLevel.Critical, "insufficient_memory",
                        $"The highly available VMs of {nodeName} need {Gb(facts.ClusteredVmMemoryBytes)} GB of memory, the other nodes have {Gb(available)} GB free: the drain would fail. Stop or move some VMs first.",
                        nodeName, ("needed", facts.ClusteredVmMemoryBytes), ("available", available)));
                }
            }
        }
        if (facts is { NotClusteredRunningVms.Count: > 0 })
        {
            warnings.Add(Issue(HealthLevel.Warning, "vms_not_highly_available",
                $"These running VMs are not highly available and will NOT be moved: {string.Join(", ", facts.NotClusteredRunningVms)}. They stop when the node is restarted.",
                nodeName, ("vms", string.Join(", ", facts.NotClusteredRunningVms)), ("count", facts.NotClusteredRunningVms.Count)));
        }
        foreach (var r in report.Roles.Where(r => Is(r.State, "Failed")))
        {
            warnings.Add(Issue(HealthLevel.Warning, "role_failed", $"Role {r.Name} has failed: check it before the maintenance.", r.Name));
        }
        if (facts is { AgentConnected: false })
        {
            warnings.Add(Issue(Info, "agent_disconnected", $"The agent of {nodeName} is not connected: the maintenance runs through another node.", nodeName));
        }
        return new MaintenanceCheck(blocking.Count == 0, blocking, warnings);
    }
}
