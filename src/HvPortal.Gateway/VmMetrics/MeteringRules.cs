using HvPortal.Gateway.Data;
using Microsoft.EntityFrameworkCore;

namespace HvPortal.Gateway.VmMetrics;

/// <summary>What the portal records when a vm.metering job succeeds (ADR-039).</summary>
public static class MeteringRules
{
    public static async Task ApplyJobOutcomeAsync(PortalDbContext db, VmMetricsBuffer buffer, VirtualMachine vm, bool enabled, DateTimeOffset now,
        CancellationToken ct)
    {
        var m = await db.VmMeterings.FirstOrDefaultAsync(x => x.VmId == vm.Id, ct);
        if (m is null)
        {
            m = new VmMetering { VmId = vm.Id, HostId = vm.HostId };
            db.VmMeterings.Add(m);
        }
        if (enabled && !m.Enabled)
        {
            // Hyper-V counters start now: the automatic reset is counted from here.
            m.EnabledAt = now;
            m.LastResetAt = null;
        }
        m.Enabled = enabled;
        m.HostId = vm.HostId;
        m.LastError = null;
        m.LastErrorAt = null;
        await db.SaveChangesAsync(ct);
        // The samples already stored stay until their retention ends (the history of the last days remains visible).
        buffer.SetMetered(vm.Id, enabled);
    }
}
