using System.Text.Json;
using System.Text.Json.Serialization;
using HvPortal.Contracts.HyperV;

namespace HvPortal.Contracts.Agent;

/// <summary>
/// Gateway &lt;-&gt; agent protocol over a WebSocket opened by the agent (mTLS).
/// The set of commands is closed and typed; there is intentionally no "run script" message.
/// </summary>
public static class AgentProtocol
{
    /// <summary>
    /// 2 = v0.3: HyperVOperation, ConsoleFrame and ConsoleInput commands.
    /// 3 = v0.3.1: BrowseFolders operation, NewVm.Location; unreadable commands are answered with Rejected.
    /// 4 = v0.3.2: ListVmGroups / SyncVmGroups (native Hyper-V VM groups mirrored from portal groups).
    /// 5 = v0.3.3: host file explorer (ListFolder, ReadFile, WriteFile*).
    /// 6 = v0.4.0: management agents (remote Hyper-V hosts reached with credentials): AgentCommand.TargetHostId,
    ///     TargetsConfiguration and TargetInventory messages, AgentRole in enrollment and Hello.
    /// 7 = v0.5.0: virtual switches (ListNetAdapters, NewSwitch, RemoveSwitch), NewDiskSpec.Folder, RestoreCheckpoint
    ///     turns off a running VM first, VmSummary.MemoryMaximumMb.
    /// 8 = v0.6.0: VM templates (NewTemplate, RemoveTemplate, NewVm.TemplateDisks: the disks of a template are copied
    ///     into the new VM); long copies may run for hours (see CommandExecutor).
    /// 9 = v0.7.0: host health, VM move (host / storage), new folders, allowed storage folders of the host, VLAN of new
    ///     and added network adapters, backups and restores, update of the agent sent by the gateway (UpdateAgent).
    /// 10 = v0.8.0: resource use history of VMs (GetVmMetrics, SetVmResourceMetering, ResetVmResourceMetering).
    /// 11 = v0.9.0: failover clusters (HostFacts.Cluster, GetClusterStatus, SetClusterNodeMaintenance, MoveClusterVm,
    ///     NewVm.HighlyAvailable) and VM folders on cluster shared volumes.
    /// 12 = v0.9.1: owner of a cluster shared volume moved to another node (MoveClusterSharedVolume).
    /// 13 = v0.9.4: rename and delete in the file explorer (RenamePath, DeletePath).
    /// </summary>
    public const int Version = 13;

    /// <summary>Agent port: the agent package, for agents updating themselves (mutual TLS, same checks as the channel).</summary>
    public const string PackagePath = "/agent/v1/package";

    /// <summary>Agent version shipped with each protocol (messages for agents that are too old).</summary>
    public static string MinimumAgentFor(int protocol) => protocol switch
    {
        >= 13 => "0.9.4",
        12 => "0.9.1",
        11 => "0.9.0",
        10 => "0.8.0",
        9 => "0.7.0",
        8 => "0.6.0",
        7 => "0.5.0",
        6 => "0.4.0",
        5 => "0.3.3",
        4 => "0.3.2",
        3 => "0.3.1",
        _ => "0.3",
    };

