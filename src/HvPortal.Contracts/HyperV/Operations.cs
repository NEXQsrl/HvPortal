using System.Text.Json;

namespace HvPortal.Contracts.HyperV;

/// <summary>
/// Closed set of Hyper-V management operations executed by the agent through the official Hyper-V PowerShell
/// module (fixed script, typed arguments on stdin, no dynamic code). Each operation has a typed argument record.
/// </summary>
public enum HvOperation
{
    GetVmConfig,
    SetVmConfig,
    NewVm,
    GetHostDefaults,
    ListIsos,
    ListCheckpoints,
    NewCheckpoint,
    RestoreCheckpoint,
    RemoveCheckpoint,
    ListSwitches,
    ListHostDisks,
    /// <summary>Protocol 3. Read-only listing of local fixed drives / sub-folders (and .iso files) for the location and ISO pickers.</summary>
    BrowseFolders,
    /// <summary>Protocol 4. Native Hyper-V VM groups of the host (Get-VMGroup).</summary>
    ListVmGroups,
    /// <summary>Protocol 4. Reconcile the native VM groups owned by the portal with the desired state (VmGroupSyncRequest).</summary>
    SyncVmGroups,
}

public static class HvOperations
{
    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Agent.AgentProtocol.Json);
    public static T? FromElement<T>(JsonElement? e) => e is { } v ? v.Deserialize<T>(Agent.AgentProtocol.Json) : default;
}

// ---------------------------------------------------------------- arguments

public sealed record VmRef(Guid VmId);

public sealed record NetworkAdapterChange(string AdapterId, string? SwitchName, int? VlanId);

public sealed record NewDiskSpec(int SizeGb, bool Fixed);

public sealed record ExpandDiskSpec(string Path, int NewSizeGb);

/// <summary>All properties are optional: only non-null ones are applied. VM-state rules are enforced by the gateway and by Hyper-V.</summary>
public sealed record VmConfigChange
{
    public Guid VmId { get; init; }
    public string? NewName { get; init; }
    public string? Notes { get; init; }
    public int? ProcessorCount { get; init; }
    public int? ProcessorReserve { get; init; }
    public int? ProcessorLimit { get; init; }
    public int? ProcessorWeight { get; init; }
    public long? MemoryStartupMb { get; init; }
    public bool? DynamicMemoryEnabled { get; init; }
    public long? MemoryMinimumMb { get; init; }
    public long? MemoryMaximumMb { get; init; }
    public int? MemoryBuffer { get; init; }
    public int? MemoryPriority { get; init; }
    /// <summary>Nothing | StartIfRunning | Start</summary>
    public string? AutomaticStartAction { get; init; }
    public int? AutomaticStartDelaySeconds { get; init; }
    /// <summary>TurnOff | Save | ShutDown</summary>
    public string? AutomaticStopAction { get; init; }
    /// <summary>Disabled | Production | ProductionOnly | Standard</summary>
    public string? CheckpointType { get; init; }
    public bool? SecureBootEnabled { get; init; }
    /// <summary>MicrosoftWindows | MicrosoftUEFICertificateAuthority | OpenSourceShieldedVM</summary>
    public string? SecureBootTemplate { get; init; }
    /// <summary>ISO path to mount in the first DVD drive; empty string ejects.</summary>
    public string? DvdIsoPath { get; init; }
    public IReadOnlyList<NetworkAdapterChange>? NetworkAdapterChanges { get; init; }
    /// <summary>Add a synthetic network adapter connected to this switch ("" = not connected).</summary>
    public string? AddNetworkAdapterSwitch { get; init; }
    public NewDiskSpec? NewDisk { get; init; }
    public ExpandDiskSpec? ExpandDisk { get; init; }
    /// <summary>Allowed ISO library roots (from the host settings); the agent refuses ISO paths outside them.</summary>
    public IReadOnlyList<string> IsoRoots { get; init; } = [];
}

