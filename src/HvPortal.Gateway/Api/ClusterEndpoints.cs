using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Agents;
using HvPortal.Gateway.Audit;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Clusters;
using HvPortal.Gateway.Configuration;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Jobs;
using HvPortal.Gateway.Pki;
using HvPortal.Gateway.Realtime;
using HvPortal.Gateway.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HvPortal.Gateway.Api;

/// <summary>
/// Failover clusters (ADR-041): a cluster of the portal groups the hosts that are nodes of the same Windows cluster; each
/// node has its own agent. Creating clusters and choosing their nodes needs hosts.manage (+ recent re-authentication);
/// reading the cluster status needs inventory.read on the cluster; node maintenance needs hosts.maintenance on the node.
/// </summary>
public static class ClusterEndpoints
{
    public const int MaxNodes = 64;

    public sealed record CreateClusterRequest(string? Name, string? Description, string[]? NewNodes, Guid[]? HostIds);
    public sealed record UpdateClusterRequest(string? Name, string? Description);
    public sealed record AddNodeRequest(Guid? HostId, string? DisplayName);
    public sealed record MaintenanceRequest(string? Action, bool Failback = false, bool StorageMaintenance = true, bool AcknowledgeWarnings = false,
        string? IdempotencyKey = null);

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/clusters").WithTags("Clusters");
        g.MapGet("/", ListAsync);
        g.MapPost("/", CreateAsync);
        g.MapGet("/{id:guid}", GetAsync);
        g.MapGet("/{id:guid}/status", StatusAsync);
        g.MapPut("/{id:guid}", UpdateAsync);
        g.MapPost("/{id:guid}/nodes", AddNodeAsync);
        g.MapPost("/{id:guid}/nodes/{hostId:guid}/remove", RemoveNodeAsync);
        g.MapPost("/{id:guid}/delete", DeleteAsync);
        var hosts = app.MapGroup("/api/v1/hosts").WithTags("Clusters");
        hosts.MapGet("/{id:guid}/maintenance", CheckMaintenanceAsync);
        hosts.MapPost("/{id:guid}/maintenance", MaintenanceAsync);
    }

    // ------------------------------------------------------------------ read

    /// <summary>Clusters whose status the user may read, or with at least one node the user can see (name and nodes only).</summary>
    private static async Task<IResult> ListAsync(HttpContext http, PortalDbContext db, AccessEvaluator access, IAgentDispatcher agents)
    {
        var uid = http.User.UserId();
        var manage = await access.HasAsync(uid, Permissions.HostsManage, ResourceRef.Global, http.RequestAborted);
        var clusters = await db.Clusters.AsNoTracking().OrderBy(c => c.Name).ToListAsync(http.RequestAborted);
        var nodes = (await db.Hosts.AsNoTracking().Where(h => h.ClusterId != null).Select(h => new { h.Id, h.ClusterId, h.Status }).ToListAsync(http.RequestAborted))
            .ToLookup(h => h.ClusterId!.Value);
        var list = new List<object>();
        foreach (var c in clusters)
        {
            var readStatus = await access.HasAsync(uid, Permissions.InventoryRead, ResourceRef.Cluster(c.Id), http.RequestAborted);
            var visibleNodes = new List<Guid>();
            foreach (var n in nodes[c.Id])
            {
                if (manage || readStatus || await access.CanSeeHostAsync(uid, n.Id, http.RequestAborted))
                {
                    visibleNodes.Add(n.Id);
                }
            }
            if (!manage && !readStatus && visibleNodes.Count == 0)
            {
                continue;
            }
            list.Add(new
            {
                c.Id,
                c.Name,
                c.Description,
                c.WindowsName,
                nodeIds = visibleNodes,
                nodesConnected = nodes[c.Id].Count(n => agents.IsConnected(n.Id)),
                nodesTotal = nodes[c.Id].Count(),
                status = readStatus ? c.LastStatusLevel : null,
                statusAt = readStatus ? c.LastStatusAt : null,
                can = new { manage, readStatus },
            });
        }
        return Results.Ok(list);
    }

    private static async Task<IResult> GetAsync(Guid id, HttpContext http, PortalDbContext db, AccessEvaluator access, ClusterService clusters,
        IAgentDispatcher agents, TimeProvider time) =>
        await DetailAsync(id, http, db, access, clusters, agents, time, live: false);

    /// <summary>Reads the cluster now (from the first node agent that answers) and returns the same view as GET /clusters/{id}.</summary>
    private static async Task<IResult> StatusAsync(Guid id, HttpContext http, PortalDbContext db, AccessEvaluator access, ClusterService clusters,
        IAgentDispatcher agents, TimeProvider time) =>
        await DetailAsync(id, http, db, access, clusters, agents, time, live: true);

    private static async Task<IResult> DetailAsync(Guid id, HttpContext http, PortalDbContext db, AccessEvaluator access, ClusterService clusters,
        IAgentDispatcher agents, TimeProvider time, bool live)
    {
        var uid = http.User.UserId();
        var cluster = await db.Clusters.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, http.RequestAborted);
        // Nodes, storage, quorum and capacity are information about the whole cluster: for who reads the cluster itself.
        if (cluster is null || !await access.HasAsync(uid, Permissions.InventoryRead, ResourceRef.Cluster(id), http.RequestAborted))
        {
            return ApiSupport.NotFound();
        }
        var hosts = await clusters.NodesAsync(id, http.RequestAborted);
        ClusterService.ClusterRead read;
        if (live)
        {
            read = await clusters.ReadStatusAsync(cluster, http.RequestAborted);
            cluster = await db.Clusters.AsNoTracking().FirstAsync(c => c.Id == id, http.RequestAborted);
        }
        else
        {
            read = new ClusterService.ClusterRead(ClusterService.LastReport(cluster), null, [], cluster.LastStatusError);
        }
        var report = read.Report;
        var facts = await clusters.NodeFactsAsync(hosts, report, http.RequestAborted);
        var capacity = ClusterRules.Capacity(facts, report);
        var issues = report is null && !live && cluster.LastStatusAt is null && cluster.LastStatusError is null
            ? [] // never read yet: the page asks for a live read
            : ClusterRules.Evaluate(report, facts, capacity, read.Error);
        var manage = await access.HasAsync(uid, Permissions.HostsManage, ResourceRef.Global, http.RequestAborted);
        var nodes = new List<object>();
        foreach (var n in facts)
        {
            var host = n.HostId is { } hid ? hosts.FirstOrDefault(h => h.Id == hid) : null;
            var hostFacts = host is null ? null : ClusterService.FactsOf(host);
            var reported = report?.Nodes.FirstOrDefault(r => string.Equals(r.Name, n.Name, StringComparison.OrdinalIgnoreCase));
            nodes.Add(new
            {
                n.Name,
                n.HostId,
                n.HostName,
                n.State,
                drainStatus = reported?.DrainStatus,
                storageMaintenance = reported?.StorageMaintenance,
                nodeWeight = reported?.NodeWeight,
                dynamicWeight = reported?.DynamicWeight,
                model = reported is null ? null : $"{reported.Manufacturer} {reported.Model}".Trim(),
                n.AgentConnected,
                agentVersion = host?.AgentVersion,
                agentReady = n.AgentConnected && n.AgentProtocol >= ClusterService.Protocol,
                n.TotalMemoryBytes,
                n.FreeMemoryBytes,
                n.LogicalProcessors,
                cpuLoadPercent = hostFacts?.CpuLoadPercent,
                n.RunningVms,
                n.RunningVcpus,
                n.RunningVmMemoryBytes,
                n.ClusteredVmMemoryBytes,
                n.NotClusteredRunningVms,
                clusterError = hostFacts?.Cluster?.Error,
                clusterMismatch = host is null ? null : InventoryEndpoints.ClusterMismatch(host, cluster, hostFacts),
                canMaintain = host is not null && await access.HasAsync(uid, Permissions.HostsMaintenance, ResourceRef.Host(host.Id), http.RequestAborted),
            });
        }
        // Hosts of the portal that could be added to this cluster: standalone hosts whose agent reports this Windows cluster.
        var suggested = manage && cluster.WindowsName is { } windowsName
            ? (await db.Hosts.AsNoTracking().Where(h => h.ClusterId == null && h.Status == HostStatus.Active).ToListAsync(http.RequestAborted))
                .Where(h => string.Equals(ClusterService.FactsOf(h)?.Cluster?.Name, windowsName, StringComparison.OrdinalIgnoreCase))
                .Select(h => new { h.Id, h.DisplayName }).ToList<object>()
            : [];
        return Results.Ok(new
        {
            cluster.Id,
            cluster.Name,
            cluster.Description,
            cluster.WindowsName,
            cluster.CreatedAt,
            can = new
            {
                manage,
                createVm = hosts.Count > 0 && await AnyAsync(hosts, h => access.HasAsync(uid, Permissions.VmCreate, ResourceRef.Host(h.Id), http.RequestAborted)),
            },
            nodes,
            suggestedHosts = suggested,
            status = new
            {
                level = issues.Count == 0 && report is null ? null : ClusterRules.Overall(issues),
                live,
                storedAt = cluster.LastStatusAt,
                collectedUtc = report?.CollectedUtc,
                readByNode = report?.ReadByNode,
                error = report is null ? read.Error ?? cluster.LastStatusError : null,
                attempts = read.Attempts,
                report,
                issues,
                capacity,
            },
        });
    }

    private static async Task<bool> AnyAsync(IEnumerable<ManagedHost> hosts, Func<ManagedHost, Task<bool>> test)
    {
        foreach (var h in hosts)
        {
            if (await test(h))
            {
                return true;
            }
        }
        return false;
    }

    // ------------------------------------------------------------------ manage

    private static async Task<IResult> CreateAsync(CreateClusterRequest req, HttpContext http, PortalDbContext db, EnrollmentService enrollment,
        InternalCertificateAuthority ca, IOptions<GatewayOptions> options, IRealtimeNotifier notifier, TimeProvider time)
    {
        if ((await http.GuardAsync(Permissions.HostsManage, ResourceRef.Global, "clusters.create") ?? await http.RequireRecentReauthAsync()) is { } denied)
        {
            return denied;
        }
        var name = req.Name?.Trim();
        if (name is null || !ApiSupport.DisplayNameRegex().IsMatch(name))
        {
            return ApiSupport.BadRequest("invalid_display_name");
        }
        var (description, descriptionOk) = InventoryManagementEndpoints.CleanDescription(req.Description);
        if (!descriptionOk)
        {
            return ApiSupport.BadRequest("description_too_long");
        }
        var newNodes = (req.NewNodes ?? []).Select(n => n?.Trim() ?? "").ToList();
        var existing = (req.HostIds ?? []).Distinct().ToList();
        if (newNodes.Count + existing.Count > MaxNodes)
        {
            return ApiSupport.BadRequest("too_many_nodes", $"At most {MaxNodes} nodes.");
        }
        if (newNodes.Any(n => !ApiSupport.DisplayNameRegex().IsMatch(n)) || newNodes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != newNodes.Count)
        {
            return ApiSupport.BadRequest("invalid_display_name", "Each new node needs a different valid name.");
        }
        if (await db.Clusters.AnyAsync(c => c.Name == name, http.RequestAborted))
        {
            return ApiSupport.Conflict("cluster_exists");
        }
        if (newNodes.Count > 0 && await db.Hosts.AnyAsync(h => newNodes.Contains(h.DisplayName), http.RequestAborted))
        {
            return ApiSupport.Conflict("host_exists", "A host with one of these names already exists: add it as an existing host.");
        }
        var attach = await db.Hosts.Where(h => existing.Contains(h.Id)).ToListAsync(http.RequestAborted);
        if (attach.Count != existing.Count)
        {
            return ApiSupport.BadRequest("unknown_host");
        }
        if (NodeProblem(attach) is { } problem)
        {
            return problem;
        }
        var now = time.GetUtcNow();
        var cluster = new HostCluster { Name = name, Description = description, CreatedAt = now };
        db.Clusters.Add(cluster);
        foreach (var h in attach)
        {
            h.ClusterId = cluster.Id;
        }
        var created = newNodes.Select(n => new ManagedHost { DisplayName = n, CreatedAt = now, ClusterId = cluster.Id }).ToList();
        db.Hosts.AddRange(created);
        await db.SaveChangesAsync(http.RequestAborted);
        var enrollments = new List<object>();
        foreach (var h in created)
        {
            var (token, expires) = await enrollment.CreateTokenAsync(h.Id, http.User.UserId(), http.RequestAborted);
            enrollments.Add(new { hostId = h.Id, h.DisplayName, enrollment = InventoryEndpoints.EnrollmentInfo(http, ca, options.Value, token, expires, clusterNode: true) });
        }
        await http.WriteAuditAsync("clusters.create", resourceType: "cluster", resourceId: cluster.Id.ToString("D"),
            details: new { cluster.Name, newNodes, existingNodes = attach.Select(h => h.DisplayName) });
        await notifier.ClustersChangedAsync();
        return Results.Ok(new { clusterId = cluster.Id, nodes = enrollments });
    }

    /// <summary>A host can join a cluster when it is not in another one and has its own agent (management agents do not read clusters).</summary>
    private static IResult? NodeProblem(IEnumerable<ManagedHost> hosts)
    {
        foreach (var h in hosts)
        {
            if (h.ClusterId is not null)
            {
                return ApiSupport.Conflict("already_in_cluster", $"{h.DisplayName} is already a node of a cluster.");
            }
            if (h.ConnectionMode == HostConnectionMode.Remote)
            {
                return ApiSupport.BadRequest("remote_host_not_supported", $"{h.DisplayName} is reached through a management agent: cluster nodes need their own agent.");
            }
            if (h.Status == HostStatus.Revoked)
            {
                return ApiSupport.BadRequest("host_revoked", $"{h.DisplayName} is revoked.");
            }
        }
        return null;
    }

    private static async Task<IResult> UpdateAsync(Guid id, UpdateClusterRequest req, HttpContext http, PortalDbContext db, IRealtimeNotifier notifier)
    {
        if (await http.GuardAsync(Permissions.HostsManage, ResourceRef.Global, "clusters.update") is { } denied)
        {
            return denied;
        }
        var cluster = await db.Clusters.FirstOrDefaultAsync(c => c.Id == id, http.RequestAborted);
        if (cluster is null)
        {
            return ApiSupport.NotFound();
        }
        var before = new { cluster.Name, cluster.Description };
        if (req.Name is not null)
        {
            var name = req.Name.Trim();
            if (!ApiSupport.DisplayNameRegex().IsMatch(name))
            {
                return ApiSupport.BadRequest("invalid_display_name");
            }
            if (await db.Clusters.AnyAsync(c => c.Id != id && c.Name == name, http.RequestAborted))
            {
                return ApiSupport.Conflict("cluster_exists");
            }
            cluster.Name = name;
        }
        if (req.Description is not null)
        {
            var (description, ok) = InventoryManagementEndpoints.CleanDescription(req.Description);
            if (!ok)
            {
                return ApiSupport.BadRequest("description_too_long");
            }
            cluster.Description = description;
        }
        await db.SaveChangesAsync(http.RequestAborted);
        await http.WriteAuditAsync("clusters.update", resourceType: "cluster", resourceId: id.ToString("D"),
            details: new { before, after = new { cluster.Name, cluster.Description } });
        await notifier.ClustersChangedAsync();
        return Results.NoContent();
    }

    /// <summary>Adds an existing host, or creates a new pending host (with its enrollment token) as a node of the cluster.</summary>
    private static async Task<IResult> AddNodeAsync(Guid id, AddNodeRequest req, HttpContext http, PortalDbContext db, EnrollmentService enrollment,
        InternalCertificateAuthority ca, IOptions<GatewayOptions> options, IRealtimeNotifier notifier, TimeProvider time)
    {
        if ((await http.GuardAsync(Permissions.HostsManage, ResourceRef.Global, "clusters.nodes.add") ?? await http.RequireRecentReauthAsync()) is { } denied)
        {
            return denied;
        }
        var cluster = await db.Clusters.FirstOrDefaultAsync(c => c.Id == id, http.RequestAborted);
        if (cluster is null)
        {
            return ApiSupport.NotFound();
        }
        if (await db.Hosts.CountAsync(h => h.ClusterId == id, http.RequestAborted) >= MaxNodes)
        {
            return ApiSupport.BadRequest("too_many_nodes", $"At most {MaxNodes} nodes.");
        }
        if (req.HostId is { } hostId)
        {
            var host = await db.Hosts.FirstOrDefaultAsync(h => h.Id == hostId, http.RequestAborted);
            if (host is null)
            {
                return ApiSupport.BadRequest("unknown_host");
            }
            if (NodeProblem([host]) is { } problem)
            {
                return problem;
            }
            host.ClusterId = id;
            await db.SaveChangesAsync(http.RequestAborted);
            await http.WriteAuditAsync("clusters.nodes.add", resourceType: "cluster", resourceId: id.ToString("D"), details: new { host = host.DisplayName, hostId });
            await notifier.ClustersChangedAsync();
            return Results.Ok(new { hostId });
        }
        var name = req.DisplayName?.Trim();
        if (name is null || !ApiSupport.DisplayNameRegex().IsMatch(name))
        {
            return ApiSupport.BadRequest("invalid_display_name");
        }
        if (await db.Hosts.AnyAsync(h => h.DisplayName == name, http.RequestAborted))
        {
            return ApiSupport.Conflict("host_exists");
        }
        var created = new ManagedHost { DisplayName = name, CreatedAt = time.GetUtcNow(), ClusterId = id };
        db.Hosts.Add(created);
        await db.SaveChangesAsync(http.RequestAborted);
        var (token, expires) = await enrollment.CreateTokenAsync(created.Id, http.User.UserId(), http.RequestAborted);
        await http.WriteAuditAsync("clusters.nodes.add", resourceType: "cluster", resourceId: id.ToString("D"),
            details: new { host = name, hostId = created.Id, newHost = true, tokenExpires = expires });
        await notifier.ClustersChangedAsync();
        return Results.Ok(new { hostId = created.Id, enrollment = InventoryEndpoints.EnrollmentInfo(http, ca, options.Value, token, expires, clusterNode: true) });
    }

    /// <summary>The host stays in the portal as a standalone host; nothing changes in Windows.</summary>
    private static async Task<IResult> RemoveNodeAsync(Guid id, Guid hostId, HttpContext http, PortalDbContext db, IRealtimeNotifier notifier)
    {
        if ((await http.GuardAsync(Permissions.HostsManage, ResourceRef.Global, "clusters.nodes.remove") ?? await http.RequireRecentReauthAsync()) is { } denied)
        {
            return denied;
        }
        var host = await db.Hosts.FirstOrDefaultAsync(h => h.Id == hostId && h.ClusterId == id, http.RequestAborted);
        if (host is null)
        {
            return ApiSupport.NotFound();
        }
        host.ClusterId = null;
        await db.SaveChangesAsync(http.RequestAborted);
        await http.WriteAuditAsync("clusters.nodes.remove", resourceType: "cluster", resourceId: id.ToString("D"), details: new { host = host.DisplayName, hostId });
        await notifier.ClustersChangedAsync();
        return Results.NoContent();
    }

    /// <summary>Deletes the cluster of the portal (its hosts stay, as standalone hosts) and the role assignments made on it.</summary>
    private static async Task<IResult> DeleteAsync(Guid id, HttpContext http, PortalDbContext db, IRealtimeNotifier notifier)
    {
        if ((await http.GuardAsync(Permissions.HostsManage, ResourceRef.Global, "clusters.delete") ?? await http.RequireRecentReauthAsync()) is { } denied)
        {
            return denied;
        }
        var cluster = await db.Clusters.FirstOrDefaultAsync(c => c.Id == id, http.RequestAborted);
        if (cluster is null)
        {
            return ApiSupport.NotFound();
        }
        var scopeId = id.ToString("D");
        var nodes = await db.Hosts.Where(h => h.ClusterId == id).ExecuteUpdateAsync(u => u.SetProperty(h => h.ClusterId, (Guid?)null), http.RequestAborted);
        var assignments = await db.RoleAssignments.Where(a => a.ScopeType == ScopeType.Cluster && a.ScopeId == scopeId).ExecuteDeleteAsync(http.RequestAborted);
        db.Clusters.Remove(cluster);
        await db.SaveChangesAsync(http.RequestAborted);
        await http.WriteAuditAsync("clusters.delete", resourceType: "cluster", resourceId: scopeId, details: new { cluster.Name, nodes, roleAssignmentsRemoved = assignments });
        await notifier.ClustersChangedAsync();
        return Results.NoContent();
    }

    // ------------------------------------------------------------------ maintenance

    private static async Task<(ManagedHost? Host, HostCluster? Cluster, IResult? Error)> MaintenanceTargetAsync(Guid id, HttpContext http, PortalDbContext db,
        AccessEvaluator access, string auditAction)
    {
        var host = await db.Hosts.AsNoTracking().FirstOrDefaultAsync(h => h.Id == id && h.Status == HostStatus.Active, http.RequestAborted);
        if (host is null || !await access.CanSeeHostAsync(http.User.UserId(), id, http.RequestAborted))
        {
            return (null, null, ApiSupport.NotFound());
        }
        if (await http.GuardAsync(Permissions.HostsMaintenance, ResourceRef.Host(id), auditAction) is { } denied)
        {
            return (null, null, denied);
        }
        if (host.ClusterId is not { } clusterId || await db.Clusters.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clusterId, http.RequestAborted) is not { } cluster)
        {
            return (null, null, ApiSupport.Conflict("not_in_cluster", "Maintenance is for nodes of a failover cluster: add the host to its cluster first."));
        }
        return (host, cluster, null);
    }

    private static async Task<(MaintenanceCheck Check, ClusterService.ClusterRead Read, string? NodeName)> RunCheckAsync(string action, ManagedHost host,
        HostCluster cluster, ClusterService clusters, CancellationToken ct)
    {
        var read = await clusters.ReadStatusAsync(cluster, ct);
        var facts = await clusters.NodeFactsAsync(await clusters.NodesAsync(cluster.Id, ct), read.Report, ct);
        var nodeName = ClusterService.NodeNameOf(host);
        var check = nodeName is null
            ? new MaintenanceCheck(false, [new ClusterIssue(HealthLevel.Unknown, "node_name_unknown", "The cluster node name of the host is not known yet (no inventory from its agent).")], [])
            : ClusterRules.CheckMaintenance(action, nodeName, read.Report, facts);
        return (check, read, nodeName);
    }

    private static string? NormalizeAction(string? action) =>
        MaintenanceActions.All.FirstOrDefault(a => string.Equals(a, action, StringComparison.OrdinalIgnoreCase));

    /// <summary>What would block or need confirmation if the node entered (or left) maintenance now. Read-only.</summary>
    private static async Task<IResult> CheckMaintenanceAsync(Guid id, string? action, HttpContext http, PortalDbContext db, AccessEvaluator access, ClusterService clusters)
    {
        if (NormalizeAction(action) is not { } normalized)
        {
            return ApiSupport.BadRequest("invalid_maintenance_action", "action is Enter or Exit.");
        }
        var (host, cluster, error) = await MaintenanceTargetAsync(id, http, db, access, "host.maintenance.check");
        if (error is not null)
        {
            return error;
        }
        var (check, read, nodeName) = await RunCheckAsync(normalized, host!, cluster!, clusters, http.RequestAborted);
        return Results.Ok(new
        {
            action = normalized,
            nodeName,
            check.Allowed,
            check.Blocking,
            check.Warnings,
            s2d = read.Report?.Storage?.S2dEnabled == true,
            readError = read.Report is null ? read.Error : null,
        });
    }

    private static async Task<IResult> MaintenanceAsync(Guid id, MaintenanceRequest req, HttpContext http, PortalDbContext db, AccessEvaluator access,
        ClusterService clusters, JobService jobs)
    {
        if (NormalizeAction(req.Action) is not { } action)
        {
            return ApiSupport.BadRequest("invalid_maintenance_action", "action is Enter or Exit.");
        }
        var (host, cluster, error) = await MaintenanceTargetAsync(id, http, db, access, "host.maintenance.request");
        if (error is not null)
        {
            return error;
        }
        var key = VmOperationsEndpoints.IdempotencyKey(http, req.IdempotencyKey);
        if (key == "!")
        {
            return ApiSupport.BadRequest("invalid_idempotency_key");
        }
        var (check, _, nodeName) = await RunCheckAsync(action, host!, cluster!, clusters, http.RequestAborted);
        if (!check.Allowed)
        {
            await http.WriteAuditAsync("host.maintenance.request", AuditOutcome.Denied, "host", id.ToString("D"),
                new { action, reason = "maintenance_blocked", blocking = check.Blocking.Select(b => b.Code) });
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "maintenance_blocked",
                detail: string.Join(" ", check.Blocking.Select(b => b.Message)),
                extensions: new Dictionary<string, object?> { ["code"] = "maintenance_blocked", ["blocking"] = check.Blocking, ["warnings"] = check.Warnings });
        }
        if (check.Warnings.Any(w => w.Level != ClusterRules.Info) && !req.AcknowledgeWarnings)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "maintenance_warnings",
                detail: "Confirm the warnings first: " + string.Join(" ", check.Warnings.Select(w => w.Message)),
                extensions: new Dictionary<string, object?> { ["code"] = "maintenance_warnings", ["warnings"] = check.Warnings });
        }
        var spec = new ClusterNodeMaintenanceRequest
        {
            NodeName = nodeName!,
            Action = action,
            Failback = action == MaintenanceActions.Exit && req.Failback,
            StorageMaintenance = req.StorageMaintenance,
        };
        if (OperationValidation.Validate(spec) is { } invalid)
        {
            return ApiSupport.BadRequest(invalid);
        }
        // Draining live-migrates every role of the node one after the other: up to 4 hours.
        var timeout = action == MaintenanceActions.Enter ? 4 * 3600 : 1800;
        var label = action == MaintenanceActions.Enter ? $"Put {host!.DisplayName} in maintenance" : $"Bring {host!.DisplayName} back from maintenance";
        return await VmOperationsEndpoints.QueueAsync(http, jobs, new JobService.OperationJobSpec(JobService.MaintenanceJobType, action, ScopeType.Host, id,
            host.DisplayName, id, Permissions.HostsMaintenance, new JobService.OperationParameters(HvOperations.ToElement(spec), ClusterId: cluster!.Id), timeout, label),
            key, "host.maintenance.request", new { action, node = nodeName, cluster = cluster.Name, spec.Failback, spec.StorageMaintenance, warningsAcknowledged = check.Warnings.Select(w => w.Code) });
    }
}
