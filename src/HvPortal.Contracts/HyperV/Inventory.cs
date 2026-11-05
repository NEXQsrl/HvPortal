namespace HvPortal.Contracts.HyperV;

/// <summary>Result of checking whether the agent can actually use the Hyper-V management API.</summary>
public enum HyperVAccessStatus
{
    Ok,
    /// <summary>The Hyper-V management service (vmms) is not installed on this machine.</summary>
    NotInstalled,
    /// <summary>vmms exists but is not running.</summary>
    ServiceStopped,
    /// <summary>
    /// vmms is running but root\virtualization\v2 returns no management service instance.
    /// Observed behavior: a non-privileged caller gets an empty result set, not an error.
    /// The agent account must be in "Hyper-V Administrators" (S-1-5-32-578) or Administrators.
    /// </summary>
    AccessDenied,
    Error,
}

public sealed record HostFacts
{
    public required string ComputerName { get; init; }
    public string? DnsHostName { get; init; }
    public string? Domain { get; init; }
    public bool PartOfDomain { get; init; }
    public string? OsCaption { get; init; }
    public string? OsVersion { get; init; }
    public string? OsBuildNumber { get; init; }
    /// <summary>Win32_OperatingSystem.OperatingSystemSKU (e.g. 7 = Server Standard, 8 = Server Datacenter).</summary>
    public uint? OsSku { get; init; }
    /// <summary>Win32_OperatingSystem.ProductType: 1 workstation, 2 domain controller, 3 server.</summary>
    public uint? ProductType { get; init; }
    public int? LogicalProcessorCount { get; init; }
    public long? TotalPhysicalMemoryBytes { get; init; }
    public long? FreePhysicalMemoryBytes { get; init; }
    public string? HyperVServiceState { get; init; }
    /// <summary>True when the Cluster service (ClusSvc) is installed. Cluster management is Milestone 3.</summary>
    public bool ClusterServicePresent { get; init; }
    public required string AgentVersion { get; init; }

    public string? CpuName { get; init; }
    public int? CpuSockets { get; init; }
    public int? CpuCores { get; init; }
    public int? CpuLoadPercent { get; init; }
    public DateTimeOffset? LastBootUtc { get; init; }
    public IReadOnlyList<HostVolume> Volumes { get; init; } = [];
    public IReadOnlyList<NetworkAdapterInfo> NetworkAdapters { get; init; } = [];
    public IReadOnlyList<string> VirtualSwitches { get; init; } = [];
}

/// <summary>Local fixed volume of the host (Win32_LogicalDisk, DriveType = 3).</summary>
public sealed record HostVolume(string Name, string? Label, string? FileSystem, long? SizeBytes, long? FreeBytes);

/// <summary>IP configuration of a network adapter (host: Win32_NetworkAdapterConfiguration; guest: Msvm_GuestNetworkAdapterConfiguration).</summary>
public sealed record NetworkAdapterInfo
{
    public required string Name { get; init; }
    public string? MacAddress { get; init; }
    public IReadOnlyList<string> IpAddresses { get; init; } = [];
    public IReadOnlyList<string> Subnets { get; init; } = [];
    public IReadOnlyList<string> Gateways { get; init; } = [];
    public IReadOnlyList<string> DnsServers { get; init; } = [];
    public bool? DhcpEnabled { get; init; }
    /// <summary>VM adapters only: virtual switch the adapter is connected to (null = not connected).</summary>
    public string? SwitchName { get; init; }
}

/// <summary>Disk or DVD attached to a VM (Msvm_StorageAllocationSettingData, sizes from Msvm_ImageManagementService).</summary>
public sealed record VmDisk
{
    /// <summary>"VirtualHardDisk", "DvdIso" or "Other".</summary>
    public required string Kind { get; init; }
    public string? Path { get; init; }
    public string? Controller { get; init; }
    public string? Location { get; init; }
    /// <summary>VHD, VHDX, VHDSet.</summary>
    public string? Format { get; init; }
    /// <summary>Fixed, Dynamic, Differencing.</summary>
    public string? DiskType { get; init; }
    public long? MaxSizeBytes { get; init; }
    public long? FileSizeBytes { get; init; }
    public string? ParentPath { get; init; }
}

public sealed record VmDetails
{
    public required Guid VmId { get; init; }
    public required DateTimeOffset CollectedAtUtc { get; init; }
    public IReadOnlyList<VmDisk> Disks { get; init; } = [];
    public IReadOnlyList<NetworkAdapterInfo> NetworkAdapters { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record VmSummary
{
    /// <summary>Msvm_ComputerSystem.Name: the VM GUID. Stable across renames and (in clusters) migrations.</summary>
    public required Guid VmId { get; init; }
    public required string Name { get; init; }
    public required VmPowerState PowerState { get; init; }
    public ushort EnabledStateRaw { get; init; }
    public bool IsCritical { get; init; }
    public ushort? HealthState { get; init; }
    public IReadOnlyList<ushort> OperationalStatus { get; init; } = [];
    public int? Generation { get; init; }
    public string? ConfigurationVersion { get; init; }
    public int? ProcessorCount { get; init; }
    public long? MemoryStartupMb { get; init; }
    public long? MemoryAssignedMb { get; init; }
    public bool? DynamicMemoryEnabled { get; init; }
    public long? UptimeMs { get; init; }
    public ushort? Heartbeat { get; init; }
    public ushort? ProcessorLoadPercent { get; init; }
    public string? GuestOperatingSystem { get; init; }
    /// <summary>Integration shutdown service reports OK (OperationalStatus[0] == 2). Required for guest shutdown/restart.</summary>
    public bool? ShutdownServiceAvailable { get; init; }
    public DateTimeOffset? TimeOfLastStateChangeUtc { get; init; }
    public DateTimeOffset? TimeOfLastConfigurationChangeUtc { get; init; }
    /// <summary>Number of standard/production checkpoints (VSSD of type Microsoft:Hyper-V:Snapshot:Realized).</summary>
    public int? CheckpointCount { get; init; }
}

public sealed record HostInventory
{
    public required DateTimeOffset CollectedAtUtc { get; init; }
    public required HyperVAccessStatus Access { get; init; }
    public string? AccessMessage { get; init; }
    public required HostFacts Host { get; init; }
    public IReadOnlyList<VmSummary> Vms { get; init; } = [];
    /// <summary>True when some data could not be collected; see <see cref="Warnings"/>.</summary>
    public bool Partial { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
