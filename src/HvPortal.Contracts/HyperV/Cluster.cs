using System.Text.RegularExpressions;

namespace HvPortal.Contracts.HyperV;

/// <summary>
/// Protocol 11. What a host agent reads about the failover cluster its host belongs to, with every inventory (WMI
/// root\MSCluster and Win32_Volume, read-only). Null with older agents and on hosts without the Cluster service.
/// <see cref="Error"/> says why the cluster could not be read (typically: the agent account has no access to the
/// cluster, see Grant-ClusterAccess); the other values are then empty, never guessed.
/// </summary>
public sealed record ClusterFacts
{
    /// <summary>Name of the failover cluster (MSCluster_Cluster.Name).</summary>
    public string? Name { get; init; }
    /// <summary>Fully qualified name of the cluster when Windows reports one.</summary>
    public string? Fqdn { get; init; }
    /// <summary>Name of this host as a node of the cluster.</summary>
    public string? NodeName { get; init; }
    /// <summary>State of this node: Up, Down, Paused, Joining, Unknown.</summary>
    public string? NodeState { get; init; }
    /// <summary>Drain of this node: NotInitiated, InProgress, Completed, Failed.</summary>
    public string? DrainStatus { get; init; }
    /// <summary>Hyper-V ids of the VMs that are roles of the cluster (highly available), whatever node owns them now.</summary>
    public IReadOnlyList<Guid> ClusteredVmIds { get; init; } = [];
    /// <summary>Cluster shared volumes as this node sees them (C:\ClusterStorage\VolumeN, file system CSVFS).</summary>
    public IReadOnlyList<HostVolume> SharedVolumes { get; init; } = [];
    /// <summary>Why the cluster could not be read ("access_denied: ..."), null when it was read.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Protocol 11, GetClusterStatus (read-only): the failover cluster as seen from one of its nodes. Any node can answer
/// for the whole cluster, so the gateway asks the agent of another node when one is unreachable. Each section is read
/// on its own; a section that cannot be read is listed in <see cref="Errors"/> and never guessed.
/// </summary>
public sealed record ClusterStatusReport
{
    public DateTimeOffset CollectedUtc { get; init; }
    public string? Name { get; init; }
    public string? Fqdn { get; init; }
    /// <summary>Get-Cluster AdministrativeAccessPoint: ActiveDirectoryAndDns, Dns (workgroup or multi-domain cluster), None.</summary>
    public string? AdministrativeAccessPoint { get; init; }
    public string? Domain { get; init; }
    /// <summary>Node whose agent read the report.</summary>
    public string? ReadByNode { get; init; }
    public IReadOnlyList<ClusterNodeStatus> Nodes { get; init; } = [];
    public ClusterQuorumStatus? Quorum { get; init; }
    public IReadOnlyList<ClusterNetworkStatus> Networks { get; init; } = [];
    public IReadOnlyList<ClusterSharedVolumeStatus> SharedVolumes { get; init; } = [];
    /// <summary>Storage Spaces Direct (null when the section could not be read).</summary>
    public ClusterStorageStatus? Storage { get; init; }
    /// <summary>Roles (cluster groups) other than the core and available-storage groups.</summary>
    public IReadOnlyList<ClusterRoleStatus> Roles { get; init; } = [];
    /// <summary>Resources in a failed state (Get-ClusterResource), whatever their group.</summary>
    public IReadOnlyList<ClusterResourceStatus> FailedResources { get; init; } = [];
    /// <summary>Agent 0.9.1+. State of the core cluster group (cluster name, IP addresses, witness) and its resources.</summary>
    public string? CoreGroupState { get; init; }
    public IReadOnlyList<ClusterResourceStatus> CoreResources { get; init; } = [];
    /// <summary>Agent 0.9.1+. Faults reported by the Health Service of Storage Spaces Direct (Get-HealthFault).</summary>
    public IReadOnlyList<ClusterHealthFault> HealthFaults { get; init; } = [];
    /// <summary>Agent 0.9.1+. Storage Replica groups of the node that read the report (empty when Storage Replica is not used).</summary>
    public IReadOnlyList<StorageReplicaStatus> Replication { get; init; } = [];
    public IReadOnlyList<string> Errors { get; init; } = [];
}

public sealed record ClusterNodeStatus
{
    public required string Name { get; init; }
    /// <summary>Up, Down, Paused, Joining, Unknown.</summary>
    public string? State { get; init; }
    /// <summary>NotInitiated, InProgress, Completed, Failed.</summary>
    public string? DrainStatus { get; init; }
    /// <summary>Configured vote (1 / 0) and the vote dynamic quorum gives the node now.</summary>
    public int? NodeWeight { get; init; }
    public int? DynamicWeight { get; init; }
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    /// <summary>Storage Spaces Direct: the disks of this node are in storage maintenance mode.</summary>
    public bool? StorageMaintenance { get; init; }
}

public sealed record ClusterQuorumStatus
{
    /// <summary>Get-ClusterQuorum QuorumType: NodeMajority, NodeAndDiskMajority, NodeAndFileShareMajority, NodeAndCloudMajority, DiskOnly, Majority, ...</summary>
    public string? QuorumType { get; init; }
    /// <summary>None, Disk, FileShare, Cloud.</summary>
    public string? WitnessType { get; init; }
    public string? WitnessName { get; init; }
    /// <summary>Online, Offline, Failed, ... (state of the witness resource).</summary>
    public string? WitnessState { get; init; }
    /// <summary>File share witness: the share; cloud witness: the storage account name (never a key).</summary>
    public string? WitnessTarget { get; init; }
    public bool? DynamicQuorum { get; init; }
    /// <summary>Vote the witness has now (dynamic witness).</summary>
    public int? WitnessDynamicWeight { get; init; }
}

public sealed record ClusterNetworkStatus
{
    public required string Name { get; init; }
    /// <summary>Up, Down, Partitioned, Unavailable, Unknown.</summary>
    public string? State { get; init; }
    /// <summary>None, Cluster, ClusterAndClient.</summary>
    public string? Role { get; init; }
    public string? Address { get; init; }
}

public sealed record ClusterSharedVolumeStatus
{
    public required string Name { get; init; }
    /// <summary>C:\ClusterStorage\VolumeN.</summary>
    public string? Path { get; init; }
    public string? OwnerNode { get; init; }
    /// <summary>Online, Offline, Failed, ...</summary>
    public string? State { get; init; }
    public long? SizeBytes { get; init; }
    public long? FreeBytes { get; init; }
    /// <summary>Access of the volume on the node that read the report: Direct, FileSystemRedirected, BlockRedirected.</summary>
    public string? Access { get; init; }
    public bool? MaintenanceMode { get; init; }
}

public sealed record ClusterStorageStatus
{
    /// <summary>Storage Spaces Direct is enabled on the cluster.</summary>
    public bool S2dEnabled { get; init; }
    /// <summary>Get-ClusterStorageSpacesDirect State (Enabled, Disabled, ...) when read.</summary>
    public string? S2dState { get; init; }
    public string? CacheState { get; init; }
    public IReadOnlyList<ClusterPoolStatus> Pools { get; init; } = [];
    public IReadOnlyList<ClusterVirtualDiskStatus> VirtualDisks { get; init; } = [];
    public IReadOnlyList<ClusterPhysicalDiskStatus> PhysicalDisks { get; init; } = [];
    /// <summary>Repair / rebalance jobs running on the pool (Get-StorageJob): the data is not fully resilient until they end.</summary>
    public IReadOnlyList<ClusterStorageJobStatus> Jobs { get; init; } = [];
}

public sealed record ClusterPoolStatus
{
    public required string Name { get; init; }
    public string? HealthStatus { get; init; }
    public string? OperationalStatus { get; init; }
    public long? SizeBytes { get; init; }
    public long? AllocatedBytes { get; init; }
    public bool? IsReadOnly { get; init; }
}

public sealed record ClusterVirtualDiskStatus
{
    public required string Name { get; init; }
    public string? HealthStatus { get; init; }
    public string? OperationalStatus { get; init; }
    /// <summary>Mirror, Parity, ...</summary>
    public string? Resiliency { get; init; }
    public int? DataCopies { get; init; }
    public long? SizeBytes { get; init; }
    public long? FootprintBytes { get; init; }
}

public sealed record ClusterPhysicalDiskStatus
{
    public required string Name { get; init; }
    public string? SerialNumber { get; init; }
    public string? MediaType { get; init; }
    /// <summary>Auto-Select, Journal (cache), Retired, ...</summary>
    public string? Usage { get; init; }
    public string? HealthStatus { get; init; }
    public string? OperationalStatus { get; init; }
    public long? SizeBytes { get; init; }
}

public sealed record ClusterStorageJobStatus
{
    public required string Name { get; init; }
    public string? State { get; init; }
    public int? PercentComplete { get; init; }
    public long? BytesTotal { get; init; }
    public long? BytesProcessed { get; init; }
    /// <summary>Agent 0.9.1+. Seconds the job has been running (Get-StorageJob ElapsedTime).</summary>
    public long? ElapsedSeconds { get; init; }
}

/// <summary>A fault of the Health Service (Get-HealthFault): what is wrong and what Microsoft recommends doing.</summary>
public sealed record ClusterHealthFault
{
    /// <summary>Critical, Warning, Minor, ...</summary>
    public string? Severity { get; init; }
    public string? FaultType { get; init; }
    public string? Reason { get; init; }
    public string? RecommendedAction { get; init; }
    public string? FaultingObject { get; init; }
}

/// <summary>A Storage Replica group (Get-SRGroup) and the state of its replicas.</summary>
public sealed record StorageReplicaStatus
{
    public required string Name { get; init; }
    public string? ReplicationMode { get; init; }
    /// <summary>ReplicationStatus of the replicas: ContinuouslyReplicating, InitialBlockCopy, WaitingForDestination, ...</summary>
    public string? Status { get; init; }
    public long? BytesRemaining { get; init; }
    public bool? IsPrimary { get; init; }
    public string? Partner { get; init; }
}

public sealed record ClusterRoleStatus
{
    public required string Name { get; init; }
    /// <summary>VirtualMachine, FileServer, ...</summary>
    public string? GroupType { get; init; }
    /// <summary>Online, Offline, Failed, PartialOnline, Pending.</summary>
    public string? State { get; init; }
    public string? OwnerNode { get; init; }
    /// <summary>Hyper-V id of the VM of a VirtualMachine role.</summary>
    public Guid? VmId { get; init; }
}

public sealed record ClusterResourceStatus
{
    public required string Name { get; init; }
    public string? ResourceType { get; init; }
    public string? State { get; init; }
    public string? OwnerGroup { get; init; }
    public string? OwnerNode { get; init; }
}

/// <summary>Values of <see cref="ClusterNodeMaintenanceRequest.Action"/>.</summary>
public static class MaintenanceActions
{
    /// <summary>Suspend-ClusterNode -Drain (roles moved to the other nodes), then storage maintenance of the node's disks (S2D).</summary>
    public const string Enter = "Enter";
    /// <summary>Storage maintenance off (S2D), then Resume-ClusterNode.</summary>
    public const string Exit = "Exit";
    public static readonly string[] All = [Enter, Exit];
}

/// <summary>
/// Protocol 11. Put a node of the cluster in maintenance or bring it back. The node name comes from the gateway's
/// records (inventory of that node), never from a client. Any node of the cluster can run it.
/// </summary>
public sealed record ClusterNodeMaintenanceRequest
{
    public required string NodeName { get; init; }
    public required string Action { get; init; }
    /// <summary>Exit only: move back the roles that were on the node (Resume-ClusterNode -Failback Immediate).</summary>
    public bool Failback { get; init; }
    /// <summary>
    /// With Storage Spaces Direct: Enter also puts the node's disks in storage maintenance mode, Exit takes them out
    /// (Microsoft's procedure for taking an S2D server offline). Ignored without S2D.
    /// </summary>
    public bool StorageMaintenance { get; init; } = true;
}

public sealed record ClusterNodeMaintenanceResult
{
    public string? NodeName { get; init; }
    public string? State { get; init; }
    public string? DrainStatus { get; init; }
    public bool StorageMaintenance { get; init; }
    public string? Warning { get; init; }
}

/// <summary>
/// Protocol 11. Move a clustered VM to another node of its cluster (Move-ClusterVirtualMachineRole: live migration when
/// running, quick migration otherwise). The files stay on the cluster storage. The node comes from the gateway's records.
/// </summary>
public sealed record MoveClusterVmRequest
{
    public Guid VmId { get; init; }
    public required string NodeName { get; init; }
}

/// <summary>Protocol 12. The cluster shared volume (its cluster resource name) and the node that becomes its owner.</summary>
public sealed record MoveSharedVolumeRequest
{
    public required string VolumeName { get; init; }
    public required string NodeName { get; init; }
}

public static partial class ClusterNames
{
    /// <summary>Cluster folder where Windows mounts the cluster shared volumes (on the system drive).</summary>
    public const string SharedVolumeFolder = "ClusterStorage";