public sealed record NewVmRequest
{
    public required string Name { get; init; }
    public int Generation { get; init; } = 2;
    public int ProcessorCount { get; init; } = 2;
    public long MemoryStartupMb { get; init; } = 4096;
    public bool DynamicMemory { get; init; }
    public long? MemoryMinimumMb { get; init; }
    public long? MemoryMaximumMb { get; init; }
    /// <summary>0 = no disk.</summary>
    public int DiskSizeGb { get; init; } = 60;
    public bool DiskFixed { get; init; }
    public string? SwitchName { get; init; }
    public string? IsoPath { get; init; }
    /// <summary>Gen2: MicrosoftWindows | MicrosoftUEFICertificateAuthority | Off</summary>
    public string? SecureBootTemplate { get; init; }
    public bool EnableTpm { get; init; }
    public string? CheckpointType { get; init; }
    public string? AutomaticStartAction { get; init; }
    public bool StartAfterCreate { get; init; }
    public string? Notes { get; init; }
    /// <summary>
    /// Protocol 3. Local folder that will contain the VM (configuration and new disk go to &lt;Location&gt;\&lt;name&gt;\).
    /// Null = host defaults (Get-VMHost VirtualMachinePath / VirtualHardDiskPath). System folders are refused (see HostPaths).
    /// </summary>
    public string? Location { get; init; }
    public IReadOnlyList<string> IsoRoots { get; init; } = [];
}

public sealed record IsoListRequest(IReadOnlyList<string> Roots);

/// <summary>Path null = list the local fixed drives.</summary>
public sealed record FolderBrowseRequest(string? Path, bool IncludeIsos);

public sealed record DriveEntry(string Path, string? Label, long TotalBytes, long FreeBytes);

public sealed record FolderEntry(string Name, string Path);

public sealed record FolderListing
{
    public string? Path { get; init; }
    public string? Parent { get; init; }
    public IReadOnlyList<DriveEntry> Drives { get; init; } = [];
    public IReadOnlyList<FolderEntry> Folders { get; init; } = [];
    public IReadOnlyList<IsoFile> Isos { get; init; } = [];
    /// <summary>Free space of the drive containing Path.</summary>
    public long? FreeBytes { get; init; }
    public bool Truncated { get; init; }
}

public sealed record CheckpointRequest(Guid VmId, string? Name, string? CheckpointId, bool IncludeChildren);

/// <summary>Hyper-V VM group types (New-VMGroup -GroupType).</summary>
public static class VmGroupTypes
{
    public const string Vm = "VMCollectionType";
    public const string Management = "ManagementCollectionType";
}

/// <summary>
/// One native group the portal wants on the host. Key = "&lt;portal group id&gt;:vms|mgmt"; Id = native id when already
/// linked (null = create, the agent creates it with a new id). VmIds = Hyper-V VM ids (VM collections);
/// MemberKeys = keys of VM collections (management collections).
/// </summary>
public sealed record VmGroupSyncItem(string Key, string Name, string GroupType, Guid? Id, IReadOnlyList<Guid> VmIds, IReadOnlyList<string> MemberKeys);

/// <summary>Managed = native ids owned by the portal on this host: only these are modified or removed.</summary>
public sealed record VmGroupSyncRequest(IReadOnlyList<VmGroupSyncItem> Items, IReadOnlyList<Guid> Managed);

public sealed record VmGroupSyncResult
{
    /// <summary>Key -> native id of every desired group that exists after the sync.</summary>
    public IReadOnlyDictionary<string, Guid> Ids { get; init; } = new Dictionary<string, Guid>();
    /// <summary>Managed ids that were removed (or found already gone).</summary>
    public IReadOnlyList<Guid> Removed { get; init; } = [];
    public IReadOnlyList<string> Errors { get; init; } = [];
}

public sealed record NativeVmGroup(Guid Id, string Name, string GroupType, IReadOnlyList<Guid> VmIds, IReadOnlyList<Guid> GroupIds);

// ---------------------------------------------------------------- results