    /// <summary>
    /// Lowest agent protocol able to execute the command as intended. The gateway never sends a command to an older
    /// agent: older agents either cannot parse it or would silently ignore new fields (e.g. create the VM elsewhere).
    /// </summary>
    public static int RequiredVersion(AgentCommand command)
    {
        if (command.Kind is AgentCommandKind.UpdateAgent)
        {
            return 9;
        }
        // An older agent would ignore the flag and keep the disks while the user asked to delete them.
        if (command.Kind is AgentCommandKind.DeleteVm && command.DeleteDisks == true)
        {
            return 9;
        }
        if (command.Operation is >= HvOperation.RenamePath)
        {
            return 13;
        }
        if (command.Operation is >= HvOperation.MoveClusterSharedVolume)
        {
            return 12;
        }
        if (command.Operation is >= HvOperation.GetClusterStatus)
        {
            return 11;
        }
        // An older agent would ignore the flag and create a VM that is not highly available.
        if (command.Operation is HvOperation.NewVm && command.OperationArgs is { ValueKind: JsonValueKind.Object } haArgs
            && haArgs.TryGetProperty("highlyAvailable", out var ha) && ha.ValueKind == JsonValueKind.True)
        {
            return 11;
        }
        if (command.Operation is >= HvOperation.GetVmMetrics)
        {
            return 10;
        }
        if (command.Operation is >= HvOperation.GetHostHealth)
        {
            return 9;
        }
        // Older agents would ignore the VLAN and leave the adapter untagged (on the wrong network).
        if (command.Operation is HvOperation.NewVm && command.OperationArgs is { ValueKind: JsonValueKind.Object } vlanArgs
            && vlanArgs.TryGetProperty("vlanId", out var vlan) && vlan.ValueKind == JsonValueKind.Number)
        {
            return 9;
        }
        if (command.Operation is HvOperation.SetVmConfig && command.OperationArgs is { ValueKind: JsonValueKind.Object } addArgs
            && addArgs.TryGetProperty("addNetworkAdapterVlanId", out var addVlan) && addVlan.ValueKind == JsonValueKind.Number)
        {
            return 9;
        }
        if (command.TargetHostId is not null)
        {
            return 6;
        }
        if (command.Kind is not (AgentCommandKind.HyperVOperation or AgentCommandKind.ConsoleFrame or AgentCommandKind.ConsoleInput))
        {
            return 1;
        }
        if (command.Operation is HvOperation.NewTemplate or HvOperation.RemoveTemplate)
        {
            return 8;
        }
        // An older agent would ignore the template disks and create an empty VM that looks like a successful deployment.
        if (command.Operation is HvOperation.NewVm && command.OperationArgs is { ValueKind: JsonValueKind.Object } vmArgs
            && vmArgs.TryGetProperty("templateDisks", out var disks) && disks.ValueKind == JsonValueKind.Array && disks.GetArrayLength() > 0)
        {
            return 8;
        }
        if (command.Operation is HvOperation.ListNetAdapters or HvOperation.NewSwitch or HvOperation.RemoveSwitch)
        {
            return 7;
        }
        // An older agent would ignore the folder and create the disk in the host default folder (outside the allowed storage).
        if (command.Operation is HvOperation.SetVmConfig && command.OperationArgs is { ValueKind: JsonValueKind.Object } cfg
            && cfg.TryGetProperty("newDisk", out var nd) && nd.ValueKind == JsonValueKind.Object
            && nd.TryGetProperty("folder", out var folder) && folder.ValueKind == JsonValueKind.String)
        {
            return 7;
        }
        if (command.Operation is HvOperation.ListFolder or HvOperation.ReadFile or HvOperation.WriteFileBegin or HvOperation.WriteFileChunk
            or HvOperation.WriteFileCommit or HvOperation.WriteFileAbort)
        {
            return 5;
        }
        if (command.Operation is HvOperation.ListVmGroups or HvOperation.SyncVmGroups)
        {
            return 4;
        }
        if (command.Operation is HvOperation.BrowseFolders)
        {
            return 3;
        }
        if (command.Operation is HvOperation.NewVm && command.OperationArgs is { ValueKind: JsonValueKind.Object } args
            && args.TryGetProperty("location", out var loc) && loc.ValueKind == JsonValueKind.String)
        {
            return 3;
        }
        return 2;
    }
    /// <summary>Large enough for an uncompressible 1920x1200 console frame (RGB565, base64 in JSON).</summary>
    public const int MaxMessageBytes = 8 * 1024 * 1024;
    public const string ChannelPath = "/agent/v1/channel";
    public const string EnrollPath = "/agent/v1/enroll";

