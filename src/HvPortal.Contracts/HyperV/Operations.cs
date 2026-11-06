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
    /// <summary>Protocol 5. Folders and files of a host folder (file explorer). In-process in the agent.</summary>
    ListFolder,
    /// <summary>Protocol 5. One chunk of a file (download). Files open for writing by others (running VM disks) cannot be read.</summary>
    ReadFile,
    /// <summary>Protocol 5. Upload: begin (temporary hidden file), chunk, commit (size + SHA-256 verified, then renamed), abort.</summary>
    WriteFileBegin,
    WriteFileChunk,
    WriteFileCommit,
    WriteFileAbort,
    /// <summary>Protocol 7. Physical network adapters of the host and the virtual switch bound to each (for external switches).</summary>
    ListNetAdapters,
    /// <summary>Protocol 7. Create a virtual switch (NewSwitchRequest).</summary>
    NewSwitch,
    /// <summary>Protocol 7. Remove a virtual switch by id; refused while VM adapters are connected to it (SwitchRef).</summary>
    RemoveSwitch,
    /// <summary>Protocol 8. Copy the disks of a VM that is off into a new template folder (NewTemplateRequest). The VM is not changed.</summary>
    NewTemplate,
    /// <summary>Protocol 8. Delete the folder of a template (TemplateFolderRequest): only disk files directly inside it.</summary>
    RemoveTemplate,
}

public static class HvOperations
{
    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Agent.AgentProtocol.Json);
    public static T? FromElement<T>(JsonElement? e) => e is { } v ? v.Deserialize<T>(Agent.AgentProtocol.Json) : default;
}

// ---------------------------------------------------------------- arguments

public sealed record VmRef(Guid VmId);

public sealed record NetworkAdapterChange(string AdapterId, string? SwitchName, int? VlanId);

/// <summary>
/// Folder (protocol 7): where the new VHDX is created. Null = host default virtual disk folder. Set by the gateway only
/// (storage limits of a VM folder, ADR-024); the agent validates it like a VM location (no system folders, no links).
/// </summary>
public sealed record NewDiskSpec(int SizeGb, bool Fixed, string? Folder = null);

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
    /// <summary>Gateway only: the template this VM is created from (the agent never uses it).</summary>
    public Guid? TemplateId { get; init; }
    /// <summary>
    /// Protocol 8. Disk files of the template, the boot disk first: each one is COPIED into the folder of the new VM and
    /// attached (no new empty disk). Set by the gateway from its own records, never taken from a client. With a
    /// template, DiskSizeGb greater than the first disk grows the copy (0 = keep the size); it never shrinks it.
    /// </summary>
    public IReadOnlyList<string> TemplateDisks { get; init; } = [];
    /// <summary>Protocol 8. Template library folders of the host: every TemplateDisks entry must be inside one of them.</summary>
    public IReadOnlyList<string> TemplateRoots { get; init; } = [];
    /// <summary>Gateway only: virtual size (GB, rounded up) of the template disks after the first, counted by the folder quotas.</summary>
    public int TemplateExtraDiskGb { get; init; }
}

/// <summary>Protocol 8. <see cref="Folder"/> = &lt;template library&gt;\&lt;template id&gt;, set by the gateway; must be inside one of <see cref="Roots"/>.</summary>
public sealed record NewTemplateRequest
{
    public Guid VmId { get; init; }
    public required string Folder { get; init; }
    public IReadOnlyList<string> Roots { get; init; } = [];
}

/// <summary>Protocol 8. Folder of an existing template (RemoveTemplate).</summary>
public sealed record TemplateFolderRequest
{
    public required string Folder { get; init; }
    public IReadOnlyList<string> Roots { get; init; } = [];
}

/// <summary>One disk of a template: file name inside the template folder, virtual size, size of the file.</summary>
public sealed record TemplateDisk(string File, long SizeBytes, long FileBytes);