public sealed record VmConfig
{
    public required string Name { get; init; }
    public string? Notes { get; init; }
    public int Generation { get; init; }
    public string? State { get; init; }
    public string? Version { get; init; }
    public string? ConfigurationLocation { get; init; }
    public int ProcessorCount { get; init; }
    public int ProcessorReserve { get; init; }
    public int ProcessorLimit { get; init; }
    public int ProcessorWeight { get; init; }
    public long MemoryStartupBytes { get; init; }
    public bool DynamicMemoryEnabled { get; init; }
    public long MemoryMinimumBytes { get; init; }
    public long MemoryMaximumBytes { get; init; }
    public int MemoryBuffer { get; init; }
    public int MemoryPriority { get; init; }
    public string? AutomaticStartAction { get; init; }
    public int AutomaticStartDelay { get; init; }
    public string? AutomaticStopAction { get; init; }
    public string? CheckpointType { get; init; }
    public bool? SecureBootEnabled { get; init; }
    public string? SecureBootTemplate { get; init; }
    public bool? TpmEnabled { get; init; }
    public IReadOnlyList<VmDvdDrive> DvdDrives { get; init; } = [];
    public IReadOnlyList<VmNetworkAdapterConfig> NetworkAdapters { get; init; } = [];
    public IReadOnlyList<VmHardDisk> HardDisks { get; init; } = [];
}

public sealed record VmDvdDrive(string? ControllerType, int ControllerNumber, int ControllerLocation, string? Path);

public sealed record VmNetworkAdapterConfig(string Id, string? Name, string? SwitchName, string? MacAddress, bool DynamicMac, int VlanId, string? VlanMode);

public sealed record VmHardDisk(string? ControllerType, int ControllerNumber, int ControllerLocation, string? Path);

public sealed record HvOperationOutcome
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    /// <summary>For multi-step operations: steps applied before a failure.</summary>
    public IReadOnlyList<string> Applied { get; init; } = [];
    public JsonElement? Result { get; init; }
}

public sealed record NewVmResult(Guid VmId, string Name, string? ConfigurationLocation, string? DiskPath, string? Warning);

public sealed record HostDefaults(string? VirtualHardDiskPath, string? VirtualMachinePath, int LogicalProcessorCount, long MemoryCapacityBytes,
    IReadOnlyList<string> SupportedVersions, string? DefaultVersion);

public sealed record IsoFile(string Path, long SizeBytes, DateTimeOffset ModifiedUtc);

public sealed record CheckpointInfo(string Id, string Name, string? ParentId, DateTimeOffset CreatedUtc, string? Type, bool IsCurrent);

public sealed record SwitchInfo(string Id, string Name, string Type, string? NetAdapter, bool AllowManagementOs, bool EmbeddedTeaming, IReadOnlyList<string> ConnectedVms);

public sealed record HostDiskInfo(string Path, Guid? VmId, string? VmName, string? Controller, string? Format, string? DiskType,
    long? SizeBytes, long? FileSizeBytes, string? ParentPath);

// ---------------------------------------------------------------- console

public sealed record ConsoleFrameRequest(Guid VmId, int MaxWidth, int MaxHeight, bool ForceFull);

public sealed record ConsoleFrame
{
    public int Width { get; init; }
    public int Height { get; init; }
    /// <summary>zlib-compressed RGB565 little-endian pixels, base64. Null when unchanged.</summary>
    public string? Data { get; init; }
    public bool Unchanged { get; init; }
    public bool MouseAvailable { get; init; }
    public int GuestWidth { get; init; }
    public int GuestHeight { get; init; }
}

public enum ConsoleInputKind
{
    Scancodes,
    MouseMove,
    MouseButton,
    CtrlAltDel,
    TypeText,
}

public sealed record ConsoleInput
{
    public Guid VmId { get; init; }
    public ConsoleInputKind Kind { get; init; }
    public byte[]? Scancodes { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    /// <summary>1-based button index (1 left, 2 right, 3 middle).</summary>
    public int Button { get; init; }
    public bool Down { get; init; }
    public string? Text { get; init; }
}
