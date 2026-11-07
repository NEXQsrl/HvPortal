using System.Text.Json;
using HvPortal.Contracts.Agent;
using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Agents;
using HvPortal.Gateway.Audit;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Jobs;
using HvPortal.Gateway.Networks;
using HvPortal.Gateway.Security;
using Microsoft.EntityFrameworkCore;

namespace HvPortal.Gateway.Api;

/// <summary>
/// Networks of the portal (ADR-030): a name for "switch + access VLAN" on a host. Choosing the network for a VM adapter
/// (new VM, added adapter, adapter change) connects it to the switch with that VLAN. Changing the VLAN of a network can
/// also re-tag the adapters that are on it now (one configuration job per VM, as the requesting user).
/// </summary>
public static class NetworkEndpoints
{
    public sealed record NetworkBody(string? Name, string? SwitchName, int? VlanId, string? Description, bool UpdateAdapters = false);

    public static void Map(WebApplication app)
    {
        var hosts = app.MapGroup("/api/v1/hosts").WithTags("Networks");
        hosts.MapGet("/{id:guid}/networks", HostNetworksAsync);
        hosts.MapPost("/{id:guid}/networks", CreateAsync);
        var nets = app.MapGroup("/api/v1/networks").WithTags("Networks");
        nets.MapGet("/", AllAsync);
        nets.MapPut("/{id:guid}", UpdateAsync);
        nets.MapDelete("/{id:guid}", DeleteAsync);
    }

    internal static object Dto(PortalNetwork n, string? hostName, bool canManage) => new
    {
        n.Id, n.HostId, hostName, n.Name, n.SwitchName, n.VlanId, n.Description, n.CreatedAt, canManage,
    };

    private static async Task<IResult> HostNetworksAsync(Guid id, HttpContext http, PortalDbContext db, AccessEvaluator access)
    {
        var uid = http.User.UserId();
        var host = await db.Hosts.AsNoTracking().FirstOrDefaultAsync(h => h.Id == id, http.RequestAborted);
        if (host is null || !await access.CanSeeHostAsync(uid, id, http.RequestAborted))
        {
            return ApiSupport.NotFound();
        }
        var manage = await access.HasAsync(uid, Permissions.NetworkManage, ResourceRef.Host(id), http.RequestAborted);
        var list = await db.PortalNetworks.AsNoTracking().Where(n => n.HostId == id).OrderBy(n => n.Name).ToListAsync(http.RequestAborted);
        return Results.Ok(list.Select(n => Dto(n, host.DisplayName, manage)));
    }

    private static async Task<IResult> AllAsync(HttpContext http, PortalDbContext db, AccessEvaluator access)
    {
        var uid = http.User.UserId();
        var hosts = await db.Hosts.AsNoTracking().ToDictionaryAsync(h => h.Id, h => h.DisplayName, http.RequestAborted);
        var result = new List<object>();
        foreach (var group in (await db.PortalNetworks.AsNoTracking().ToListAsync(http.RequestAborted)).GroupBy(n => n.HostId))
        {
            if (!await access.CanSeeHostAsync(uid, group.Key, http.RequestAborted))
            {
                continue;
            }
            var manage = await access.HasAsync(uid, Permissions.NetworkManage, ResourceRef.Host(group.Key), http.RequestAborted);
            result.AddRange(group.OrderBy(n => n.Name).Select(n => Dto(n, hosts.GetValueOrDefault(n.HostId), manage)));
        }
        return Results.Ok(result);
    }

    private static string? Validate(NetworkBody b, bool creating)
    {
        if (!NetworkRules.IsValidName(b.Name?.Trim())) return "invalid_network_name";
        if (creating && !OperationValidation.IsValidSwitchName(b.SwitchName)) return "invalid_switch_name";
        if (!OperationValidation.IsValidVlanId(b.VlanId)) return "invalid_vlan";
        if (b.Description is { Length: > 500 }) return "description_too_long";
        return null;
    }

