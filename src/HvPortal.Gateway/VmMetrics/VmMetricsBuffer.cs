using System.Collections.Concurrent;
using HvPortal.Contracts.HyperV;

namespace HvPortal.Gateway.VmMetrics;

/// <summary>
/// CPU and memory of the VMs with resource metering on, accumulated from the inventory reports (every 30 s) between
/// two stored samples (ADR-039): a 5-minute sample then carries the average and the peak of the period, not one reading.
/// In memory only: after a gateway restart the first sample covers a shorter period (ObservedSeconds says how long).
/// </summary>
public sealed class VmMetricsBuffer
{
    public sealed record Stats(double? CpuAvg, double? CpuMax, long? MemAvgMb, long? MemMaxMb, int? AvailableMinPercent, int ObservedSeconds, int Readings);

    private sealed class Acc
    {
        public double CpuSum;
        public int CpuCount;
        public double CpuMax;
        public long MemSum;
        public int MemCount;
        public long MemMax;
        public int? AvailMin;
        public DateTimeOffset First;
        public DateTimeOffset Last;
        public int Readings;
    }

    private readonly ConcurrentDictionary<Guid, Acc> _acc = new();
    private volatile HashSet<Guid> _metered = [];

    /// <summary>The VMs (portal ids) whose inventory readings are kept. Set by the collector at every pass.</summary>
    public void SetMetered(IEnumerable<Guid> vmIds)
    {
        var set = vmIds.ToHashSet();
        _metered = set;
        foreach (var key in _acc.Keys.Where(k => !set.Contains(k)))
        {
            _acc.TryRemove(key, out _);
        }
    }

    public void SetMetered(Guid vmId, bool metered)
    {
        var set = new HashSet<Guid>(_metered);
        if (metered ? set.Add(vmId) : set.Remove(vmId))
        {
            _metered = set;
        }
        if (!metered)
        {
            _acc.TryRemove(vmId, out _);
        }
    }

    public bool IsMetered(Guid vmId) => _metered.Contains(vmId);

    /// <summary>One inventory reading of a VM. A VM that is not running uses no CPU and has no assigned memory to average.</summary>
    public void Observe(Guid vmId, VmSummary v, DateTimeOffset at)
    {
        if (!_metered.Contains(vmId) || v.PowerState != VmPowerState.Running)
        {
            return;
        }
        var acc = _acc.GetOrAdd(vmId, _ => new Acc { First = at });
        lock (acc)
        {
            if (acc.Readings == 0)
            {
                acc.First = at;
            }
            acc.Last = at;
            acc.Readings++;
            if (v.ProcessorLoadPercent is { } cpu)
            {
                acc.CpuSum += cpu;
                acc.CpuCount++;
                acc.CpuMax = Math.Max(acc.CpuMax, cpu);
            }
            if (v.MemoryAssignedMb is { } mem and > 0)
            {
                acc.MemSum += mem;
                acc.MemCount++;
                acc.MemMax = Math.Max(acc.MemMax, mem);
            }
            if (v.MemoryAvailablePercent is { } avail)
            {
                acc.AvailMin = acc.AvailMin is { } m ? Math.Min(m, avail) : avail;
            }
        }
    }

    /// <summary>Statistics since the previous call for this VM (and starts a new period).</summary>
    public Stats Take(Guid vmId)
    {
        if (!_acc.TryRemove(vmId, out var acc))
        {
            return new Stats(null, null, null, null, null, 0, 0);
        }
        lock (acc)
        {
            return new Stats(
                acc.CpuCount > 0 ? Math.Round(acc.CpuSum / acc.CpuCount, 1) : null,
                acc.CpuCount > 0 ? acc.CpuMax : null,
                acc.MemCount > 0 ? acc.MemSum / acc.MemCount : null,
                acc.MemCount > 0 ? acc.MemMax : null,
                acc.AvailMin,
                (int)Math.Max(0, (acc.Last - acc.First).TotalSeconds),
                acc.Readings);
        }
    }
}
