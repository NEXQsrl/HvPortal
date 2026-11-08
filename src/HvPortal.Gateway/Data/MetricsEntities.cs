namespace HvPortal.Gateway.Data;

/// <summary>
/// Resource metering of one VM as the portal knows it (ADR-039). The source of truth is Hyper-V
/// (ResourceMeteringEnabled): Enabled is updated from every report of the host and by the enable/disable jobs.
/// </summary>
public sealed class VmMetering
{
    /// <summary>Portal VM id.</summary>
    public Guid VmId { get; set; }
    public Guid HostId { get; set; }
    public bool Enabled { get; set; }
    public DateTimeOffset? EnabledAt { get; set; }
    /// <summary>Last Reset-VMResourceMetering done by the portal (or the moment it saw the counters start again).</summary>
    public DateTimeOffset? LastResetAt { get; set; }
    public DateTimeOffset? LastSampleAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? LastErrorAt { get; set; }
}

/// <summary>
/// One sample of the resource use of a VM, stored every MetricsOptions.SampleIntervalSeconds. CPU and memory "Avg/Max"
/// are computed by the gateway from the inventory reports of the period (every 30 s); the Measure-VM values are the totals
/// of Hyper-V at the moment of the sample (the use of a period is the difference between two samples).
/// </summary>
public sealed class VmMetricSample
{
    public long Id { get; set; }
    public Guid VmId { get; set; }
    public DateTimeOffset At { get; set; }
    /// <summary>Seconds covered by the inventory statistics of this sample (0 = none received).</summary>
    public int ObservedSeconds { get; set; }
    public string? State { get; set; }
    public int? ProcessorCount { get; set; }
    public double? CpuAvgPercent { get; set; }
    public double? CpuMaxPercent { get; set; }
    public long? MemoryAssignedAvgMb { get; set; }
    public long? MemoryAssignedMaxMb { get; set; }
    public long? MemoryDemandMb { get; set; }
    public long? MemoryMaximumMb { get; set; }
    public long? MemoryStartupMb { get; set; }
    public bool? DynamicMemory { get; set; }
    public string? MemoryStatus { get; set; }
    /// <summary>Lowest "available memory" of the period (Msvm_SummaryInformation.MemoryAvailable, %; negative = pressure).</summary>
    public int? MemoryAvailableMinPercent { get; set; }

    // ---- Measure-VM totals at the moment of the sample
    public double? MeteringSeconds { get; set; }
    public long? AverageCpuMhz { get; set; }
    public long? MaximumMemoryMb { get; set; }
    public long? TotalDiskMb { get; set; }
    public long? NormalizedIoCount { get; set; }
    public long? AverageLatencyMs { get; set; }
    public long? DiskReadMb { get; set; }
    public long? DiskWrittenMb { get; set; }
    public long? NetworkInboundMb { get; set; }
    public long? NetworkOutboundMb { get; set; }
}
