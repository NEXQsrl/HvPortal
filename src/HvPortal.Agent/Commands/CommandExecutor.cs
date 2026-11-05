using System.Collections.Concurrent;
using HvPortal.Agent.HyperV;
using HvPortal.Contracts.Agent;
using HvPortal.Contracts.HyperV;

namespace HvPortal.Agent.Commands;

/// <summary>
/// Executes the closed set of typed commands. Guarantees:
///  - one operation at a time per VM (per-VM semaphore);
///  - a command id is executed at most once per agent process lifetime: a re-delivered id returns the cached
///    result (or joins the in-flight execution) instead of executing again;
///  - input is validated even though the gateway already validated it (defense in depth).
/// </summary>
public sealed class CommandExecutor
{
    private const int MaxCachedResults = 1000;
    private static readonly TimeSpan MaxTimeout = TimeSpan.FromMinutes(30);

    private readonly IHyperVHost _host;
    private readonly ILogger<CommandExecutor> _logger;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _vmLocks = new();
    private readonly ConcurrentDictionary<Guid, Lazy<Task<AgentCommandResult>>> _executions = new();
    private readonly ConcurrentQueue<Guid> _order = new();

    public CommandExecutor(IHyperVHost host, ILogger<CommandExecutor> logger)
    {
        _host = host;
        _logger = logger;
    }

    public async Task<AgentCommandResult> ExecuteAsync(AgentCommand command, IProgress<AgentCommandProgress> progress, CancellationToken ct)
    {
        var created = false;
        var lazy = _executions.GetOrAdd(command.CommandId, _ =>
        {
            created = true;
            return new Lazy<Task<AgentCommandResult>>(() => RunAsync(command, progress, ct));
        });
        if (created)
        {
            _order.Enqueue(command.CommandId);
            Trim();
        }
        var result = await lazy.Value;
        return created ? result : result with { Replayed = true };
    }

    private void Trim()
    {
        while (_order.Count > MaxCachedResults && _order.TryDequeue(out var old))
        {
            if (_executions.TryGetValue(old, out var l) && l.IsValueCreated && l.Value.IsCompleted)
            {
                _executions.TryRemove(old, out _);
            }
        }
    }

    private async Task<AgentCommandResult> RunAsync(AgentCommand command, IProgress<AgentCommandProgress> progress, CancellationToken ct)
    {
        try
        {
            switch (command.Kind)
            {
                case AgentCommandKind.CollectInventory:
                    return new AgentCommandResult
                    {
                        CommandId = command.CommandId,
                        Status = AgentCommandStatus.Succeeded,
                        Inventory = await _host.CollectInventoryAsync(ct),
                    };

                case AgentCommandKind.GetVm when command.VmId is { } id && id != Guid.Empty:
                    var vm = await _host.GetVmAsync(id, ct);
                    return new AgentCommandResult
                    {
                        CommandId = command.CommandId,
                        Status = vm is null ? AgentCommandStatus.NotFound : AgentCommandStatus.Succeeded,
                        Vm = vm,
                    };

                case AgentCommandKind.VmPower when command.VmId is { } vmId && vmId != Guid.Empty
                                                   && command.PowerOperation is { } op && Enum.IsDefined(op):
                    return await RunPowerAsync(command, vmId, op, progress, ct);

                default:
                    return Reject(command, "Malformed or unsupported command.");
            }
        }
        catch (OperationCanceledException)
        {
            return new AgentCommandResult { CommandId = command.CommandId, Status = AgentCommandStatus.Failed, Message = "Cancelled by agent shutdown." };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Command {CommandId} ({Kind}) failed", command.CommandId, command.Kind);
            return new AgentCommandResult { CommandId = command.CommandId, Status = AgentCommandStatus.Failed, Message = ex.GetType().Name + ": " + ex.Message };
        }
    }

    private async Task<AgentCommandResult> RunPowerAsync(AgentCommand command, Guid vmId, VmPowerOperation op,
        IProgress<AgentCommandProgress> progress, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(command.TimeoutSeconds, 10, (int)MaxTimeout.TotalSeconds));
        var reason = SanitizeReason(command.Reason);
        var gate = _vmLocks.GetOrAdd(vmId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            _logger.LogInformation("Executing {Operation} on VM {VmId} (command {CommandId})", op, vmId, command.CommandId);
            var p = new Progress<(int Percent, string Message)>(x =>
                progress.Report(new AgentCommandProgress { CommandId = command.CommandId, Percent = x.Percent, Message = x.Message }));
            var outcome = await _host.ExecutePowerAsync(vmId, op, reason, timeout, p, ct);
            _logger.LogInformation("{Operation} on VM {VmId}: {Status} {Message}", op, vmId, outcome.Status, outcome.Message);
            return new AgentCommandResult
            {
                CommandId = command.CommandId,
                Status = outcome.Status,
                Message = outcome.Message,
                HyperVCode = outcome.HyperVCode,
                Vm = outcome.Vm,
            };
        }
        finally
        {
            gate.Release();
        }
    }

    internal static string SanitizeReason(string? reason)
    {
        var text = string.IsNullOrWhiteSpace(reason) ? "HvPortal" : reason;
        var clean = new string(text.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or ':' or '.' or '_').Take(120).ToArray());
        return clean.Length == 0 ? "HvPortal" : clean;
    }

    private static AgentCommandResult Reject(AgentCommand c, string message) =>
        new() { CommandId = c.CommandId, Status = AgentCommandStatus.Rejected, Message = message };
}
