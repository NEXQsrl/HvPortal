using System.Text.Json;
using System.Threading.Channels;
using HvPortal.Contracts.Agent;
using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Data;
using Microsoft.EntityFrameworkCore;

namespace HvPortal.Gateway.Jobs;

/// <summary>Wakes the job runner when a job is queued.</summary>
public sealed class JobSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    public void Notify() => _channel.Writer.TryWrite(true);
    public async Task WaitAsync(TimeSpan max, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(max);
        try
        {
            await _channel.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
    }
}

public sealed class IdempotencyConflictException() : Exception("The idempotency key was already used for a different operation.");

public sealed class JobService
{
    private readonly PortalDbContext _db;
    private readonly TimeProvider _time;
    private readonly JobSignal _signal;

    public JobService(PortalDbContext db, TimeProvider time, JobSignal signal)
    {
        _db = db;
        _time = time;
        _signal = signal;
    }

    public const string PowerJobType = "vm.power";
    public const string DeleteJobType = "vm.delete";
    public const string ConfigJobType = "vm.config";
    public const string CreateJobType = "vm.create";
    public const string CheckpointJobType = "vm.checkpoint";

    /// <summary>Typed Hyper-V operation executed by a job of the given type/operation (null for power/delete jobs).</summary>
    public static HvOperation? HvOperationFor(string type, string operation) => (type, operation) switch
    {
        (ConfigJobType, "Update") => HvOperation.SetVmConfig,
        (CreateJobType, "New") => HvOperation.NewVm,
        (CheckpointJobType, "Create") => HvOperation.NewCheckpoint,
        (CheckpointJobType, "Apply") => HvOperation.RestoreCheckpoint,
        (CheckpointJobType, "Delete") => HvOperation.RemoveCheckpoint,
        _ => null,
    };

    /// <summary>
    /// Stored in Job.ParametersJson: the operation arguments (sent to the agent after re-stamping server-controlled
    /// fields at execution time) and gateway-only metadata.
    /// </summary>
    public sealed record OperationParameters(JsonElement Args, Guid? GroupId = null, IReadOnlyList<string>? Permissions = null);

    public sealed record OperationJobSpec(string Type, string Operation, ScopeType TargetType, Guid TargetId, string? TargetName, Guid HostId,
        string RequiredPermission, OperationParameters Parameters, int TimeoutSeconds, string Description);

