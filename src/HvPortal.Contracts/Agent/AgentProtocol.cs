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
    public const int Version = 1;
    public const int MaxMessageBytes = 4 * 1024 * 1024;
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
}

public enum AgentCommandKind
{
    CollectInventory,
    GetVm,
    VmPower,
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
}
