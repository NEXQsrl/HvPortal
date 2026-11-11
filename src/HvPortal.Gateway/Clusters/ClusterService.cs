using System.Text.Json;
using HvPortal.Contracts.Agent;
using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Agents;
using HvPortal.Gateway.Data;
using Microsoft.EntityFrameworkCore;

namespace HvPortal.Gateway.Clusters;

/// <summary>
/// Failover clusters (ADR-041). Every node has its own agent; a cluster-wide read (GetClusterStatus) or operation is
/// sent to one connected node agent and, when that node does not answer, to the next one: the portal keeps managing
/// the cluster while a node or its agent is down (high availability of the agents).
/// </summary>
public sealed class ClusterService(PortalDbContext db, IAgentDispatcher agents, TimeProvider time, ILogger<ClusterService> logger, StorageJobTracker storageJobs,
    Microsoft.Extensions.Options.IOptions<Configuration.GatewayOptions> options)
{
    /// <summary>Storage jobs of the cluster as last seen, with what became of those no longer listed (0.9.4).</summary>
    public IReadOnlyList<TrackedStorageJob> StorageJobs(Guid clusterId, ClusterStatusReport? report)
    {
        var tracked = storageJobs.Get(clusterId);
        // Nothing seen since the gateway started: the jobs of the stored report, as they were when it was read.
        return tracked.Count > 0 || report is null || StorageJobTracker.JobsUnreadable(report)
            ? tracked
            : StorageJobTracker.Merge([], report.Storage!.Jobs, report.CollectedUtc);
    }

    /// <summary>
    /// A stored status older than this says nothing about now: three monitor intervals (at least 10 minutes), so that a
    /// monitor pass that is late or a single failed read does not hide the last good status.
    /// </summary>
    public TimeSpan StaleAfter => TimeSpan.FromSeconds(Math.Max(600, 3 * Math.Clamp(options.Value.Clusters.MonitorIntervalSeconds, 30, 3600)));

    /// <summary>
    /// Why the stored status cannot be taken for the present state (0.9.4), or null: no node agent is connected, or the
    /// last read is too old. A cluster that cannot be read is Unknown, never healthy.
    /// </summary>
    public ClusterIssue? Staleness(HostCluster cluster, IReadOnlyList<ManagedHost> nodes)
    {
        if (nodes.Count == 0)
        {
            return null; // nothing to read yet: the cluster has no nodes in the portal
        }
        if (!nodes.Any(n => agents.IsConnected(n.Id)))
        {
            return new ClusterIssue(HealthLevel.Unknown, "no_node_connected",
                "No node agent is connected: the state of the cluster is not known" + (cluster.LastStatusAt is { } at ? $" (last read {at:u})." : "."),
                null, new Dictionary<string, string> { ["at"] = cluster.LastStatusAt?.ToString("o") ?? "" });
        }
        if (cluster.LastStatusAt is { } last && time.GetUtcNow() - last > StaleAfter && cluster.LastStatusError is null)
        {
            return new ClusterIssue(HealthLevel.Unknown, "status_stale", $"The cluster has not been read since {last:u}: its state now is not known.",
                null, new Dictionary<string, string> { ["at"] = last.ToString("o") });
        }
        return null;
    }

    /// <summary>Agent protocol needed for clusters (agent 0.9.0).</summary>
    public const int Protocol = 11;
    private const int StatusTimeoutSeconds = 120;

    public sealed record ClusterRead(ClusterStatusReport? Report, Guid? ReadByHostId, IReadOnlyList<string> Attempts, string? Error);

    internal static HostFacts? FactsOf(ManagedHost h)
    {
        if (h.HostFactsJson is null)
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<HostFacts>(h.HostFactsJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Name of the host as a node of its cluster: what its agent read, else the computer name.</summary>
    public static string? NodeNameOf(ManagedHost h, HostFacts? facts = null)
    {
        facts ??= FactsOf(h);
        var name = facts?.Cluster?.NodeName ?? facts?.ComputerName ?? h.MachineName;
        return ClusterNames.IsValidNodeName(name) ? name : null;
    }

    public Task<List<ManagedHost>> NodesAsync(Guid clusterId, CancellationToken ct) =>
        db.Hosts.AsNoTracking().Where(h => h.ClusterId == clusterId && h.Status == HostStatus.Active).OrderBy(h => h.DisplayName).ToListAsync(ct);

    /// <summary>
    /// Node agents able to answer for the cluster, best first: connected with protocol 11, own agent (not a remote host),
    /// node Up rather than paused, the preferred host first when given.
    /// </summary>
    public IReadOnlyList<ManagedHost> Candidates(IEnumerable<ManagedHost> nodes, Guid? preferred = null) =>
        nodes.Where(h => h.ConnectionMode == HostConnectionMode.Agent && agents.IsConnected(h.Id) && agents.ProtocolVersionOf(h.Id) >= Protocol)
            .OrderBy(h => h.Id == preferred ? 0 : 1)
            .ThenBy(h => string.Equals(FactsOf(h)?.Cluster?.NodeState, "Up", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(h => h.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Reads the cluster from the first node agent that answers; the snapshot is stored on the cluster record.</summary>
    public async Task<ClusterRead> ReadStatusAsync(HostCluster cluster, CancellationToken ct)
    {
        var nodes = await NodesAsync(cluster.Id, ct);
        var attempts = new List<string>();
        ClusterRead read = new(null, null, attempts, nodes.Count == 0 ? "The cluster has no nodes in the portal." : "No node agent 0.9.0 or later is connected.");
        foreach (var host in Candidates(nodes))
        {
            try
            {
                var r = await agents.SendAsync(host.Id, new AgentCommand
                {
                    CommandId = Guid.NewGuid(),
                    Kind = AgentCommandKind.HyperVOperation,
                    Operation = HvOperation.GetClusterStatus,
                    OperationArgs = HvOperations.ToElement(new { }),
                    TimeoutSeconds = StatusTimeoutSeconds,
                }, null, TimeSpan.FromSeconds(StatusTimeoutSeconds + 15), ct);
                if (r.Operation is { Ok: true } ok && HvOperations.FromElement<ClusterStatusReport>(ok.Result) is { } report)
                {
                    read = new ClusterRead(report, host.Id, attempts, null);
                    break;
                }
                var error = r.Operation?.Error ?? r.Message ?? r.Status.ToString();
                attempts.Add($"{host.DisplayName}: {error}");
                read = read with { Error = error };
            }
            catch (Exception ex) when (ex is HostNotConnectedException or AgentChannelLostException or TimeoutException)
            {
                // That node is gone or did not answer: try the next one.
                attempts.Add($"{host.DisplayName}: {ex.Message}");
                read = read with { Error = ex.Message };
            }
        }
        if (read.Report is { } readReport)
        {
            storageJobs.Observe(cluster.Id, readReport, time.GetUtcNow());
        }
        await StoreAsync(cluster.Id, read, nodes, ct);
        return read;
    }

    private async Task StoreAsync(Guid clusterId, ClusterRead read, List<ManagedHost> nodes, CancellationToken ct)
    {
        var tracked = await db.Clusters.FirstOrDefaultAsync(c => c.Id == clusterId, ct);
        if (tracked is null)
        {
            return;
        }
        var now = time.GetUtcNow();
        if (read.Report is { } report)
        {
            var json = JsonSerializer.Serialize(report, AgentProtocol.Json);
            tracked.LastStatusJson = json.Length <= 256000 ? json : null;
            tracked.LastStatusAt = now;
            tracked.LastStatusError = null;
            if (report.Name is { Length: > 0 } windowsName)
            {
                tracked.WindowsName = windowsName.Length <= 255 ? windowsName : windowsName[..255];
            }
        }
        else
        {
            tracked.LastStatusError = Truncate(string.Join(" | ", read.Attempts.DefaultIfEmpty(read.Error ?? "unknown")), 2000);
        }
        var facts = await NodeFactsAsync(nodes, read.Report, ct);
        var capacity = ClusterRules.Capacity(facts, read.Report);
        tracked.LastStatusLevel = ClusterRules.Overall(ClusterRules.Evaluate(read.Report, facts, capacity, read.Error));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            logger.LogWarning(ex, "Could not store the status of cluster {ClusterId}", clusterId);
        }
    }

    /// <summary>
    /// Problems of the last stored read (no agent call): all of them, and those about each node of the portal (issue
    /// subject = node name or host name). Notes (Info) are left out.
    /// </summary>
    public async Task<(List<ClusterIssue> Issues, Dictionary<Guid, List<ClusterIssue>> ByHost)> StoredProblemsAsync(HostCluster cluster, CancellationToken ct)
    {
        var report = LastReport(cluster);
        var hosts = await NodesAsync(cluster.Id, ct);
        var stale = Staleness(cluster, hosts);
        if (report is null && cluster.LastStatusError is null)
        {
            return (stale is null ? [] : [stale], []);
        }
        var facts = await NodeFactsAsync(hosts, report, ct);
        var issues = ClusterRules.Evaluate(report, facts, ClusterRules.Capacity(facts, report), cluster.LastStatusError)
            .Where(i => i.Level != ClusterRules.Info).ToList();
        if (stale is not null)
        {
            issues.Insert(0, stale);
        }
        // Most serious first (the lists shown elsewhere are cut): Critical, Warning, Unknown.
        issues = issues.OrderBy(i => i.Level switch { HealthLevel.Critical => 0, HealthLevel.Warning => 1, _ => 2 }).ToList();
        var byHost = new Dictionary<Guid, List<ClusterIssue>>();
        foreach (var n in facts.Where(n => n.HostId is not null))
        {
            var mine = issues.Where(i => i.Subject is { } s
                && (s.Equals(n.Name, StringComparison.OrdinalIgnoreCase) || s.Equals(n.HostName, StringComparison.OrdinalIgnoreCase))).ToList();
            if (mine.Count > 0)
            {
                byHost[n.HostId!.Value] = mine;
            }
        }
        return (issues, byHost);
    }

    public static ClusterStatusReport? LastReport(HostCluster cluster)
    {
        if (cluster.LastStatusJson is null)
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<ClusterStatusReport>(cluster.LastStatusJson, AgentProtocol.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The nodes as the gateway sees them: every node listed by Windows (with its portal host when there is one) plus
    /// portal hosts of the cluster that Windows does not list; memory and VMs from the latest inventories.
    /// </summary>
    public async Task<List<NodeFacts>> NodeFactsAsync(IReadOnlyList<ManagedHost> hosts, ClusterStatusReport? report, CancellationToken ct)
    {
        var ids = hosts.Select(h => h.Id).ToList();
        var vms = await db.VirtualMachines.AsNoTracking().Where(v => ids.Contains(v.HostId) && !v.IsMissing).ToListAsync(ct);
        var list = new List<NodeFacts>();
        var used = new HashSet<Guid>();
        foreach (var n in report?.Nodes ?? [])
        {
            var host = hosts.FirstOrDefault(h => string.Equals(NodeNameOf(h), n.Name, StringComparison.OrdinalIgnoreCase));
            if (host is not null)
            {
                used.Add(host.Id);
            }
            list.Add(Build(n.Name, host, n.State, vms));
        }
        foreach (var h in hosts.Where(h => !used.Contains(h.Id)))
        {
            var facts = FactsOf(h);
            // Without a report the state comes from the node's own inventory (and nothing when its agent is gone).
            var state = report is null && agents.IsConnected(h.Id) ? facts?.Cluster?.NodeState : null;
            list.Add(Build(NodeNameOf(h, facts) ?? h.DisplayName, h, state, vms));
        }
        return list;
    }

    private NodeFacts Build(string name, ManagedHost? host, string? state, List<VirtualMachine> vms)
    {
        var facts = host is null ? null : FactsOf(host);
        var running = host is null ? [] : vms.Where(v => v.HostId == host.Id && IsActive(v.PowerState)).ToList();
        static long Memory(VirtualMachine v) => (v.MemoryAssignedMb ?? v.MemoryStartupMb ?? 0) * 1024L * 1024L;
        return new NodeFacts
        {
            Name = name,
            HostId = host?.Id,
            HostName = host?.DisplayName,
            AgentConnected = host is not null && agents.IsConnected(host.Id),
            AgentProtocol = host is null ? 0 : agents.ProtocolVersionOf(host.Id),
            State = state,
            TotalMemoryBytes = facts?.TotalPhysicalMemoryBytes,
            FreeMemoryBytes = facts?.FreePhysicalMemoryBytes,
            LogicalProcessors = facts?.LogicalProcessorCount,
            // Not known yet (older agent): counted as highly available, so the check errs on the safe side.
            ClusteredVmMemoryBytes = running.Where(v => v.IsClustered != false).Sum(Memory),
            RunningVmMemoryBytes = running.Sum(Memory),
            RunningVcpus = running.Sum(v => v.ProcessorCount ?? 0),
            RunningVms = running.Count,
            NotClusteredRunningVms = running.Where(v => v.IsClustered == false).Select(v => v.Name).Order(StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    private static bool IsActive(VmPowerState s) => s is VmPowerState.Running or VmPowerState.Paused or VmPowerState.Starting or VmPowerState.Stopping
        or VmPowerState.Saving or VmPowerState.Pausing or VmPowerState.Resuming or VmPowerState.Resetting;

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}