    public async Task<(Job Job, bool Created)> CreateOperationJobAsync(Guid userId, OperationJobSpec spec, string? idempotencyKey, CancellationToken ct)
    {
        if (HvOperationFor(spec.Type, spec.Operation) is null)
        {
            throw new ArgumentException("Unknown operation job.", nameof(spec));
        }
        if (idempotencyKey is not null)
        {
            var existing = await _db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.RequestedByUserId == userId && j.IdempotencyKey == idempotencyKey, ct);
            if (existing is not null)
            {
                return existing.TargetId == spec.TargetId && existing.Type == spec.Type && existing.Operation == spec.Operation
                    ? (existing, false)
                    : throw new IdempotencyConflictException();
            }
        }
        var parameters = JsonSerializer.Serialize(spec.Parameters, AgentProtocol.Json);
        if (parameters.Length > 16000)
        {
            throw new ArgumentException("Operation parameters too large.", nameof(spec));
        }
        var now = _time.GetUtcNow();
        var job = new Job
        {
            Type = spec.Type,
            Operation = spec.Operation,
            TargetType = spec.TargetType,
            TargetId = spec.TargetId,
            TargetName = spec.TargetName,
            HostId = spec.HostId,
            RequestedByUserId = userId,
            RequiredPermission = spec.RequiredPermission,
            Status = JobStatus.Queued,
            CreatedAt = now,
            TimeoutSeconds = spec.TimeoutSeconds,
            IdempotencyKey = idempotencyKey,
            ParametersJson = parameters,
        };
        _db.Jobs.Add(job);
        _db.JobEvents.Add(new JobEvent { JobId = job.Id, At = now, Kind = "created", Message = spec.Description.Length > 1000 ? spec.Description[..1000] : spec.Description });
        await _db.SaveChangesAsync(ct);
        _signal.Notify();
        return (job, true);
    }

    /// <summary>Deletes the VM configuration on Hyper-V (DestroySystem). Disk files are kept. VM must be Off or Saved.</summary>
    public async Task<(Job Job, bool Created)> CreateVmDeleteJobAsync(Guid userId, VirtualMachine vm, string? idempotencyKey, CancellationToken ct)
    {
        if (idempotencyKey is not null)
        {
            var existing = await _db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.RequestedByUserId == userId && j.IdempotencyKey == idempotencyKey, ct);
            if (existing is not null)
            {
                return existing.TargetId == vm.Id && existing.Type == DeleteJobType ? (existing, false) : throw new IdempotencyConflictException();
            }
        }
        var now = _time.GetUtcNow();
        var job = new Job
        {
            Type = DeleteJobType,
            Operation = "Delete",
            TargetType = ScopeType.Vm,
            TargetId = vm.Id,
            TargetName = vm.Name,
            HostId = vm.HostId,
            RequestedByUserId = userId,
            RequiredPermission = Permissions.VmDelete,
            Status = JobStatus.Queued,
            CreatedAt = now,
            TimeoutSeconds = 300,
            IdempotencyKey = idempotencyKey,
        };
        _db.Jobs.Add(job);
        _db.JobEvents.Add(new JobEvent { JobId = job.Id, At = now, Kind = "created", Message = $"Delete requested for {vm.Name}" });
        await _db.SaveChangesAsync(ct);
        _signal.Notify();
        return (job, true);
    }

    public static string PermissionFor(VmPowerOperation op) =>
        VmPowerRules.IsDestructive(op) ? Permissions.VmPowerForce : Permissions.VmPower;

    public static int TimeoutFor(VmPowerOperation op) => op switch
    {
        VmPowerOperation.Shutdown => 600,
        VmPowerOperation.Restart => 600,
        VmPowerOperation.Save => 600,
        _ => 180,
    };

    public async Task<(Job Job, bool Created)> CreateVmPowerJobAsync(Guid userId, VirtualMachine vm, VmPowerOperation op, string? idempotencyKey, CancellationToken ct)
    {
        if (idempotencyKey is not null)
        {
            var existing = await _db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.RequestedByUserId == userId && j.IdempotencyKey == idempotencyKey, ct);
            if (existing is not null)
            {
                return existing.TargetId == vm.Id && existing.Operation == op.ToString() ? (existing, false) : throw new IdempotencyConflictException();
            }
        }
        var now = _time.GetUtcNow();
        var job = new Job
        {
            Type = PowerJobType,
            Operation = op.ToString(),
            TargetType = ScopeType.Vm,
            TargetId = vm.Id,
            TargetName = vm.Name,
            HostId = vm.HostId,
            RequestedByUserId = userId,
            RequiredPermission = PermissionFor(op),
            Status = JobStatus.Queued,
            CreatedAt = now,
            TimeoutSeconds = TimeoutFor(op),
            IdempotencyKey = idempotencyKey,
        };
        _db.Jobs.Add(job);
        _db.JobEvents.Add(new JobEvent { JobId = job.Id, At = now, Kind = "created", Message = $"{op} requested on {vm.Name}" });
        await _db.SaveChangesAsync(ct);
        _signal.Notify();
        return (job, true);
    }

    /// <summary>
    /// Cancels a job only where it is safe: queued jobs, or running jobs not yet dispatched to the host.
    /// Once dispatched, a power operation cannot be interrupted safely and cancellation is refused.
    /// </summary>
    public async Task<CancelResult> CancelAsync(Guid jobId, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var n = await _db.Jobs.Where(j => j.Id == jobId && j.Status == JobStatus.Queued)
            .ExecuteUpdateAsync(u => u.SetProperty(j => j.Status, JobStatus.Cancelled).SetProperty(j => j.CompletedAt, now)
                .SetProperty(j => j.ResultMessage, "Cancelled before execution."), ct);
        if (n == 1)
        {
            _db.JobEvents.Add(new JobEvent { JobId = jobId, At = now, Kind = "cancelled", Message = "Cancelled while queued." });
            await _db.SaveChangesAsync(ct);
            return CancelResult.Cancelled;
        }
        n = await _db.Jobs.Where(j => j.Id == jobId && j.Status == JobStatus.Running && j.DispatchedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(j => j.CancelRequested, true), ct);
        if (n == 1)
        {
            return CancelResult.CancelRequested;
        }
        var status = await _db.Jobs.AsNoTracking().Where(j => j.Id == jobId).Select(j => (JobStatus?)j.Status).FirstOrDefaultAsync(ct);
        return status is JobStatus.Running or JobStatus.Reconciling ? CancelResult.NotInterruptible : CancelResult.AlreadyFinished;
    }
}

public enum CancelResult
{
    Cancelled,
    CancelRequested,
    NotInterruptible,
    AlreadyFinished,
}