    public static readonly JsonSerializerOptions Json = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            MaxDepth = 32,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        };
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }
}

public enum AgentMessageType
{
    Hello,
    Inventory,
    Command,
    CommandProgress,
    CommandResult,
    Ping,
    Pong,
    RenewCertificate,
    RenewCertificateResult,
    Error,
    /// <summary>Gateway -> management agent: the remote hosts it must manage, with their credentials (protocol 6).</summary>
    TargetsConfiguration,
    /// <summary>Management agent -> gateway: inventory of one remote host (protocol 6).</summary>
    TargetInventory,
}

/// <summary>
/// Host: the agent runs on the Hyper-V host and manages it locally (default).
/// Management: the agent runs on a management server and manages the remote hosts the gateway assigns to it, with the
/// credentials stored in the portal (WMI/DCOM, WinRM, SMB administrative shares).
/// </summary>
public enum AgentRole
{
    Host,
    Management,
}

public sealed record AgentEnvelope(AgentMessageType Type, string? CorrelationId, JsonElement? Payload)
{
    public static AgentEnvelope Create<T>(AgentMessageType type, string? correlationId, T payload) =>
        new(type, correlationId, JsonSerializer.SerializeToElement(payload, AgentProtocol.Json));

    public T? Read<T>() => Payload is { } p ? p.Deserialize<T>(AgentProtocol.Json) : default;
}

public sealed record AgentHello
{
    public required int ProtocolVersion { get; init; }
    public required string AgentVersion { get; init; }
    public required string MachineName { get; init; }
    public string? OsVersion { get; init; }
    /// <summary>Informative (protocol 6+): the gateway relies on its own enrollment records, not on this value.</summary>
    public AgentRole Role { get; init; } = AgentRole.Host;
    /// <summary>Protocol 9: the scheduled task that installs updates sent by the gateway is registered on the machine.</summary>
    public bool? UpdaterInstalled { get; init; }
    /// <summary>Protocol 9: outcome of the last update run by that task ("&lt;time&gt; ok 0.7.1: ..." or the error), when there is one.</summary>
    public string? LastUpdateResult { get; init; }
}

public enum AgentCommandKind
{
    CollectInventory,
    GetVm,
    VmPower,
    /// <summary>Disks and network adapters (with guest IPs) of one VM. Read-only, on demand.</summary>
    GetVmDetails,
    /// <summary>Msvm_VirtualSystemManagementService.DestroySystem. VM must be Off or Saved. Disk files are not deleted.</summary>
    DeleteVm,
    /// <summary>Typed Hyper-V module operation (see HvOperation). Arguments in OperationArgs.</summary>
    HyperVOperation,
    /// <summary>Console: current screen via Msvm_VirtualSystemManagementService.GetVirtualSystemThumbnailImage.</summary>
    ConsoleFrame,
    /// <summary>Console: keyboard (Msvm_Keyboard) / mouse (Msvm_SyntheticMouse) input.</summary>
    ConsoleInput,
    /// <summary>
    /// Protocol 9. Download the agent package from the gateway (agent port, mutual TLS), check its SHA-256 and hand it to
    /// the updater task of the machine, which checks the publisher signature of the package before installing it.
    /// </summary>
    UpdateAgent,
}

/// <summary>Package the agent must download (AgentProtocol.PackagePath) and verify before handing it to the updater.</summary>
public sealed record AgentUpdateRequest
{
    public required string Version { get; init; }
    public required string Sha256 { get; init; }
    public required long SizeBytes { get; init; }
}

