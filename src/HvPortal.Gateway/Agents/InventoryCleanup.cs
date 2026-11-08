using HvPortal.Gateway.Data;
using Microsoft.EntityFrameworkCore;

namespace HvPortal.Gateway.Agents;

/// <summary>
/// Removes inventory records consistently: role assignments scoped to the removed resources and logical-group
/// memberships are deleted too, so no stale grant can ever re-attach to a future resource with the same id.
/// Jobs, job events and audit entries are kept as history (they carry the resource name).
/// </summary>
public static class InventoryCleanup
{
    public static async Task RemoveVmAsync(PortalDbContext db, Guid vmId, Guid hostId, string eventKind, DateTimeOffset now, CancellationToken ct)
    {
        var vm = await db.VirtualMachines.AsNoTracking().FirstOrDefaultAsync(v => v.Id == vmId, ct);
        var scope = vmId.ToString("D");
        await db.RoleAssignments.Where(a => a.ScopeType == ScopeType.Vm && a.ScopeId == scope).ExecuteDeleteAsync(ct);
        await db.LogicalGroupMembers.Where(m => m.VmId == vmId).ExecuteDeleteAsync(ct);
        await db.ResourceLocks.Where(l => l.ResourceKey == "vm:" + vmId.ToString("N")).ExecuteDeleteAsync(ct);
        await db.VmMetricSamples.Where(s => s.VmId == vmId).ExecuteDeleteAsync(ct);
        await db.VmMeterings.Where(m => m.VmId == vmId).ExecuteDeleteAsync(ct);
        await db.VirtualMachines.Where(v => v.Id == vmId).ExecuteDeleteAsync(ct);
        db.InventoryEvents.Add(new InventoryEvent { HostId = hostId, VmId = vmId, Kind = eventKind, Details = vm?.Name, At = now });
        await db.SaveChangesAsync(ct);
    }

    public static async Task<int> RemoveHostAsync(PortalDbContext db, Guid hostId, CancellationToken ct)
    {
        var vmIds = await db.VirtualMachines.Where(v => v.HostId == hostId).Select(v => v.Id).ToListAsync(ct);
        var vmScopes = vmIds.Select(i => i.ToString("D")).ToList();
        var hostScope = hostId.ToString("D");
        await db.RoleAssignments.Where(a => (a.ScopeType == ScopeType.Host && a.ScopeId == hostScope)
                                            || (a.ScopeType == ScopeType.Vm && vmScopes.Contains(a.ScopeId))).ExecuteDeleteAsync(ct);
        await db.LogicalGroupMembers.Where(m => vmIds.Contains(m.VmId)).ExecuteDeleteAsync(ct);
        await db.InventoryEvents.Where(e => e.HostId == hostId).ExecuteDeleteAsync(ct);
        await db.GroupNetworks.Where(n => n.HostId == hostId).ExecuteDeleteAsync(ct);
        // The records only: the template folders stay on the host, which the portal no longer manages.
        await db.VmTemplates.Where(t => t.HostId == hostId).ExecuteDeleteAsync(ct);
        await db.VmMetricSamples.Where(s => vmIds.Contains(s.VmId)).ExecuteDeleteAsync(ct);
        await db.VmMeterings.Where(m => m.HostId == hostId || vmIds.Contains(m.VmId)).ExecuteDeleteAsync(ct);
        await db.VirtualMachines.Where(v => v.HostId == hostId).ExecuteDeleteAsync(ct);
        await db.EnrollmentTokens.Where(t => t.HostId == hostId).ExecuteDeleteAsync(ct);
        await db.AgentCertificates.Where(c => c.HostId == hostId).ExecuteDeleteAsync(ct);
        await db.Hosts.Where(h => h.Id == hostId).ExecuteDeleteAsync(ct);
        return vmIds.Count;
    }
}
