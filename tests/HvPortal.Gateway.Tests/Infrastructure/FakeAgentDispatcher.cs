using System.Collections.Concurrent;
using HvPortal.Contracts.Agent;
using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Agents;

namespace HvPortal.Gateway.Tests.Infrastructure;

/// <summary>
/// TEST DOUBLE. Simulates agents in memory so gateway behavior (authorization, jobs, reconciliation) can be tested
/// without Hyper-V. It lives only in the test project and is never registered in production.
/// </summary>
public sealed class FakeAgentDispatcher : IAgentDispatcher
{
    private readonly ConcurrentDictionary<Guid, bool> _connected = new();
    private readonly ConcurrentDictionary<Guid, (Guid HostId, string Name, VmPowerState State, long UptimeMs)> _vms = new();

    public ConcurrentQueue<AgentCommand> Received { get; } = new();

    /// <summary>When set, a VmPower command is "sent" and then the channel drops (outcome unknown to the gateway).</summary>
    public bool DropChannelAfterPowerCommand { get; set; }

    /// <summary>When true, the dropped command was nonetheless executed on the host.</summary>
    public bool ExecuteBeforeDrop { get; set; }

    public void Connect(Guid hostId) => _connected[hostId] = true;
    public void DisconnectHost(Guid hostId) => _connected[hostId] = false;
    public void AddVm(Guid hostId, Guid hyperVId, string name, VmPowerState state) => _vms[hyperVId] = (hostId, name, state, 600_000);
    public VmPowerState StateOf(Guid hyperVId) => _vms[hyperVId].State;
    public void SetState(Guid hyperVId, VmPowerState state) => _vms[hyperVId] = _vms[hyperVId] with { State = state };
    public int PowerCommandsFor(Guid hyperVId) => Received.Count(c => c.Kind == AgentCommandKind.VmPower && c.VmId == hyperVId);

    public bool IsConnected(Guid hostId) => _connected.TryGetValue(hostId, out var c) && c;

    public void Disconnect(Guid hostId, string reason) => _connected[hostId] = false;

    public Task<AgentCommandResult> SendAsync(Guid hostId, AgentCommand command, Action<AgentCommandProgress>? onProgress, TimeSpan timeout, CancellationToken ct)
    {
        if (!IsConnected(hostId))
        {
            throw new HostNotConnectedException(hostId);
        }
        Received.Enqueue(command);
        switch (command.Kind)
        {
            case AgentCommandKind.GetVm:
                return Task.FromResult(_vms.TryGetValue(command.VmId!.Value, out var v)
                    ? new AgentCommandResult { CommandId = command.CommandId, Status = AgentCommandStatus.Succeeded, Vm = Summary(command.VmId.Value, v) }
                    : new AgentCommandResult { CommandId = command.CommandId, Status = AgentCommandStatus.NotFound });

            case AgentCommandKind.VmPower:
                var id = command.VmId!.Value;
                if (!_vms.TryGetValue(id, out var vm) || vm.HostId != hostId)
                {
                    return Task.FromResult(new AgentCommandResult { CommandId = command.CommandId, Status = AgentCommandStatus.NotFound });
                }
                var op = command.PowerOperation!.Value;
                if (DropChannelAfterPowerCommand)
                {
                    if (ExecuteBeforeDrop && VmPowerRules.IsAllowed(op, vm.State))
                    {
                        _vms[id] = vm with { State = VmPowerRules.ExpectedState(op), UptimeMs = 10 };
                    }
                    throw new AgentChannelLostException(hostId);
                }
                if (VmPowerRules.IsAlreadyInTargetState(op, vm.State))
                {
                    return Task.FromResult(new AgentCommandResult { CommandId = command.CommandId, Status = AgentCommandStatus.AlreadyInState, Message = "already" });
                }
                if (!VmPowerRules.IsAllowed(op, vm.State))
                {
                    return Task.FromResult(new AgentCommandResult { CommandId = command.CommandId, Status = AgentCommandStatus.InvalidState });
                }
                onProgress?.Invoke(new AgentCommandProgress { CommandId = command.CommandId, Percent = 50 });
                _vms[id] = vm with { State = VmPowerRules.ExpectedState(op), UptimeMs = 10 };
                return Task.FromResult(new AgentCommandResult
                {
                    CommandId = command.CommandId,
                    Status = AgentCommandStatus.Succeeded,
                    Vm = Summary(id, _vms[id]),
                    HyperVCode = 0,
                });

            default:
                return Task.FromResult(new AgentCommandResult { CommandId = command.CommandId, Status = AgentCommandStatus.Rejected });
        }
    }

    private static VmSummary Summary(Guid id, (Guid HostId, string Name, VmPowerState State, long UptimeMs) v) =>
        new() { VmId = id, Name = v.Name, PowerState = v.State, UptimeMs = v.UptimeMs };
}
