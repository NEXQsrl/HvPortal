using System.Text.Json;
using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Data;
using Microsoft.EntityFrameworkCore;

namespace HvPortal.Gateway.Jobs;

/// <summary>
/// VM move to another host (Move-VM): the destination is a host of the portal, named by the DNS name its agent reports.
/// After a successful move the portal record of the VM (groups, role assignments, restore points, history) follows the
/// VM to the destination: Move-VM keeps the Hyper-V id.
/// </summary>
public static class MigrationRules
{
    public static string? DestinationName(ManagedHost destination)
    {
        if (destination.ConnectionMode == HostConnectionMode.Remote && RemoteHosts.IsValidComputerName(destination.RemoteComputerName))
        {
            return destination.RemoteComputerName;
        }
        var facts = destination.HostFactsJson is null ? null : JsonSerializer.Deserialize<HostFacts>(destination.HostFactsJson);
        var name = facts?.DnsHostName is { Length: > 0 } dns
            ? (dns.Contains('.', StringComparison.Ordinal) || facts.Domain is not { Length: > 0 } || !facts.PartOfDomain ? dns : dns + "." + facts.Domain)
            : destination.DnsHostName ?? destination.MachineName;
        return RemoteHosts.IsValidComputerName(name) ? name : null;
    }

    public static string DestinationLabel(ManagedHost? h) => h?.DisplayName ?? "the destination host";

    /// <summary>
    /// The VM record now belongs to the destination host. A record the destination's inventory may have created for the
    /// same Hyper-V id in the meantime is removed (the original keeps its groups and permissions).
    /// </summary>
    public static async Task ApplyMoveAsync(PortalDbContext db, VirtualMachine vm, Guid destinationHostId, DateTimeOffset now, CancellationToken ct)
    {
        var duplicates = await db.VirtualMachines.AsNoTracking()
            .Where(v => v.HostId == destinationHostId && v.HyperVId == vm.HyperVId && v.Id != vm.Id).Select(v => v.Id).ToListAsync(ct);
        foreach (var d in duplicates)
        {
            await Agents.InventoryCleanup.RemoveVmAsync(db, d, destinationHostId, "vm.duplicate_after_move", now, ct);
        }
        await db.VirtualMachines.Where(v => v.Id == vm.Id).ExecuteUpdateAsync(u => u
            .SetProperty(v => v.HostId, destinationHostId)
            .SetProperty(v => v.IsMissing, false)
            .SetProperty(v => v.MissingSince, (DateTimeOffset?)null)
            .SetProperty(v => v.LastSeenAt, now), ct);
        db.InventoryEvents.Add(new InventoryEvent { HostId = vm.HostId, VmId = vm.Id, Kind = "vm.moved_out", Details = vm.Name, At = now });
        db.InventoryEvents.Add(new InventoryEvent { HostId = destinationHostId, VmId = vm.Id, Kind = "vm.moved_in", Details = vm.Name, At = now });
        await db.SaveChangesAsync(ct);
    }
}