/// <summary>What NewTemplate copied and the hardware of the source VM, proposed again when a VM is created from the template.</summary>
public sealed record NewTemplateResult
{
    public string? Folder { get; init; }
    public IReadOnlyList<TemplateDisk> Disks { get; init; } = [];
    public int Generation { get; init; }
    public int ProcessorCount { get; init; }
    public long MemoryStartupMb { get; init; }
    public bool DynamicMemory { get; init; }
    public long? MemoryMinimumMb { get; init; }
    public long? MemoryMaximumMb { get; init; }
    /// <summary>Generation 2: the Secure Boot template, or "Off".</summary>
    public string? SecureBootTemplate { get; init; }
    public bool TpmEnabled { get; init; }
}

public sealed record IsoListRequest(IReadOnlyList<string> Roots);

/// <summary>Path null = list the local fixed drives.</summary>
public sealed record FolderBrowseRequest(string? Path, bool IncludeIsos);

public sealed record DriveEntry(string Path, string? Label, long TotalBytes, long FreeBytes);

public sealed record FolderEntry(string Name, string Path);

public sealed record FileEntry(string Name, string Path, long SizeBytes, DateTimeOffset ModifiedUtc);

/// <summary>File transfer between the portal and a host (protocol 5): fixed-size chunks, base64 in the agent channel.</summary>
public static class FileTransfer
{
    public const int ChunkBytes = 4 * 1024 * 1024;
    /// <summary>Base64 length of a full chunk.</summary>
    public const int MaxChunkBase64 = (ChunkBytes + 2) / 3 * 4;
    public const long MaxUploadBytes = 1L << 40; // 1 TB
}

public sealed record FileListRequest(string Path);
public sealed record FileReadRequest(string Path, long Offset, int Length);
public sealed record FileChunk(long Offset, long TotalBytes, DateTimeOffset ModifiedUtc, string Data, bool Eof);
public sealed record FileWriteBeginRequest(string Folder, string Name, long SizeBytes, bool Overwrite);
public sealed record FileWriteBeginResult(Guid UploadId, string Path);
public sealed record FileWriteChunkRequest(Guid UploadId, long Offset, string Data);
public sealed record FileWriteCommitRequest(Guid UploadId, string Sha256);
public sealed record FileWriteAbortRequest(Guid UploadId);
public sealed record FileWriteResult(string Path, long SizeBytes, string Sha256);

public sealed record FolderListing
{
    public string? Path { get; init; }
    public string? Parent { get; init; }
    public IReadOnlyList<DriveEntry> Drives { get; init; } = [];
    public IReadOnlyList<FolderEntry> Folders { get; init; } = [];
    public IReadOnlyList<IsoFile> Isos { get; init; } = [];
    /// <summary>ListFolder only: all files (hidden/system/linked excluded).</summary>
    public IReadOnlyList<FileEntry> Files { get; init; } = [];
    /// <summary>Free space of the drive containing Path.</summary>
    public long? FreeBytes { get; init; }
    public bool Truncated { get; init; }
}

public sealed record CheckpointRequest(Guid VmId, string? Name, string? CheckpointId, bool IncludeChildren);

/// <summary>Hyper-V virtual switch types (New-VMSwitch).</summary>
public static class SwitchTypes
{
    /// <summary>Bound to a physical network adapter: VMs reach the physical network.</summary>
    public const string External = "External";
    /// <summary>VMs and the host only.</summary>
    public const string Internal = "Internal";
    /// <summary>VMs only.</summary>
    public const string Private = "Private";
    public static readonly string[] All = [External, Internal, Private];
}

/// <summary>
/// NetAdapterName (External only) = name of a physical adapter as returned by ListNetAdapters. AllowManagementOs
/// (External only): the host keeps using the adapter through a virtual adapter on the switch.
/// </summary>
public sealed record NewSwitchRequest(string Name, string Type, string? NetAdapterName, bool AllowManagementOs, string? Notes);

/// <summary>A switch identified by its id (names are not unique in Hyper-V); Name is used for messages only.</summary>
public sealed record SwitchRef(string SwitchId, string? Name);

public sealed record NetAdapterEntry(string Name, string? Description, string? Status, string? LinkSpeed, string? MacAddress, string? SwitchName);

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
