namespace HvPortal.Contracts.HyperV;

/// <summary>
/// Protocol 9. Move a VM to another Hyper-V host (Move-VM). Running VMs are live-migrated; VMs that are off are moved
/// with their configuration. With <see cref="IncludeStorage"/> the files go to &lt;DestinationPath&gt;\&lt;VM name&gt; on the
/// destination (shared-nothing migration); without it the files stay where they are (shared storage, SMB or CSV).
/// The destination comes from the gateway's own host records, never from a client. Live migration must be enabled on
/// both hosts (Enable-VMMigration, authentication, networks): the agent does not change host settings.
/// </summary>
public sealed record MoveVmRequest
{
    public Guid VmId { get; init; }
    /// <summary>DNS name of the destination host (from its inventory facts).</summary>
    public required string DestinationHost { get; init; }
    public bool IncludeStorage { get; init; } = true;
    /// <summary>Local folder of the destination host; required with IncludeStorage.</summary>
    public string? DestinationPath { get; init; }
}

/// <summary>Protocol 9. Move the files of a VM (configuration, disks, checkpoints, smart paging) to &lt;DestinationPath&gt;\&lt;VM name&gt; on the same host.</summary>
public sealed record MoveVmStorageRequest
{
    public Guid VmId { get; init; }
    public required string DestinationPath { get; init; }
}

public sealed record MoveVmResult(Guid VmId, string? Name, string? DestinationHost, string? Path, string? State);

/// <summary>Protocol 9. Create &lt;Parent&gt;\&lt;Name&gt; (file explorer). Parent must be an allowed folder of the host.</summary>
public sealed record NewFolderRequest(string Parent, string Name);

/// <summary>Result of RemoveVmWithDisks: the disk files deleted, kept (with the reason) and not deletable (with the error).</summary>
public sealed record VmDiskDeletion
{
    public IReadOnlyList<string> Deleted { get; init; } = [];
    public IReadOnlyList<string> Kept { get; init; } = [];
    public IReadOnlyList<string> Failed { get; init; } = [];
}
