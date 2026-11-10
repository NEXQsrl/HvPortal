using System.Text.Json;
using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Clusters;
using HvPortal.Gateway.Data;
using Microsoft.EntityFrameworkCore;

namespace HvPortal.Gateway.Jobs;

/// <summary>Protocol 11 jobs (v0.9.0, ADR-041): maintenance of cluster nodes, VM moves inside a cluster.</summary>
public sealed partial class JobRunner
{
    /// <summary>
    /// The node agent that runs a cluster job: the node itself when its agent is connected (protocol 11), otherwise any
    /// other connected node of the same cluster (a node can be paused or resumed from any node). Null = none available.
    /// </summary>
    private async Task<Guid?> ClusterExecutorAsync(PortalDbContext db, ManagedHost target, int protocol, CancellationToken ct)
    {
        if (_dispatcher.IsConnected(target.Id) && _dispatcher.ProtocolVersionOf(target.Id) >= protocol)
        {
            return target.Id;
        }
        if (target.ClusterId is not { } clusterId)
        {
            return null;
        }
        var nodes = await db.Hosts.AsNoTracking().Where(h => h.ClusterId == clusterId && h.Status == HostStatus.Active && h.Id != target.Id
            && h.ConnectionMode == HostConnectionMode.Agent).OrderBy(h => h.DisplayName).ToListAsync(ct);
        return nodes.FirstOrDefault(h => _dispatcher.IsConnected(h.Id) && _dispatcher.ProtocolVersionOf(h.Id) >= protocol)?.Id;
    }

    /// <summary>
    /// Server-controlled fields of protocol 11 operations, from the portal's records now (node names from the inventory of
    /// the nodes, never from the request), and the maintenance checks run again against the live cluster.
    /// </summary>
    private static async Task<((string Code, string Message)? Error, JsonElement Args)> StampProtocol11Async(IServiceProvider services, PortalDbContext db,
        HvOperation op, JobService.OperationParameters parameters, VirtualMachine? vm, ManagedHost host, CancellationToken ct)
    {
        var args = parameters.Args;
        switch (op)
        {
            case HvOperation.SetClusterNodeMaintenance when HvOperations.FromElement<ClusterNodeMaintenanceRequest>(args) is { } request:
            {
                if (host.ClusterId is not { } clusterId || await db.Clusters.FirstOrDefaultAsync(c => c.Id == clusterId, ct) is not { } cluster)
                {
                    return (("not_in_cluster", "The host is no longer a node of a cluster in the portal."), args);
                }
                if (ClusterService.NodeNameOf(host) is not { } nodeName)
                {
                    return (("node_name_unknown", "The cluster node name of the host is not known yet (no inventory from its agent)."), args);
                }
                var stamped = request with { NodeName = nodeName };
                if (OperationValidation.Validate(stamped) is { } invalid)
                {
                    return ((invalid, "Invalid maintenance request."), args);
                }
                // The cluster may have changed since the request (another node down, a repair started): checked again now.
                var clusters = services.GetRequiredService<ClusterService>();
                var read = await clusters.ReadStatusAsync(cluster, ct);
                var facts = await clusters.NodeFactsAsync(await clusters.NodesAsync(clusterId, ct), read.Report, ct);
                var check = ClusterRules.CheckMaintenance(stamped.Action, nodeName, read.Report, facts);
                if (!check.Allowed)
                {
                    return (("maintenance_blocked", "Not done, the cluster is not ready: " + string.Join(" ", check.Blocking.Select(b => b.Message))), args);
                }
                return (null, HvOperations.ToElement(stamped));
            }
            case HvOperation.MoveClusterSharedVolume when HvOperations.FromElement<MoveSharedVolumeRequest>(args) is { } csvMove:
            {
                // Target = the node that becomes the owner: its node name from its inventory, the volume checked on the live cluster.
                if (host.ClusterId is not { } clusterId || await db.Clusters.FirstOrDefaultAsync(c => c.Id == clusterId, ct) is not { } cluster)
                {
                    return (("not_in_cluster", "The host is no longer a node of a cluster in the portal."), args);
                }
                if (ClusterService.NodeNameOf(host) is not { } nodeName)
                {
                    return (("node_name_unknown", "The cluster node name of the host is not known yet (no inventory from its agent)."), args);
                }
                var stamped = csvMove with { NodeName = nodeName };
                var read = await services.GetRequiredService<ClusterService>().ReadStatusAsync(cluster, ct);
                if (ClusterRules.CheckSharedVolumeMove(stamped.VolumeName, nodeName, read.Report) is { } refused)
                {
                    return (("csv_move_refused", refused.Message), args);
                }
                return (null, HvOperations.ToElement(stamped));
            }
            case HvOperation.MoveClusterVm when vm is not null && HvOperations.FromElement<MoveClusterVmRequest>(args) is { } move:
            {
                if (parameters.DestinationHostId is not { } destinationId
                    || await db.Hosts.AsNoTracking().FirstOrDefaultAsync(h => h.Id == destinationId && h.Status == HostStatus.Active, ct) is not { } destination)
                {
                    return (("destination_not_found", "The destination node is no longer managed by the portal."), args);
                }
                if (host.ClusterId is null || destination.ClusterId != host.ClusterId)
                {
                    return (("not_same_cluster", "The destination host is no longer a node of the VM's cluster."), args);
                }
                if (ClusterService.NodeNameOf(destination) is not { } nodeName)
                {
                    return (("node_name_unknown", "The cluster node name of the destination is not known yet (no inventory from its agent)."), args);
                }
                if (await services.GetRequiredService<Quotas.GroupPolicyService>().CheckMoveAsync(vm, destinationId, null, ct) is { } violation)
                {
                    return ((violation.Code, violation.Message), args);
                }
                return (null, HvOperations.ToElement(move with { VmId = vm.HyperVId, NodeName = nodeName }));
            }
            default:
                return (null, args);
        }
    }

