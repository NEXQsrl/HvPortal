namespace HvPortal.Contracts.HyperV;

/// <summary>
/// Where restore points are written and read (protocol 9): a local folder of the host ("D:\Backups") or an SMB share
/// ("\\nas01\backups\hyperv"). Credentials are for shares outside the domain (a NAS in a workgroup): they are taken from
/// the encrypted repository record by the gateway when the command is sent (never stored in job parameters), travel only
/// over the mutually authenticated agent channel, are used by the operations script for that one operation and are
/// never logged. ToString never shows them.
/// Hyper-V itself (vmms) reaches a share only with the host computer account: with credentials the export is prepared
/// in <see cref="StagingPath"/> on the host and then copied to the share by the agent.
/// </summary>
public sealed class BackupTarget
{
    public required string Path { get; init; }
    public string? UserName { get; init; }
    public string? Password { get; init; }
    /// <summary>Local folder of the host for the temporary copy (required when credentials are used; also used by restore tests).</summary>
    public string? StagingPath { get; init; }

    public bool IsShare => Path.StartsWith(@"\\", StringComparison.Ordinal);

    public override string ToString() => $"BackupTarget {Path}";
}

/// <summary>Checkpoint used to take a consistent copy of a running VM.</summary>
public static class BackupCheckpointTypes
{
    /// <summary>Production checkpoint (guest VSS / file system freeze through the integration services), falls back to a standard checkpoint.</summary>
    public const string Production = "Production";
    /// <summary>Production checkpoint only: the backup fails when the guest cannot take part (no integration services, VSS error).</summary>
    public const string ProductionOnly = "ProductionOnly";
    /// <summary>Standard checkpoint: memory state is not used, the copy is crash-consistent.</summary>
    public const string Standard = "Standard";
    public static readonly string[] All = [Production, ProductionOnly, Standard];
}

/// <summary>
/// Protocol 9. Back up one VM into &lt;Target.Path&gt;\&lt;Folder&gt;. Folder = "&lt;vm key&gt;\&lt;point id&gt;", decided by the gateway.
/// Steps on the host: checkpoint of the requested type, Export-VMSnapshot of that checkpoint, checkpoint removed (its
/// differencing disks merged back), SHA-256 of every exported file, manifest written last (a folder without manifest
/// is an incomplete restore point).
/// </summary>
public sealed record BackupVmRequest
{
    public Guid VmId { get; init; }
    public required BackupTarget Target { get; init; }
    public required string Folder { get; init; }
    public Guid PointId { get; init; }
    public string CheckpointType { get; init; } = BackupCheckpointTypes.Production;
    /// <summary>Read the copy back from the repository and compare its SHA-256 with the source (slower, safer).</summary>
    public bool VerifyAfterCopy { get; init; } = true;
}

/// <summary>One file of a restore point: path relative to the restore point folder.</summary>
public sealed record BackupFile(string Path, long SizeBytes, string? Sha256);

public sealed record BackupNic(string? Name, string? SwitchName, int VlanId);

