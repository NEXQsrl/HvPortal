namespace HvPortal.Contracts.HyperV;

/// <summary>
/// Health of a Hyper-V host as Windows reports it (protocol 9, GetHostHealth). Every section is read independently: a
/// section the agent account cannot read is returned with its error instead of being left out or guessed, and the
/// overall status is never better than what was actually read.
/// Status values: "Ok", "Warning", "Critical", "Unknown".
/// </summary>
public sealed record HostHealthReport
{
    public DateTimeOffset CollectedUtc { get; init; }
    public string Status { get; init; } = HealthLevel.Unknown;
    public HostHardwareInfo? Hardware { get; init; }
    public IReadOnlyList<PhysicalDiskHealth> PhysicalDisks { get; init; } = [];
    public IReadOnlyList<VolumeHealth> Volumes { get; init; } = [];
    public IReadOnlyList<StoragePoolHealth> StoragePools { get; init; } = [];
    public IReadOnlyList<NetAdapterHealth> NetworkAdapters { get; init; } = [];
    public IReadOnlyList<HardwareEvent> Events { get; init; } = [];
    /// <summary>Hyper-V services of the host (vmms, vmcompute) and whether they run.</summary>
    public IReadOnlyList<ServiceHealth> Services { get; init; } = [];
    /// <summary>Sections that could not be read, with the reason (e.g. "physicalDisks: access denied").</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];
}

public static class HealthLevel
{
    public const string Ok = "Ok";
    public const string Warning = "Warning";
    public const string Critical = "Critical";
    public const string Unknown = "Unknown";

    /// <summary>The worst of the given statuses (Unknown counts as better than Warning, worse than Ok).</summary>
    public static string Worst(IEnumerable<string?> statuses)
    {
        var rank = 0;
        foreach (var s in statuses)
        {
            rank = Math.Max(rank, s switch { Critical => 3, Warning => 2, Unknown => 1, _ => 0 });
        }
        return rank switch { 3 => Critical, 2 => Warning, 1 => Unknown, _ => Ok };
    }
}

public sealed record HostHardwareInfo
{
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    public string? SerialNumber { get; init; }
    public string? BiosVersion { get; init; }
    public DateTimeOffset? BiosDate { get; init; }
    public int? MemoryModules { get; init; }
    public long? MemoryInstalledBytes { get; init; }
    public IReadOnlyList<string> Processors { get; init; } = [];
    /// <summary>Win32_Processor.Status / Win32_ComputerSystem.Status as reported ("OK", "Degraded", ...).</summary>
    public string? Status { get; init; }
}

/// <summary>Get-PhysicalDisk (+ Get-StorageReliabilityCounter when readable).</summary>
public sealed record PhysicalDiskHealth
{
    public required string Name { get; init; }
    public string? SerialNumber { get; init; }
    public string? MediaType { get; init; }
    public string? BusType { get; init; }
    public long? SizeBytes { get; init; }
    /// <summary>Healthy, Warning, Unhealthy, Unknown (MSFT_PhysicalDisk.HealthStatus).</summary>
    public string? HealthStatus { get; init; }
    public IReadOnlyList<string> OperationalStatus { get; init; } = [];
    public int? TemperatureCelsius { get; init; }
    public int? WearPercent { get; init; }
    public long? ReadErrorsUncorrected { get; init; }
    public long? WriteErrorsUncorrected { get; init; }
    public long? PowerOnHours { get; init; }
    public string Status { get; init; } = HealthLevel.Unknown;
}

/// <summary>Get-Volume of local volumes.</summary>
public sealed record VolumeHealth
{
    public required string Name { get; init; }
    public string? Label { get; init; }
    public string? FileSystem { get; init; }
    public long? SizeBytes { get; init; }
    public long? FreeBytes { get; init; }
    public string? HealthStatus { get; init; }
    public string? OperationalStatus { get; init; }
    public string Status { get; init; } = HealthLevel.Unknown;
}

/// <summary>Storage Spaces pools and their virtual disks (Get-StoragePool, Get-VirtualDisk).</summary>
public sealed record StoragePoolHealth
{
    public required string Name { get; init; }
    public string? HealthStatus { get; init; }
    public string? OperationalStatus { get; init; }
    public long? SizeBytes { get; init; }
    public long? AllocatedBytes { get; init; }
    public IReadOnlyList<string> VirtualDisks { get; init; } = [];
    public string Status { get; init; } = HealthLevel.Unknown;
}

public sealed record NetAdapterHealth
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? Status { get; init; }
    public string? LinkSpeed { get; init; }
    public string Health { get; init; } = HealthLevel.Unknown;
}

/// <summary>Critical/error events of the last days from hardware, disk, file system and Hyper-V sources (System log).</summary>
public sealed record HardwareEvent(DateTimeOffset TimeUtc, string Provider, int Id, string Level, string? Message);

public sealed record ServiceHealth(string Name, string? DisplayName, string? State, string? StartMode);
