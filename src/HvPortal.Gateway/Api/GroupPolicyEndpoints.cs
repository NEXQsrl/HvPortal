using System.Text.Json;
using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Agents;
using HvPortal.Gateway.Audit;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Quotas;
using HvPortal.Gateway.Realtime;
using HvPortal.Gateway.Security;
using Microsoft.EntityFrameworkCore;

namespace HvPortal.Gateway.Api;

/// <summary>
/// Limits of a VM folder (ADR-024): quotas (VMs, vCPU, memory, disk, private networks), the hosts opened to the folder's
/// users, the networks and the storage paths its VMs may use. Reading needs access to the folder; changing them needs
/// groups.manage and hosts.manage, because the limits decide which host resources folder users can reach.
/// </summary>
public static class GroupPolicyEndpoints
{
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/groups/{id:guid}/policy").WithTags("Groups");
        g.MapGet("/", GetAsync);
        g.MapPut("/", SetAsync);
    }

    private static List<string> SwitchNames(ManagedHost host)
    {
        if (host.HostFactsJson is null)
        {
            return [];
        }
        var facts = JsonSerializer.Deserialize<HostFacts>(host.HostFactsJson);
        return facts?.VirtualSwitches.Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList() ?? [];
    }

    private static async Task<IResult> GetAsync(Guid id, bool? live, HttpContext http, PortalDbContext db, AccessEvaluator access, GroupPolicyService policies,
        IAgentDispatcher agents)
    {
        var ct = http.RequestAborted;
        var uid = http.User.UserId();
        var group = await db.LogicalGroups.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        var manage = await access.HasAsync(uid, Permissions.GroupsManage, ResourceRef.Global, ct);
        if (group is null || !(manage || await access.HasAsync(uid, Permissions.InventoryRead, ResourceRef.Group(id), ct)))
        {
            // Unknown and unreadable folders are indistinguishable.
            return ApiSupport.NotFound();
        }
        var chain = await policies.ChainAsync(id, ct);
        var levels = new List<object>();
        foreach (var level in chain)
        {
            var usage = await policies.UsageAsync(level.GroupId, live == true, null, ct);
            levels.Add(new
            {
                groupId = level.GroupId,
                name = level.Name,
                policy = level.Policy,
                usage = new
                {
                    usage.Vms,
                    usage.Processors,
                    usage.MemoryMb,
                    diskGb = (usage.DiskBytes + (1L << 30) - 1) / (1L << 30),
                    usage.DiskUnknownVms,
                    usage.Networks,
                },
            });
        }
        var canCreateVm = await access.HasAsync(uid, Permissions.VmCreate, ResourceRef.Group(id), ct);
        var canCreateNetwork = await access.HasAsync(uid, Permissions.NetworkManage, ResourceRef.Group(id), ct);
        var canReadFiles = await access.HasAsync(uid, Permissions.HostFilesRead, ResourceRef.Group(id), ct);
        var canWriteFiles = await access.HasAsync(uid, Permissions.HostFilesWrite, ResourceRef.Group(id), ct);
        var delegated = GroupPolicyService.DelegatedHosts(chain) ?? [];
        var hostRows = await db.Hosts.AsNoTracking().Where(h => delegated.Contains(h.Id) && h.Status == HostStatus.Active).OrderBy(h => h.DisplayName).ToListAsync(ct);
        var hosts = new List<object>();
        foreach (var h in hostRows)
        {
            var view = await policies.HostViewAsync(id, h.Id, SwitchNames(h), ct);
            var connected = agents.IsConnected(h.Id);
            var ready = connected && h.Access == HyperVAccessStatus.Ok;
            hosts.Add(new
            {
                h.Id,
                h.DisplayName,
                connected,
                ready,
                switches = view.Switches,
                networksRestricted = view.NetworksRestricted,
                storageRoots = view.StorageRoots,
                canCreateVm = canCreateVm && ready,
                canCreateNetwork = canCreateNetwork && ready && agents.ProtocolVersionOf(h.Id) >= 7,
                canBrowseFiles = canReadFiles && connected && view.StorageRoots.Count > 0 && agents.ProtocolVersionOf(h.Id) >= 5,
                canUploadFiles = canWriteFiles && connected && view.StorageRoots.Count > 0 && agents.ProtocolVersionOf(h.Id) >= 5,
            });
        }
        var tree = await GroupTree.LoadAsync(db, ct);
        var subtree = tree.Subtree(id);
        var groupNames = await db.LogicalGroups.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var hostNames = await db.Hosts.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.DisplayName, ct);
        var owned = await db.GroupNetworks.AsNoTracking().Where(n => subtree.Contains(n.GroupId)).OrderBy(n => n.SwitchName).ToListAsync(ct);
        var networks = new List<object>();
        foreach (var n in owned)
        {
            networks.Add(new
            {
                n.Id,
                n.HostId,
                hostName = hostNames.GetValueOrDefault(n.HostId),
                name = n.SwitchName,
                n.SwitchId,
                n.GroupId,
                groupName = groupNames.GetValueOrDefault(n.GroupId),
                n.CreatedAt,
                canDelete = n.SwitchId is not null && (await access.HasAsync(uid, Permissions.NetworkManage, ResourceRef.Host(n.HostId), ct)
                                                        || await policies.HoldsViaGroupAsync(uid, Permissions.NetworkManage, n.GroupId, n.HostId, ct)),
            });
        }
        return Results.Ok(new
        {
            policy = GroupPolicy.Parse(group.PolicyJson),
            canEdit = manage && await access.HasAsync(uid, Permissions.HostsManage, ResourceRef.Global, ct),
            canCreateVm,
            canCreateNetwork,
            levels,
            hosts,
            networks,
        });
    }

    private static async Task<IResult> SetAsync(Guid id, GroupPolicy? req, HttpContext http, PortalDbContext db, IRealtimeNotifier notifier)
    {
        if ((await http.GuardAsync(Permissions.GroupsManage, ResourceRef.Global, "groups.policy.update")
             ?? await http.GuardAsync(Permissions.HostsManage, ResourceRef.Global, "groups.policy.update")) is { } denied)
        {
            return denied;
        }
        var ct = http.RequestAborted;
        var group = await db.LogicalGroups.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (group is null)
        {
            return ApiSupport.NotFound();
        }
        var policy = (req ?? new GroupPolicy()).Normalize();
        if (policy.Validate() is { } invalid)
        {
            return ApiSupport.BadRequest(invalid);
        }
        var referenced = (policy.Hosts ?? []).Concat((policy.Networks ?? []).Select(n => n.HostId)).Concat((policy.Storage ?? []).Select(s => s.HostId)).Distinct().ToList();
        var known = await db.Hosts.AsNoTracking().Where(h => referenced.Contains(h.Id)).Select(h => h.Id).ToListAsync(ct);
        if (known.Count != referenced.Count)
        {
            return ApiSupport.BadRequest("unknown_host");
        }
        var before = GroupPolicy.Parse(group.PolicyJson);
        group.PolicyJson = policy.IsEmpty ? null : policy.Serialize();
        if (group.PolicyJson is { Length: > 32000 })
        {
            return ApiSupport.BadRequest("too_many_entries");
        }
        await db.SaveChangesAsync(ct);
        await http.WriteAuditAsync("groups.policy.update", resourceType: "group", resourceId: id.ToString("D"),
            details: new { group.Name, before, after = policy.IsEmpty ? null : policy });
        await notifier.GroupsChangedAsync();
        return Results.Ok(new { policy = policy.IsEmpty ? null : policy });
    }
}