    private static async Task<IResult> CreateAsync(Guid id, NetworkBody body, HttpContext http, PortalDbContext db, TimeProvider time)
    {
        var host = await db.Hosts.AsNoTracking().FirstOrDefaultAsync(h => h.Id == id && h.Status == HostStatus.Active, http.RequestAborted);
        if (host is null)
        {
            return ApiSupport.NotFound();
        }
        if (await http.GuardAsync(Permissions.NetworkManage, ResourceRef.Host(id), "network.portal.create") is { } denied)
        {
            return denied;
        }
        if (Validate(body, creating: true) is { } invalid)
        {
            return ApiSupport.BadRequest(invalid);
        }
        var name = body.Name!.Trim();
        if (!VmOperationsEndpoints.SwitchNames(host).Contains(body.SwitchName!, StringComparer.Ordinal))
        {
            return ApiSupport.BadRequest("unknown_switch", "The virtual switch does not exist on the host.");
        }
        if (await db.PortalNetworks.CountAsync(n => n.HostId == id, http.RequestAborted) >= NetworkRules.MaxNetworksPerHost)
        {
            return ApiSupport.Conflict("too_many_networks");
        }
        if (await db.PortalNetworks.AnyAsync(n => n.HostId == id && n.Name == name, http.RequestAborted))
        {
            return ApiSupport.Conflict("network_name_exists", "A network with this name already exists on the host.");
        }
        if (await db.PortalNetworks.AnyAsync(n => n.HostId == id && n.SwitchName == body.SwitchName && n.VlanId == body.VlanId, http.RequestAborted))
        {
            return ApiSupport.Conflict("network_exists", "A network with the same switch and VLAN already exists on the host.");
        }
        var n = new PortalNetwork
        {
            HostId = id, Name = name, SwitchName = body.SwitchName!, VlanId = body.VlanId, Description = string.IsNullOrWhiteSpace(body.Description) ? null : body.Description.Trim(),
            CreatedAt = time.GetUtcNow(), CreatedByUserId = http.User.UserId(),
        };
        db.PortalNetworks.Add(n);
        await db.SaveChangesAsync(http.RequestAborted);
        await http.WriteAuditAsync("network.portal.create", resourceType: "host", resourceId: id.ToString("D"), details: new { networkId = n.Id, n.Name, n.SwitchName, n.VlanId });
        return Results.Ok(Dto(n, host.DisplayName, true));
    }

    private static async Task<IResult> UpdateAsync(Guid id, NetworkBody body, HttpContext http, PortalDbContext db, AccessEvaluator access, IAgentDispatcher agents,
        JobService jobs)
    {
        var n = await db.PortalNetworks.FirstOrDefaultAsync(x => x.Id == id, http.RequestAborted);
        if (n is null || !await access.CanSeeHostAsync(http.User.UserId(), n.HostId, http.RequestAborted))
        {
            return ApiSupport.NotFound();
        }
        if (await http.GuardAsync(Permissions.NetworkManage, ResourceRef.Host(n.HostId), "network.portal.update") is { } denied)
        {
            return denied;
        }
        if (Validate(body with { SwitchName = n.SwitchName }, creating: false) is { } invalid)
        {
            return ApiSupport.BadRequest(invalid);
        }
        var name = body.Name!.Trim();
        if (await db.PortalNetworks.AnyAsync(x => x.HostId == n.HostId && x.Id != id && x.Name == name, http.RequestAborted))
        {
            return ApiSupport.Conflict("network_name_exists", "A network with this name already exists on the host.");
        }
        if (await db.PortalNetworks.AnyAsync(x => x.HostId == n.HostId && x.Id != id && x.SwitchName == n.SwitchName && x.VlanId == body.VlanId, http.RequestAborted))
        {
            return ApiSupport.Conflict("network_exists", "A network with the same switch and VLAN already exists on the host.");
        }
        var before = new { n.Name, n.VlanId, n.Description };
        var oldVlan = n.VlanId;
        // Adapters on the network now: same switch and same access VLAN (read from the host before the change).
        var retag = new List<(VirtualMachine Vm, List<string> Adapters)>();
        var skipped = new List<string>();
        if (body.UpdateAdapters && oldVlan != body.VlanId)
        {
            var host = await db.Hosts.AsNoTracking().FirstAsync(h => h.Id == n.HostId, http.RequestAborted);
            if (!agents.IsConnected(host.Id))
            {
                return ApiSupport.Error(StatusCodes.Status503ServiceUnavailable, "host_unreachable", "The host agent is not connected: the adapters cannot be read.");
            }
            var (found, unread, error) = await AdaptersOnAsync(db, agents, host, n.SwitchName, oldVlan ?? 0, http.RequestAborted);
            if (error is not null)
            {
                return error;
            }
            // VMs whose adapters could not be read (or beyond the limit of one change) are listed, never skipped silently.
            skipped.AddRange(unread);
            foreach (var (vm, adapters) in found)
            {
                if (await access.HasAsync(http.User.UserId(), Permissions.VmConfigNetwork, ResourceRef.Vm(vm.Id), http.RequestAborted))
                {
                    retag.Add((vm, adapters));
                }
                else
                {
                    skipped.Add(vm.Name);
                }
            }
        }
        n.Name = name;
        n.VlanId = body.VlanId;
        n.Description = string.IsNullOrWhiteSpace(body.Description) ? null : body.Description.Trim();
        await db.SaveChangesAsync(http.RequestAborted);
        var queued = new List<object>();
        foreach (var (vm, adapters) in retag)
        {
            // The adapters name the network: switch and VLAN are taken from it again when the job runs.
            var change = new VmConfigChange
            {
                VmId = vm.HyperVId,
                NetworkAdapterChanges = adapters.Select(a => new NetworkAdapterChange(a, n.SwitchName, n.VlanId ?? 0, n.Id)).ToList(),
            };
            var (job, _) = await jobs.CreateOperationJobAsync(http.User.UserId(), new JobService.OperationJobSpec(JobService.ConfigJobType, "Update", ScopeType.Vm, vm.Id, vm.Name,
                vm.HostId, Permissions.VmConfigNetwork, new JobService.OperationParameters(HvOperations.ToElement(change), Permissions: [Permissions.VmConfigNetwork]), 300,
                $"VLAN of network {n.Name} changed: adapters of {vm.Name} re-tagged"), null, http.RequestAborted);
            queued.Add(new { vmId = vm.Id, vm.Name, jobId = job.Id });
        }
        await http.WriteAuditAsync("network.portal.update", resourceType: "host", resourceId: n.HostId.ToString("D"),
            details: new { networkId = n.Id, before, after = new { n.Name, n.VlanId, n.Description }, retagged = queued.Count, skipped });
        return Results.Ok(new { network = Dto(n, null, true), jobs = queued, skipped });
    }

