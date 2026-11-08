using System.Text.Json;
using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Data;
using Microsoft.EntityFrameworkCore;

namespace HvPortal.Gateway.Agents;

/// <summary>
/// Applies an inventory snapshot reported by an agent. Detects changes made outside the portal
/// (state changes with no active portal job, renames, VMs appearing/disappearing) and records them
/// in the operation history (InventoryEvents).
/// VMs are never deleted from the portal automatically: a VM absent from a complete snapshot is flagged
/// "missing" so that permissions and history are preserved.
/// </summary>
public sealed class InventoryIngestor
{
    private const int MaxVms = 5000;
    private readonly PortalDbContext _db;
    private readonly TimeProvider _time;
    private readonly VmMetrics.VmMetricsBuffer? _metrics;

    public InventoryIngestor(PortalDbContext db, TimeProvider time, VmMetrics.VmMetricsBuffer? metrics = null)
    {
        _db = db;
        _time = time;
        _metrics = metrics;
    }

    public async Task ApplyAsync(Guid hostId, HostInventory inv, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var host = await _db.Hosts.FirstOrDefaultAsync(h => h.Id == hostId, ct);
        if (host is null)
        {
            return;
        }
        host.LastSeenAt = now;
        host.LastInventoryAt = now;
        host.Access = inv.Access;
        host.AccessMessage = Truncate(inv.AccessMessage, 2000);
        host.InventoryPartial = inv.Partial;
        host.InventoryWarningsJson = Truncate(JsonSerializer.Serialize(inv.Warnings.Take(50)), 16000);
        host.HostFactsJson = JsonSerializer.Serialize(inv.Host with
        {
            Volumes = inv.Host.Volumes.Take(64).ToList(),
            NetworkAdapters = inv.Host.NetworkAdapters.Take(32).ToList(),
            VirtualSwitches = inv.Host.VirtualSwitches.Take(64).ToList(),
        });
        host.MachineName = Truncate(inv.Host.ComputerName, 63);
        host.DnsHostName = Truncate(inv.Host.DnsHostName, 255);

        if (inv.Access != HyperVAccessStatus.Ok)
        {
            // No reliable VM data: keep the last known inventory, it is shown as stale by the UI.
            await _db.SaveChangesAsync(ct);
            return;
        }

        var existing = await _db.VirtualMachines.Where(v => v.HostId == hostId).ToDictionaryAsync(v => v.HyperVId, ct);
        var activeJobTargets = (await _db.Jobs.AsNoTracking()
            .Where(j => j.HostId == hostId && (j.Status == JobStatus.Running || j.Status == JobStatus.Reconciling))
            .Select(j => j.TargetId).ToListAsync(ct)).ToHashSet();
        var seen = new HashSet<Guid>();

        foreach (var v in inv.Vms.Take(MaxVms))
        {
            if (v.VmId == Guid.Empty || !seen.Add(v.VmId))
            {
                continue;
            }
            var name = Truncate(v.Name, 256) ?? v.VmId.ToString("D");
            if (!existing.TryGetValue(v.VmId, out var vm))
            {
                vm = new VirtualMachine { HostId = hostId, HyperVId = v.VmId, Name = name, FirstSeenAt = now, StateChangedAt = now };
                _db.VirtualMachines.Add(vm);
                Event(hostId, vm.Id, "vm.discovered", $"{name} ({v.PowerState})", now);
            }
            else
            {
                if (vm.PowerState != v.PowerState)
                {
                    var kind = activeJobTargets.Contains(vm.Id) ? "vm.state_changed" : "vm.state_changed_externally";
                    Event(hostId, vm.Id, kind, $"{vm.PowerState} -> {v.PowerState}", now);
                    vm.StateChangedAt = now;
                }
                if (vm.Name != name)
                {
                    Event(hostId, vm.Id, "vm.renamed", $"{vm.Name} -> {name}", now);
                }
                if (vm.IsMissing)
                {
                    Event(hostId, vm.Id, "vm.reappeared", name, now);
                }
            }
            vm.Name = name;
            vm.PowerState = v.PowerState;
            vm.EnabledStateRaw = v.EnabledStateRaw;
            vm.IsCritical = v.IsCritical;
            vm.Generation = v.Generation;
            vm.ConfigurationVersion = Truncate(v.ConfigurationVersion, 16);
            vm.ProcessorCount = v.ProcessorCount;
            vm.MemoryStartupMb = v.MemoryStartupMb;
            vm.MemoryAssignedMb = v.MemoryAssignedMb;
            vm.DynamicMemoryEnabled = v.DynamicMemoryEnabled;
            vm.MemoryMaximumMb = v.MemoryMaximumMb ?? vm.MemoryMaximumMb;
            vm.UptimeMs = v.UptimeMs;
            vm.Heartbeat = v.Heartbeat;
            vm.HealthState = v.HealthState;
            vm.ProcessorLoadPercent = v.ProcessorLoadPercent;
            vm.GuestOperatingSystem = Truncate(v.GuestOperatingSystem, 256);
            vm.ShutdownServiceAvailable = v.ShutdownServiceAvailable;
            vm.CheckpointCount = v.CheckpointCount ?? vm.CheckpointCount;
            // Agent 0.3.4+ fields (older agents send null: the previous values are kept).
            if (v.Notes is not null && vm.HyperVNotes is not null && !string.Equals(v.Notes, vm.HyperVNotes, StringComparison.Ordinal))
            {
                Event(hostId, vm.Id, "vm.notes.changed", Truncate(v.Notes, 200), now);
            }
            vm.HyperVNotes = v.Notes ?? vm.HyperVNotes;
            vm.HyperVCreatedAt = v.CreatedUtc ?? vm.HyperVCreatedAt;
            vm.ConfigurationPath = Truncate(v.ConfigurationPath, 520) ?? vm.ConfigurationPath;
            vm.MemoryAvailablePercent = v.MemoryAvailablePercent;
            vm.MemoryBufferPercent = v.MemoryBufferPercent;
            vm.ReplicationMode = v.ReplicationMode ?? vm.ReplicationMode;
            vm.ReplicationState = v.ReplicationState ?? vm.ReplicationState;
            vm.ReplicationHealth = v.ReplicationHealth ?? vm.ReplicationHealth;
            vm.OperationalStatusJson = JsonSerializer.Serialize(v.OperationalStatus.Take(8));
            vm.LastSeenAt = now;
            vm.IsMissing = false;
            vm.MissingSince = null;
            // CPU and memory readings of VMs with resource metering on (resource use history, ADR-039).
            _metrics?.Observe(vm.Id, v, now);
        }

        if (!inv.Partial)
        {
            foreach (var vm in existing.Values.Where(x => !seen.Contains(x.HyperVId) && !x.IsMissing))
            {
                vm.IsMissing = true;
                vm.MissingSince = now;
                Event(hostId, vm.Id, "vm.missing", vm.Name, now);
            }
        }
        await _db.SaveChangesAsync(ct);
    }

    private void Event(Guid hostId, Guid vmId, string kind, string details, DateTimeOffset at) =>
        _db.InventoryEvents.Add(new InventoryEvent { HostId = hostId, VmId = vmId, Kind = kind, Details = Truncate(details, 1000), At = at });

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}