/// <summary>hvportal-backup.json, written by the agent in each restore point folder.</summary>
public sealed record BackupManifest
{
    public const string FileName = "hvportal-backup.json";
    public int Format { get; init; } = 1;
    public Guid PointId { get; init; }
    public Guid VmId { get; init; }
    public string? VmName { get; init; }
    public string? HostName { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset CompletedUtc { get; init; }
    public string? CheckpointTypeRequested { get; init; }
    /// <summary>What Hyper-V actually produced ("Production", "Standard"), when it can be read.</summary>
    public string? CheckpointTypeUsed { get; init; }
    public int Generation { get; init; }
    public string? ConfigurationVersion { get; init; }
    public int ProcessorCount { get; init; }
    public long MemoryStartupMb { get; init; }
    public IReadOnlyList<BackupNic> NetworkAdapters { get; init; } = [];
    /// <summary>Relative path of the exported configuration (.vmcx) used by Import-VM.</summary>
    public string? ConfigFile { get; init; }
    /// <summary>Relative paths of the virtual disks (.vhdx/.vhd).</summary>
    public IReadOnlyList<string> Disks { get; init; } = [];
    public IReadOnlyList<BackupFile> Files { get; init; } = [];
    public long TotalBytes { get; init; }
    public string? AgentVersion { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>A restore point folder in a repository (VerifyBackup, RemoveBackup, ListBackups use Target only).</summary>
public sealed record BackupPointRequest
{
    public required BackupTarget Target { get; init; }
    public string? Folder { get; init; }
    public Guid PointId { get; init; }
}

public sealed record BackupVerifyResult(bool Ok, int FilesChecked, long BytesChecked, IReadOnlyList<string> Problems);

public static class RestoreModes
{
    /// <summary>A new VM (new id, new name) from the restore point; the original VM is not touched.</summary>
    public const string NewVm = "NewVm";
    /// <summary>
    /// Over the original VM: it is turned off and removed from Hyper-V, its disk files are renamed (*.pre-restore-&lt;time&gt;),
    /// then the restore point is imported with the original id, name and folders.
    /// </summary>
    public const string Replace = "Replace";
    /// <summary>Restore test: a new VM with network adapters disconnected, started, checked (heartbeat), then turned off and deleted with its files.</summary>
    public const string Test = "Test";
    public static readonly string[] All = [NewVm, Replace, Test];
}

public static class RestoreNetworks
{
    /// <summary>Network adapters connected to the switches recorded in the restore point (when they exist on the host).</summary>
    public const string Same = "Same";
    public const string Disconnected = "Disconnected";
    /// <summary>Every adapter connected to <see cref="RestoreBackupRequest.SwitchName"/>.</summary>
    public const string Switch = "Switch";
    public static readonly string[] All = [Same, Disconnected, Switch];
}

/// <summary>
/// Protocol 9. Restore a VM from &lt;Source.Path&gt;\&lt;Folder&gt; (Import-VM -Copy). From a share with credentials the restore
/// point is first copied to Source.StagingPath (Hyper-V cannot read the share with them). Location = host folder of the
/// restored VM (&lt;Location&gt;\&lt;name&gt;); null = host default folders (not for Replace, which uses the original folders).
/// </summary>
public sealed record RestoreBackupRequest
{
    public required BackupTarget Source { get; init; }
    public required string Folder { get; init; }
    public Guid PointId { get; init; }
    public required string Mode { get; init; }
    public string? NewName { get; init; }
    public string? Location { get; init; }
    public string Network { get; init; } = RestoreNetworks.Same;
    public string? SwitchName { get; init; }
    public bool StartAfter { get; init; }
    /// <summary>Replace: Hyper-V id of the VM to replace; it must be the VM the restore point was taken from.</summary>
    public Guid? ReplaceVmId { get; init; }
    /// <summary>Test: how long to wait for the guest heartbeat (60-3600 s).</summary>
    public int TestTimeoutSeconds { get; init; } = 600;
}

public sealed record RestoreBackupResult
{
    public string? Mode { get; init; }
    public Guid? VmId { get; init; }
    public string? Name { get; init; }
    public string? Path { get; init; }
    /// <summary>Test: true when the VM started and the guest reported heartbeat OK within the timeout.</summary>
    public bool? TestPassed { get; init; }
    public int? TestSeconds { get; init; }
    public string? TestDetail { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Protocol 9. Copy Disk (relative path of a disk listed in the manifest) to &lt;DestinationFolder&gt; on the host; with
/// AttachVmId the copy is added to that VM as a SCSI disk (a running generation 2 VM accepts it), so files are restored
/// from inside the guest, with the guest's own permissions.
/// </summary>
public sealed record RestoreDiskRequest
{
    public required BackupTarget Source { get; init; }
    public required string Folder { get; init; }
    public Guid PointId { get; init; }
    public required string Disk { get; init; }
    public required string DestinationFolder { get; init; }
    public Guid? AttachVmId { get; init; }
}

public sealed record RestoreDiskResult(string Path, bool Attached, Guid? VmId);

/// <summary>Protocol 9. Attach a disk of a restore point to the host, read-only, to browse its files (MountBackupDisk).</summary>
public sealed record MountBackupDiskRequest
{
    public required BackupTarget Source { get; init; }
    public required string Folder { get; init; }
    public Guid PointId { get; init; }
    public required string Disk { get; init; }
}

public sealed record MountedVolume(string Root, string? Label, string? FileSystem, long? SizeBytes);

/// <summary>A disk mounted by the agent: only the volumes listed here can be browsed with ListBackupFiles/ReadBackupFile.</summary>
public sealed record BackupMount(Guid MountId, IReadOnlyList<MountedVolume> Volumes, DateTimeOffset ExpiresUtc, bool Staged);

public sealed record BackupMountRef(Guid MountId);

public sealed record BackupFileListRequest(Guid MountId, string Path);

public sealed record BackupFileReadRequest(Guid MountId, string Path, long Offset, int Length);

/// <summary>Protocol 9. GetBackupReadiness: checks for a repository (optional) and the VMs to protect (optional).</summary>
public sealed record BackupReadinessRequest
{
    public BackupTarget? Target { get; init; }
    public IReadOnlyList<Guid> VmIds { get; init; } = [];
}

/// <summary>One check: Status Ok / Warning / Critical; Fix = what to do (a command when there is one).</summary>
public sealed record BackupCheck(string Key, string Status, string Message, string? Fix);

public sealed record BackupReadiness(IReadOnlyList<BackupCheck> Checks);