    public const int MaxRetaggedVms = 100;

    /// <summary>
    /// VMs of the host with adapters connected to the switch with this access VLAN (0 = untagged), read live; Unread: the
    /// VMs on the switch whose configuration could not be read, or beyond <see cref="MaxRetaggedVms"/>.
    /// </summary>
    internal static async Task<(List<(VirtualMachine Vm, List<string> Adapters)> Found, List<string> Unread, IResult? Error)> AdaptersOnAsync(PortalDbContext db,
        IAgentDispatcher agents, ManagedHost host, string switchName, int vlan, CancellationToken ct)
    {
        var found = new List<(VirtualMachine, List<string>)>();
        var unread = new List<string>();
        var (switches, error) = await VmOperationsEndpoints.QueryAsync(agents, host.Id, HvOperation.ListSwitches, new { }, null, ct);
        if (error is not null)
        {
            return (found, unread, error);
        }
        var names = (JsonSerializer.Deserialize<List<SwitchInfo>>(switches!.Value.GetRawText(), AgentProtocol.Json) ?? [])
            .Where(s => s.Name == switchName).SelectMany(s => s.ConnectedVms).ToHashSet(StringComparer.Ordinal);
        var vms = await db.VirtualMachines.AsNoTracking().Where(v => v.HostId == host.Id && !v.IsMissing).ToListAsync(ct);
        var onSwitch = vms.Where(v => names.Contains(v.Name)).OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
        unread.AddRange(onSwitch.Skip(MaxRetaggedVms).Select(v => v.Name + " (over the limit of " + MaxRetaggedVms + " VMs per change)"));
        foreach (var vm in onSwitch.Take(MaxRetaggedVms))
        {
            var (config, configError) = await VmOperationsEndpoints.QueryAsync(agents, host.Id, HvOperation.GetVmConfig, new VmRef(vm.HyperVId), vm.HyperVId, ct);
            if (configError is not null || config is null)
            {
                unread.Add(vm.Name + " (configuration not read)");
                continue;
            }
            var adapters = (HvOperations.FromElement<VmConfig>(config)?.NetworkAdapters ?? [])
                .Where(a => a.SwitchName == switchName && a.VlanId == vlan).Select(a => a.Id).ToList();
            if (adapters.Count > 0)
            {
                found.Add((vm, adapters));
            }
        }
        return (found, unread, null);
    }

    private static async Task<IResult> DeleteAsync(Guid id, HttpContext http, PortalDbContext db, AccessEvaluator access)
    {
        var n = await db.PortalNetworks.FirstOrDefaultAsync(x => x.Id == id, http.RequestAborted);
        if (n is null || !await access.CanSeeHostAsync(http.User.UserId(), n.HostId, http.RequestAborted))
        {
            return ApiSupport.NotFound();
        }
        if (await http.GuardAsync(Permissions.NetworkManage, ResourceRef.Host(n.HostId), "network.portal.delete") is { } denied)
        {
            return denied;
        }
        db.PortalNetworks.Remove(n);
        await db.SaveChangesAsync(http.RequestAborted);
        // Only the portal record: adapters keep their switch and VLAN in Hyper-V.
        await http.WriteAuditAsync("network.portal.delete", resourceType: "host", resourceId: n.HostId.ToString("D"), details: new { networkId = n.Id, n.Name, n.SwitchName, n.VlanId });
        return Results.NoContent();
    }
}
