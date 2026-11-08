namespace HvPortal.Contracts.HyperV;

/// <summary>GetVmMetrics (protocol 10): VmId = one VM (answered also when its metering is off); null = every VM with metering on.</summary>
public sealed record VmMetricsRequest(Guid? VmId);

/// <summary>SetVmResourceMetering (protocol 10).</summary>
public sealed record VmMeteringChange(Guid VmId, bool Enabled);

/// <summary>
/// Resource use of VMs as Hyper-V reports it. Instant values come from Get-VM; the Measure-VM values are totals or
/// averages since metering was enabled or last reset (MeteringSeconds), so the gateway derives the use between two
/// reports from the difference of the totals. Every field is optional: a host may not report some of them.
/// </summary>
public sealed record VmMetricsReport
{
    /// <summary>Highest clock of the host processors (Win32_Processor.MaxClockSpeed), for MHz -> percent.</summary>
    public int? HostProcessorMhz { get; init; }
    public int? HostLogicalProcessors { get; init; }
    public IReadOnlyList<VmMetricsSample> Vms { get; init; } = [];
    /// <summary>VMs that could not be measured: "&lt;vm id&gt;: &lt;message&gt;".</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];
}

public sealed record VmMetricsSample
{
    public required Guid VmId { get; init; }
    public string? Name { get; init; }
    public string? State { get; init; }
    public bool MeteringEnabled { get; init; }
    public int? ProcessorCount { get; init; }
    /// <summary>Get-VM CPUUsage: percent of the VM's virtual processors, at the moment of the report.</summary>
    public int? CpuUsagePercent { get; init; }
    public long? MemoryAssignedMb { get; init; }
    public long? MemoryDemandMb { get; init; }
    public long? MemoryStartupMb { get; init; }
    public long? MemoryMaximumMb { get; init; }
    public bool? DynamicMemoryEnabled { get; init; }
    /// <summary>Get-VM MemoryStatus (e.g. "OK", "Low", "Warning", "Spanning"); empty for static memory.</summary>
    public string? MemoryStatus { get; init; }
    public long? UptimeSeconds { get; init; }

    // ---- Measure-VM (only with metering on), totals or averages since enable / last reset
    public double? MeteringSeconds { get; init; }
    public long? AverageCpuMhz { get; init; }
    public long? AverageMemoryMb { get; init; }
    public long? MinimumMemoryMb { get; init; }
    public long? MaximumMemoryMb { get; init; }
    /// <summary>Space allocated to the VM's virtual disks.</summary>
    public long? TotalDiskMb { get; init; }
    /// <summary>Normalized I/O operations (8 KB units) since enable / reset.</summary>
    public long? NormalizedIoCount { get; init; }
    public long? AverageNormalizedIops { get; init; }
    public long? AverageLatencyMs { get; init; }
    public long? DiskReadMb { get; init; }
    public long? DiskWrittenMb { get; init; }
    public long? NetworkInboundMb { get; init; }
    public long? NetworkOutboundMb { get; init; }
}
