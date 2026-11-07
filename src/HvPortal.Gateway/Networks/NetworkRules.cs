using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Data;
using Microsoft.EntityFrameworkCore;

namespace HvPortal.Gateway.Networks;

/// <summary>
/// Networks of the portal (ADR-030): a network = switch + access VLAN. A request names the network; the gateway puts
/// its switch and VLAN into the agent command, at request time and again when the job runs (the VLAN of a network may
/// have changed meanwhile). The client never chooses the VLAN of a network.
/// </summary>
public static class NetworkRules
{
    public const int MaxNetworksPerHost = 200;

    public static (string Code, string Message) Unknown => ("unknown_network", "The network does not exist on the VM's host.");

    /// <summary>Adapter changes and the added adapter, with the switch and VLAN of their networks.</summary>
    public static async Task<((string Code, string Message)? Error, VmConfigChange Change)> ResolveAsync(PortalDbContext db, Guid hostId, VmConfigChange c,
        CancellationToken ct)
    {
        var ids = (c.NetworkAdapterChanges ?? []).Where(n => n.NetworkId is not null).Select(n => n.NetworkId!.Value).ToList();
        if (c.AddNetworkAdapterNetworkId is { } addId)
        {
            ids.Add(addId);
        }
        if (ids.Count == 0)
        {
            return (null, c with { AddNetworkAdapterVlanId = null });
        }
        var networks = await db.PortalNetworks.AsNoTracking().Where(n => n.HostId == hostId && ids.Contains(n.Id)).ToDictionaryAsync(n => n.Id, ct);
        if (ids.Any(id => !networks.ContainsKey(id)))
        {
            return (Unknown, c);
        }
        var changes = (c.NetworkAdapterChanges ?? []).Select(n => n.NetworkId is { } nid
            ? n with { SwitchName = networks[nid].SwitchName, VlanId = networks[nid].VlanId ?? 0 }
            : n).ToList();
        var result = c with { NetworkAdapterChanges = changes.Count > 0 ? changes : c.NetworkAdapterChanges };
        if (c.AddNetworkAdapterNetworkId is { } added)
        {
            result = result with { AddNetworkAdapterSwitch = networks[added].SwitchName, AddNetworkAdapterVlanId = networks[added].VlanId };
        }
        else
        {
            result = result with { AddNetworkAdapterVlanId = null };
        }
        return (null, result);
    }

    /// <summary>The adapter of a new VM, with the switch and VLAN of its network.</summary>
    public static async Task<((string Code, string Message)? Error, NewVmRequest Spec)> ResolveAsync(PortalDbContext db, Guid hostId, NewVmRequest r, CancellationToken ct)
    {
        if (r.NetworkId is not { } id)
        {
            return (null, r);
        }
        var n = await db.PortalNetworks.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.HostId == hostId, ct);
        return n is null ? (Unknown, r) : (null, r with { SwitchName = n.SwitchName, VlanId = n.VlanId });
    }

    /// <summary>Null when the name is acceptable (letters, digits, space . _ ( ) -, 1-64).</summary>
    public static bool IsValidName(string? name) => name is not null && name.Trim() == name && VmBackup.BackupRules.NameRegex().IsMatch(name);
}