public sealed record AgentCommand
{
    public required Guid CommandId { get; init; }
    public required AgentCommandKind Kind { get; init; }
    public Guid? VmId { get; init; }
    public VmPowerOperation? PowerOperation { get; init; }
    /// <summary>Upper bound for the whole operation on the host, including waiting for the end state.</summary>
    public int TimeoutSeconds { get; init; } = 300;
    /// <summary>Short, non-sensitive text passed to the guest as shutdown reason (e.g. job id).</summary>
    public string? Reason { get; init; }
    public HvOperation? Operation { get; init; }
    public JsonElement? OperationArgs { get; init; }
    public ConsoleFrameRequest? FrameRequest { get; init; }
    public ConsoleInput? Input { get; init; }
    /// <summary>UpdateAgent only.</summary>
    public AgentUpdateRequest? Update { get; init; }
    /// <summary>DeleteVm only (protocol 9): also delete the virtual disk files of the VM (see HvOperation.RemoveVmWithDisks).</summary>
    public bool? DeleteDisks { get; init; }
    /// <summary>
    /// Management agents only (protocol 6): the remote host the command is for. Set by the gateway, never by callers;
    /// a host agent rejects commands that carry it, a management agent rejects commands without it.
    /// </summary>
    public Guid? TargetHostId { get; init; }
}

public enum AgentCommandStatus
{
    Succeeded,
    AlreadyInState,
    /// <summary>Hyper-V accepted the request but the expected end state was not observed within the timeout.</summary>
    Unverified,
    Failed,
    InvalidState,
    NotSupported,
    NotFound,
    AccessDenied,
    TimedOut,
    Rejected,
}

public sealed record AgentCommandResult
{
    public required Guid CommandId { get; init; }
    public required AgentCommandStatus Status { get; init; }
    public string? Message { get; init; }
    /// <summary>Raw Hyper-V WMI return/error code, when one was produced.</summary>
    public uint? HyperVCode { get; init; }
    public VmSummary? Vm { get; init; }
    public HostInventory? Inventory { get; init; }
    public VmDetails? Details { get; init; }
    public HvOperationOutcome? Operation { get; init; }
    public ConsoleFrame? Frame { get; init; }
    /// <summary>True when the agent returned a cached result for a command id it already executed.</summary>
    public bool Replayed { get; init; }
}

public sealed record AgentCommandProgress
{
    public required Guid CommandId { get; init; }
    public int Percent { get; init; }
    public string? Message { get; init; }
}

public sealed record RenewCertificateRequest
{
    public required string CsrPem { get; init; }
}

public sealed record RenewCertificateResponse
{
    public required string CertificatePem { get; init; }
}

public sealed record EnrollmentRequest
{
    public required string Token { get; init; }
    public required string CsrPem { get; init; }
    public required string MachineName { get; init; }
    public required string AgentVersion { get; init; }
}

public sealed record EnrollmentResponse
{
    public required Guid HostId { get; init; }
    public required string CertificatePem { get; init; }
    public required string CaCertificatePem { get; init; }
    /// <summary>Protocol 6+. Older gateways omit it (Host).</summary>
    public AgentRole Role { get; init; } = AgentRole.Host;
}

/// <summary>
/// A remote Hyper-V host assigned to a management agent. Sent only over the mutually authenticated agent channel;
/// the agent keeps it in memory (never on disk). ToString never includes the password.
/// </summary>
public sealed class RemoteTarget
{
    public required Guid HostId { get; init; }
    public required string ComputerName { get; init; }
    public required string UserName { get; init; }
    public required string Password { get; init; }
    /// <summary>WinRM over HTTPS (5986) instead of HTTP (5985, NTLM message encryption; needs TrustedHosts in a workgroup).</summary>
    public bool UseHttps { get; init; }
    /// <summary>Protocol 9. Folders of the remote host the portal may use (set in the portal; empty = not restricted).</summary>
    public IReadOnlyList<string> StorageFolders { get; init; } = [];

    public override string ToString() => $"RemoteTarget {HostId:D} {ComputerName}";
}

public sealed record TargetsConfiguration
{
    public IReadOnlyList<RemoteTarget> Targets { get; init; } = [];
}

public sealed record TargetInventory
{
    public required Guid HostId { get; init; }
    public required HostInventory Inventory { get; init; }
}
