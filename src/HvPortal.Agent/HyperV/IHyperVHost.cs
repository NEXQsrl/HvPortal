using HvPortal.Contracts.Agent;
using HvPortal.Contracts.HyperV;

namespace HvPortal.Agent.HyperV;

/// <summary>Abstraction over the local Hyper-V management API. Production implementation: <see cref="WmiHyperVHost"/>.</summary>
public interface IHyperVHost
{
    Task<HostInventory> CollectInventoryAsync(CancellationToken ct);

    Task<VmSummary?> GetVmAsync(Guid vmId, CancellationToken ct);

    Task<VmDetails?> GetVmDetailsAsync(Guid vmId, CancellationToken ct);

    Task<PowerOutcome> ExecutePowerAsync(Guid vmId, VmPowerOperation operation, string reason, TimeSpan timeout,
        IProgress<(int Percent, string Message)> progress, CancellationToken ct);

    Task<PowerOutcome> DeleteVmAsync(Guid vmId, TimeSpan timeout, IProgress<(int Percent, string Message)> progress, CancellationToken ct);
}

public sealed record PowerOutcome(AgentCommandStatus Status, string Message, uint? HyperVCode, VmSummary? Vm);
