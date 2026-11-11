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

/// <summary>
/// Why the agent could not talk to Windows management on a host (0.9.4). The agent puts the code at the start of
/// <c>HostInventory.AccessMessage</c> ("wmi_timeout: ...") followed by what to check and, in square brackets, the original
/// error; the portal shows an explanation by code and keeps the original text. Messages of older agents have no code.
/// </summary>
public static class WmiProblemCodes
{
    /// <summary>The host does not answer on the network (RPC server unavailable, name not resolved, firewall).</summary>
    public const string Unreachable = "wmi_unreachable";
    /// <summary>The host answers but WMI or its Hyper-V provider is not available (service stopped, namespace or class missing, provider failed to load).</summary>
    public const string ProviderUnavailable = "wmi_provider_unavailable";
    /// <summary>No answer within the time allowed.</summary>
    public const string Timeout = "wmi_timeout";
    /// <summary>The credentials were refused (wrong user name or password, account locked, disabled or expired).</summary>
    public const string Authentication = "wmi_authentication_failed";
    /// <summary>The account is accepted but has no right to do this (not in Hyper-V Administrators, DCOM / WMI namespace permissions, UAC remote filter).</summary>
    public const string AccessDenied = "wmi_access_denied";
    /// <summary>WMI works; the Hyper-V management API itself returned an error.</summary>
    public const string HyperV = "hyperv_error";
    /// <summary>Any other WMI error.</summary>
    public const string Other = "wmi_error";

    public static readonly string[] All = [Unreachable, ProviderUnavailable, Timeout, Authentication, AccessDenied, HyperV, Other];

    /// <summary>The code at the start of a message ("code: text"), or null when the message has none of the known codes.</summary>
    public static string? CodeOf(string? message)
    {
        if (message is null)
        {
            return null;
        }
        var colon = message.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 && All.Contains(message[..colon], StringComparer.Ordinal) ? message[..colon] : null;
    }
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
    /// <summary>True when the Cluster service (ClusSvc) is installed.</summary>
    public bool ClusterServicePresent { get; init; }
    /// <summary>Protocol 11: the failover cluster of the host, when the Cluster service runs (null otherwise and with older agents).</summary>
    public ClusterFacts? Cluster { get; init; }
    public required string AgentVersion { get; init; }

    public string? CpuName { get; init; }
    public int? CpuSockets { get; init; }
    public int? CpuCores { get; init; }
    public int? CpuLoadPercent { get; init; }
    /// <summary>"hypervisor" (all partitions, physical CPU load) or "root-partition" (host OS only).</summary>
    public string? CpuLoadSource { get; init; }
    public DateTimeOffset? LastBootUtc { get; init; }
    public IReadOnlyList<HostVolume> Volumes { get; init; } = [];
    public IReadOnlyList<NetworkAdapterInfo> NetworkAdapters { get; init; } = [];
    public IReadOnlyList<string> VirtualSwitches { get; init; } = [];
    /// <summary>
    /// Agent 0.7.0+. Folders of the host the portal may use (file explorer, folder pickers, VM locations, ISO and
    /// template libraries, backup folders), set when the agent is installed (Install-Agent -StorageFolders) and enforced
    /// by the agent. Empty = not restricted (every local fixed drive, system folders excluded). Null with older agents.
    /// </summary>
    public IReadOnlyList<string>? StorageFolders { get; init; }
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
    /// <summary>Agent 0.5.0+. Msvm_MemorySettingData.Limit: the most memory the VM can get (dynamic memory); equals startup when static.</summary>
    public long? MemoryMaximumMb { get; init; }
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
    /// <summary>Agent 0.3.4+. Msvm_SummaryInformation.CreationTime.</summary>
    public DateTimeOffset? CreatedUtc { get; init; }
    /// <summary>Agent 0.3.4+. Hyper-V notes of the VM (Msvm_SummaryInformation.Notes), truncated to 4000 characters.</summary>
    public string? Notes { get; init; }
    /// <summary>Agent 0.3.4+. Folder of the VM configuration (Msvm_VirtualSystemSettingData.ConfigurationDataRoot = Get-VM Path).</summary>
    public string? ConfigurationPath { get; init; }
    /// <summary>
    /// Agent 0.3.4+, dynamic memory only: Msvm_SummaryInformation.MemoryAvailable, percentage of the assigned memory that
    /// the guest does not need (negative = memory pressure). Demand = assigned x (100 - value) / 100.
    /// </summary>
    public int? MemoryAvailablePercent { get; init; }
    /// <summary>Agent 0.3.4+, dynamic memory only: Msvm_SummaryInformation.AvailableMemoryBuffer (percent of the ideal buffer).</summary>
    public int? MemoryBufferPercent { get; init; }
    /// <summary>Agent 0.3.4+. Msvm_SummaryInformation.ReplicationMode: 0 None, 1 Primary, 2 Replica, 3 Test replica, 4 Extended replica.</summary>
    public ushort? ReplicationMode { get; init; }
    /// <summary>Agent 0.3.4+. ReplicationState: 0 Disabled ... 21 (see Msvm_SummaryInformation).</summary>
    public ushort? ReplicationState { get; init; }
    /// <summary>Agent 0.3.4+. ReplicationHealth: 0 Not applicable, 1 Ok, 2 Warning, 3 Critical.</summary>
    public ushort? ReplicationHealth { get; init; }
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