    // One label of a host name: letters, digits and hyphens, not starting or ending with a hyphen (checked apart).
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9-]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex NodeNameRegex();

    // Cluster resource names such as "Cluster Virtual Disk (Volume1)": letters, digits, space and . _ ( ) - only.
    [GeneratedRegex(@"^[\p{L}\p{N}][\p{L}\p{N} ._()-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex ResourceNameRegex();

    /// <summary>Name of a cluster resource (a cluster shared volume): 1-128 safe characters, not ending with a space.</summary>
    public static bool IsValidResourceName(string? name) => name is not null && ResourceNameRegex().IsMatch(name) && !name.EndsWith(' ');

    /// <summary>Name of a cluster node (computer name): one DNS label, 1-63 characters.</summary>
    public static bool IsValidNodeName(string? name) => name is not null && NodeNameRegex().IsMatch(name) && !name.EndsWith('-');

    /// <summary>
    /// True when the path is on a cluster shared volume: &lt;drive&gt;:\ClusterStorage\&lt;volume&gt;[\...]. Text only; the
    /// agent checks on the host that the volume really is a CSV.
    /// </summary>
    public static bool IsSharedVolumePath(string? path)
    {
        if (!HostPaths.IsValidLocalFolder(path) && !HostPaths.IsValidFilePath(path))
        {
            return false;
        }
        var segments = path!.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 3 && segments[1].Equals(SharedVolumeFolder, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The volume part of a CSV path ("C:\ClusterStorage\Volume1"), null when the path is not on a CSV.</summary>
    public static string? SharedVolumeRoot(string? path)
    {
        if (!IsSharedVolumePath(path))
        {
            return null;
        }
        var segments = path!.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return $"{char.ToUpperInvariant(segments[0][0])}:\\{segments[1]}\\{segments[2]}";
    }
}
