using HvPortal.Gateway.Agents;
using HvPortal.Gateway.Api;
using HvPortal.Gateway.Audit;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Configuration;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Realtime;
using HvPortal.Gateway.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HvPortal.Gateway.VmGroups;

/// <summary>Native Hyper-V VM groups of a host (Get-VMGroup), their mirroring status and the one-time import (ADR-017).</summary>
public static class VmGroupEndpoints
{
    public static void Map(WebApplication app)
    {
        var hosts = app.MapGroup("/api/v1/hosts").WithTags("Hosts");
        hosts.MapGet("/{id:guid}/vm-groups", ListAsync);
        hosts.MapPost("/{id:guid}/vm-groups/sync", SyncAsync);
        hosts.MapPost("/{id:guid}/vm-groups/import", ImportAsync);
    }

    private static async Task<ManagedHost?> HostAsync(Guid id, HttpContext http, PortalDbContext db) =>
        await db.Hosts.AsNoTracking().FirstOrDefaultAsync(h => h.Id == id && h.Status == HostStatus.Active, http.RequestAborted);

    private static IResult Unavailable(Guid id, IAgentDispatcher agents) =>
        !agents.IsConnected(id)
            ? ApiSupport.Error(StatusCodes.Status503ServiceUnavailable, "host_unreachable", "The host agent is not connected.")
            : ApiSupport.Error(StatusCodes.Status501NotImplemented, "agent_outdated", "Native VM groups need agent 0.3.2 or later on this host. Update the agent.");

    private static async Task<IResult> ListAsync(Guid id, HttpContext http, PortalDbContext db, AccessEvaluator access, IAgentDispatcher agents,
        VmGroupSyncService sync, IOptions<GatewayOptions> options)
    {
        var host = await HostAsync(id, http, db);
        if (host is null || !await access.HasAsync(http.User.UserId(), Permissions.InventoryRead, ResourceRef.Host(id), http.RequestAborted))
        {
            return ApiSupport.NotFound();
        }
        // adoptedAt null with autoImport on = the groups already on the host are still to be imported automatically.
        var status = new { syncedAt = host.VmGroupsSyncedAt, error = host.VmGroupsSyncError, adoptedAt = host.VmGroupsAdoptedAt, autoImport = options.Value.VmGroups.AutoImport };
        if (!agents.IsConnected(id) || !VmGroupSyncService.Supported(agents, id))
        {
            return Results.Ok(new { supported = false, connected = agents.IsConnected(id), status, groups = Array.Empty<object>() });
        }
        var (native, error) = await sync.ListAsync(id, http.RequestAborted);
        if (native is null)
        {
            return ApiSupport.Error(StatusCodes.Status502BadGateway, "host_operation_failed", error);
        }
        var links = await db.HostVmGroupLinks.AsNoTracking().Where(l => l.HostId == id).ToListAsync(http.RequestAborted);
        var vms = await db.VirtualMachines.AsNoTracking().Where(v => v.HostId == id && !v.IsMissing).Select(v => new { v.Id, v.HyperVId, v.Name }).ToListAsync(http.RequestAborted);
        var vmByHv = vms.GroupBy(v => v.HyperVId).ToDictionary(g => g.Key, g => g.First());
        var names = native.ToDictionary(n => n.Id, n => n.Name);
        return Results.Ok(new
        {
            supported = true,
            connected = true,
            status,
            groups = native.OrderBy(n => n.GroupType).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase).Select(n =>
            {
                var link = links.FirstOrDefault(l => l.NativeId == n.Id);
                return new
                {
                    n.Id,
                    n.Name,
                    n.GroupType,
                    managed = link is not null,
                    portalGroupId = link?.GroupId,
                    vms = n.VmIds.Select(hv => vmByHv.TryGetValue(hv, out var v) ? new { id = (Guid?)v.Id, name = v.Name } : new { id = (Guid?)null, name = hv.ToString("D") }),
                    groups = n.GroupIds.Select(g => names.TryGetValue(g, out var nm) ? nm : g.ToString("D")),
                };
            }),
        });
    }

    private static async Task<IResult> SyncAsync(Guid id, HttpContext http, PortalDbContext db, IAgentDispatcher agents, VmGroupSyncService sync)
    {
        if (await http.GuardAsync(Permissions.GroupsManage, ResourceRef.Global, "hosts.vmgroups.sync.request") is { } denied)
        {
            return denied;
        }
        if (await HostAsync(id, http, db) is null)
        {
            return ApiSupport.NotFound();
        }
        if (!agents.IsConnected(id) || !VmGroupSyncService.Supported(agents, id))
        {
            return Unavailable(id, agents);
        }
        var outcome = await sync.SyncHostAsync(id, force: true, http.RequestAborted);
        await http.WriteAuditAsync("hosts.vmgroups.sync.request", outcome.Errors.Count == 0 ? AuditOutcome.Success : AuditOutcome.Failure,
            "host", id.ToString("D"), new { outcome.Status, applied = outcome.Applied.Count, errors = outcome.Errors.Count });
        return Results.Ok(outcome);
    }

    private static async Task<IResult> ImportAsync(Guid id, HttpContext http, PortalDbContext db, IAgentDispatcher agents, VmGroupSyncService sync,
        IRealtimeNotifier notifier, AccessEvaluator access, TimeProvider time)
    {
        // Creates portal groups and memberships (groups.manage) from a host's configuration (hosts.manage).
        if (await http.GuardAsync(Permissions.HostsManage, ResourceRef.Global, "hosts.vmgroups.import") is { } denied)
        {
            return denied;
        }
        if (await http.GuardAsync(Permissions.GroupsManage, ResourceRef.Global, "hosts.vmgroups.import") is { } denied2)
        {
            return denied2;
        }
        var host = await db.Hosts.FirstOrDefaultAsync(h => h.Id == id && h.Status == HostStatus.Active, http.RequestAborted);
        if (host is null)
        {
            return ApiSupport.NotFound();
        }
        if (!agents.IsConnected(id) || !VmGroupSyncService.Supported(agents, id))
        {
            return Unavailable(id, agents);
        }
        var (native, error) = await sync.ListAsync(id, http.RequestAborted);
        if (native is null)
        {
            return ApiSupport.Error(StatusCodes.Status502BadGateway, "host_operation_failed", error);
        }
        // An import on request settles the host: the automatic import of existing groups has nothing left to decide.
        host.VmGroupsAdoptedAt ??= time.GetUtcNow();
        var result = await VmGroupImporter.ImportAsync(db, host, native, time, http.RequestAborted);
        access.Invalidate();
        await http.WriteAuditAsync("hosts.vmgroups.import", resourceType: "host", resourceId: id.ToString("D"), details: result);
        await notifier.GroupsChangedAsync();
        var outcome = await sync.SyncHostAsync(id, force: true, http.RequestAborted);
        return Results.Ok(new { result, sync = outcome });
    }
}