    /// <summary>A VM moved by the cluster keeps its Hyper-V id: its portal record follows it to the destination node.</summary>
    private async Task<string?> ApplyProtocol11ResultAsync(PortalDbContext db, HvOperation op, JobService.OperationParameters parameters, VirtualMachine? vm,
        JobStatus status, HvOperationOutcome? outcome, string? message)
    {
        try
        {
            if (op == HvOperation.MoveClusterVm && status == JobStatus.Succeeded && vm is not null && parameters.DestinationHostId is { } destination)
            {
                await MigrationRules.ApplyMoveAsync(db, vm, destination, _time.GetUtcNow(), CancellationToken.None);
                await _notifier.GroupsChangedAsync();
                return $"Moved to {MigrationRules.DestinationLabel(await db.Hosts.AsNoTracking().FirstOrDefaultAsync(h => h.Id == destination))}.";
            }
            if (op == HvOperation.MoveClusterSharedVolume && status == JobStatus.Succeeded && outcome?.Result is { ValueKind: JsonValueKind.Object } csv)
            {
                var owner = csv.TryGetProperty("ownerNode", out var o) ? o.GetString() : null;
                var moved = csv.TryGetProperty("moved", out var m) && m.ValueKind == JsonValueKind.True;
                var volume = csv.TryGetProperty("volumeName", out var v) ? v.GetString() : null;
                return moved ? $"{volume}: owner is now {owner}." : $"No change needed: {volume} was already owned by {owner}.";
            }
            if (op == HvOperation.SetClusterNodeMaintenance && status == JobStatus.Succeeded
                && HvOperations.FromElement<ClusterNodeMaintenanceResult>(outcome?.Result) is { } result)
            {
                var done = $"Node {result.NodeName}: {result.State}" + (result.DrainStatus is { Length: > 0 } d && d != "NotInitiated" ? $", drain {d}" : "")
                           + (result.StorageMaintenance ? ", disks in storage maintenance" : "") + ".";
                return result.Warning is { Length: > 0 } w ? done + " " + w : done;
            }
        }
        catch (DbUpdateException ex)
        {
            _logger.LogWarning(ex, "Could not record the outcome of a cluster job");
        }
        return message;
    }
}
