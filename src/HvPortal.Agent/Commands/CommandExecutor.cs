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
    private readonly PowerShellOperationRunner? _ops;
    private readonly ConsoleService? _console;
    private readonly ILogger<CommandExecutor> _logger;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _vmLocks = new();
    private readonly ConcurrentDictionary<Guid, Lazy<Task<AgentCommandResult>>> _executions = new();
    private readonly ConcurrentQueue<Guid> _order = new();

    public CommandExecutor(IHyperVHost host, ILogger<CommandExecutor> logger, PowerShellOperationRunner? ops = null, ConsoleService? console = null)
    {
        _host = host;
        _logger = logger;
        _ops = ops;
        _console = console;
    }

    /// <summary>Operations that change state on the host; only these are de-duplicated and serialized per VM.</summary>
    public static bool IsMutating(AgentCommand c) => c.Kind switch
    {
        AgentCommandKind.VmPower or AgentCommandKind.DeleteVm => true,
        AgentCommandKind.HyperVOperation => c.Operation is HvOperation.SetVmConfig or HvOperation.NewVm or HvOperation.NewCheckpoint
            or HvOperation.RestoreCheckpoint or HvOperation.RemoveCheckpoint or HvOperation.SyncVmGroups
            or HvOperation.NewSwitch or HvOperation.RemoveSwitch,
        _ => false,
    };

    public async Task<AgentCommandResult> ExecuteAsync(AgentCommand command, IProgress<AgentCommandProgress> progress, CancellationToken ct)
    {
        if (!IsMutating(command))
        {
            // Read-only and console commands: no result cache (frames are large and frequent).
            return await RunAsync(command, progress, ct);
        }
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

                case AgentCommandKind.GetVmDetails when command.VmId is { } detailsId && detailsId != Guid.Empty:
                    var details = await _host.GetVmDetailsAsync(detailsId, ct);
                    return new AgentCommandResult
                    {
                        CommandId = command.CommandId,
                        Status = details is null ? AgentCommandStatus.NotFound : AgentCommandStatus.Succeeded,
                        Details = details,
                    };

                case AgentCommandKind.DeleteVm when command.VmId is { } deleteId && deleteId != Guid.Empty:
                    return await RunDeleteAsync(command, deleteId, progress, ct);

                case AgentCommandKind.HyperVOperation when command.Operation is { } op && Enum.IsDefined(op) && _ops is not null:
                    return await RunOperationAsync(command, op, ct);

                case AgentCommandKind.ConsoleFrame when command.FrameRequest is { } fr && fr.VmId != Guid.Empty && _console is not null:
                    return await _console.FrameAsync(command.CommandId, fr, ct);

                case AgentCommandKind.ConsoleInput when command.Input is { } input && input.VmId != Guid.Empty && Enum.IsDefined(input.Kind) && _console is not null:
                    return await _console.InputAsync(command.CommandId, input, ct);

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

    private async Task<AgentCommandResult> RunOperationAsync(AgentCommand command, HvOperation op, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(command.TimeoutSeconds, 10, (int)MaxTimeout.TotalSeconds));
        SemaphoreSlim? gate = null;
        if (IsMutating(command) && command.VmId is { } vmId && vmId != Guid.Empty)
        {
            gate = _vmLocks.GetOrAdd(vmId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
        }
        try
        {
            if (op is HvOperation.ReadFile or HvOperation.WriteFileChunk)
            {
                _logger.LogDebug("File transfer chunk {Operation} (command {CommandId})", op, command.CommandId);
            }
            else
            {
                _logger.LogInformation("Hyper-V operation {Operation} (command {CommandId})", op, command.CommandId);
            }
            var outcome = await _ops!.RunAsync(op, command.OperationArgs, timeout, ct);
            return new AgentCommandResult
            {
                CommandId = command.CommandId,
                Status = outcome.Ok ? AgentCommandStatus.Succeeded : AgentCommandStatus.Failed,
                Message = outcome.Error,
                Operation = outcome,
            };
        }
        finally
        {
            gate?.Release();
        }
    }

    private async Task<AgentCommandResult> RunDeleteAsync(AgentCommand command, Guid vmId, IProgress<AgentCommandProgress> progress, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(command.TimeoutSeconds, 10, (int)MaxTimeout.TotalSeconds));
        var gate = _vmLocks.GetOrAdd(vmId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            _logger.LogWarning("Deleting VM {VmId} (command {CommandId})", vmId, command.CommandId);
            var p = new Progress<(int Percent, string Message)>(x =>
                progress.Report(new AgentCommandProgress { CommandId = command.CommandId, Percent = x.Percent, Message = x.Message }));
            var outcome = await _host.DeleteVmAsync(vmId, timeout, p, ct);
            _logger.LogWarning("Delete VM {VmId}: {Status} {Message}", vmId, outcome.Status, outcome.Message);
            return new AgentCommandResult { CommandId = command.CommandId, Status = outcome.Status, Message = outcome.Message, HyperVCode = outcome.HyperVCode };
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
