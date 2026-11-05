using HvPortal.Contracts.Agent;

namespace HvPortal.Gateway.Agents;

/// <summary>Sends typed commands to connected agents.</summary>
public interface IAgentDispatcher
{
    bool IsConnected(Guid hostId);

    /// <summary>Protocol announced by the connected agent (AgentHello), 0 when not connected.</summary>
    int ProtocolVersionOf(Guid hostId);

    /// <exception cref="HostNotConnectedException">Nothing was sent: safe to retry.</exception>
    /// <exception cref="AgentChannelLostException">The command was sent but the channel dropped: outcome unknown.</exception>
    /// <exception cref="TimeoutException">The command was sent but no result arrived in time: outcome unknown.</exception>
    Task<AgentCommandResult> SendAsync(Guid hostId, AgentCommand command, Action<AgentCommandProgress>? onProgress,
        TimeSpan timeout, CancellationToken ct);

    void Disconnect(Guid hostId, string reason);
}

public sealed class HostNotConnectedException(Guid hostId) : Exception($"Host {hostId} is not connected.")
{
    public Guid HostId { get; } = hostId;
}

public sealed class AgentChannelLostException(Guid hostId) : Exception($"Channel to host {hostId} was lost after the command was sent.")
{
    public Guid HostId { get; } = hostId;
}
