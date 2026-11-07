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

    /// <summary>Host agent: closes its channel. Remote host: stops routing to it (its management agent stays connected).</summary>
    void Disconnect(Guid hostId, string reason);

    // ---- management agents (ADR-022)

    bool IsManagementAgentConnected(Guid agentId);

    /// <summary>
    /// Sends a connected management agent its current remote hosts (with credentials) and updates routing.
    /// Call after any change to the remote hosts of that agent. No-op when the agent is offline (it gets them on connect).
    /// </summary>
    Task ReconfigureManagementAgentAsync(Guid agentId, CancellationToken ct);

    void DisconnectManagementAgent(Guid agentId, string reason);

    /// <summary>Protocol 9: a command for a management agent itself (UpdateAgent).</summary>
    Task<AgentCommandResult> SendToManagementAgentAsync(Guid agentId, AgentCommand command, TimeSpan timeout, CancellationToken ct);

    /// <summary>Protocol and version announced by the connected agent of a host, or by a connected management agent; null when not connected.</summary>
    (int Protocol, string? Version)? AgentOf(Guid hostOrAgentId);
}

public sealed class HostNotConnectedException(Guid hostId) : Exception($"Host {hostId} is not connected.")
{
    public Guid HostId { get; } = hostId;
}

public sealed class AgentChannelLostException(Guid hostId) : Exception($"Channel to host {hostId} was lost after the command was sent.")
{
    public Guid HostId { get; } = hostId;
}
