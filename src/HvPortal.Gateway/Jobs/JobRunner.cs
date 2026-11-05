using System.Collections.Concurrent;
using HvPortal.Contracts.Agent;
using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Agents;
using HvPortal.Gateway.Audit;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Configuration;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Realtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HvPortal.Gateway.Jobs;

/// <summary>
/// Executes persistent jobs.
///  - Claiming uses a conditional UPDATE (Queued -> Running) plus a lease, so a job runs once per control plane.
///  - A per-resource lock row serializes operations on the same VM.
///  - Authorization and user status are re-validated at execution time (revocation applies to queued jobs).
///  - The owning host is resolved at execution time from current inventory.
///  - "Dispatched" is persisted before sending. If the outcome becomes uncertain (channel lost, timeout,
///    gateway restart after dispatch), the job moves to Reconciling and the outcome is derived from the VM's
///    observed state. Commands with an uncertain outcome are never re-sent (no exactly-once claim).
/// </summary>
public sealed class JobRunner : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IAgentDispatcher _dispatcher;
    private readonly AuditLog _audit;
    private readonly IRealtimeNotifier _notifier;
    private readonly JobSignal _signal;
    private readonly TimeProvider _time;
    private readonly JobOptions _options;
    private readonly ILogger<JobRunner> _logger;
    private readonly string _instanceId = Environment.MachineName + ":" + Guid.NewGuid().ToString("N")[..8];
    private readonly ConcurrentDictionary<Guid, Task> _running = new();

    public JobRunner(IServiceScopeFactory scopes, IAgentDispatcher dispatcher, AuditLog audit, IRealtimeNotifier notifier,
        JobSignal signal, TimeProvider time, IOptions<GatewayOptions> options, ILogger<JobRunner> logger)
    {
        _scopes = scopes;
        _dispatcher = dispatcher;
        _audit = audit;
        _notifier = notifier;
        _signal = signal;
        _time = time;
        _options = options.Value.Jobs;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.RunnerEnabled)
        {
            return;
        }
        await RecoverAfterRestartAsync(stoppingToken);
        var lastReconcile = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessQueueAsync(stoppingToken);
                if (_time.GetUtcNow() - lastReconcile > TimeSpan.FromSeconds(10))
                {
                    lastReconcile = _time.GetUtcNow();
                    await ReconcileAsync(stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Job runner loop failed");
            }
            await _signal.WaitAsync(TimeSpan.FromMilliseconds(Math.Max(200, _options.PollIntervalMilliseconds)), stoppingToken);
        }
        await Task.WhenAll(_running.Values);
    }

    /// <summary>Jobs left Running by a previous process: never-dispatched ones are re-queued, dispatched ones reconciled.</summary>
    internal async Task RecoverAfterRestartAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
        var now = _time.GetUtcNow();
        var stale = await db.Jobs.Where(j => j.Status == JobStatus.Running && j.LeaseOwner != _instanceId).ToListAsync(ct);
        foreach (var job in stale)
        {
            if (job.DispatchedAt is null)
            {
                job.Status = JobStatus.Queued;
                job.LeaseOwner = null;
                job.LeaseExpiresAt = null;
                AddEvent(db, job, "requeued", "Gateway restarted before the command was sent; re-queued.");
            }
            else
            {
                job.Status = JobStatus.Reconciling;
                job.ReconcileStartedAt = now;
                AddEvent(db, job, "reconciling", "Gateway restarted after the command was sent; verifying outcome from host state.");
            }
        }
        await db.SaveChangesAsync(ct);
        if (stale.Count > 0)
        {
            _logger.LogWarning("Recovered {Count} job(s) interrupted by a restart", stale.Count);
        }
    }

    internal async Task ProcessQueueAsync(CancellationToken ct)
    {
        foreach (var done in _running.Where(kv => kv.Value.IsCompleted).Select(kv => kv.Key).ToList())
        {
            _running.TryRemove(done, out _);
        }
        if (_running.Count >= _options.MaxConcurrentJobs)
        {
            return;
        }
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
        var now = _time.GetUtcNow();
        var ids = await db.Jobs.AsNoTracking()
            .Where(j => j.Status == JobStatus.Queued && (j.NotBefore == null || j.NotBefore <= now))
            .OrderBy(j => j.CreatedAt).Select(j => j.Id).Take(20).ToListAsync(ct);
        foreach (var id in ids)
        {
            if (_running.Count >= _options.MaxConcurrentJobs)
            {
                break;
            }
            var lease = now.AddMinutes(2);
            var claimed = await db.Jobs.Where(j => j.Id == id && j.Status == JobStatus.Queued)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(j => j.Status, JobStatus.Running)
                    .SetProperty(j => j.LeaseOwner, _instanceId)
                    .SetProperty(j => j.LeaseExpiresAt, lease)
                    .SetProperty(j => j.HeartbeatAt, now)
                    .SetProperty(j => j.Attempt, j => j.Attempt + 1), ct);
            if (claimed == 1)
            {
                _running[id] = Task.Run(() => RunJobAsync(id, ct), CancellationToken.None);
            }
        }
    }

    /// <summary>For tests: waits for in-flight job executions.</summary>
    internal Task DrainAsync() => Task.WhenAll(_running.Values);

    private async Task RunJobAsync(Guid jobId, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
        var access = scope.ServiceProvider.GetRequiredService<AccessEvaluator>();
        var job = await db.Jobs.FirstAsync(j => j.Id == jobId, ct);
        var lockKey = "vm:" + job.TargetId.ToString("N");
        var keepLock = false;
        try
        {
            job.StartedAt ??= _time.GetUtcNow();
            if (job.CancelRequested)
            {
                await FinalizeAsync(db, job, JobStatus.Cancelled, "Cancelled before dispatch.", "cancelled", null);
                return;
            }
            if (!await TryAcquireLockAsync(db, job, lockKey, ct))
            {
                job.Status = JobStatus.Queued;
                job.LeaseOwner = null;
                job.NotBefore = _time.GetUtcNow().AddSeconds(2);
                job.Attempt--;
                job.ProgressMessage = "Waiting for another operation on the same VM.";
                await db.SaveChangesAsync(ct);
                return;
            }

            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == job.RequestedByUserId, ct);
            if (user is null || !user.IsEnabled || !await access.HasAsync(job.RequestedByUserId, job.RequiredPermission, ResourceRef.Vm(job.TargetId), ct))
            {
                await FinalizeAsync(db, job, JobStatus.Failed, "The requesting user is no longer authorized for this operation.", "authorization_revoked", null,
                    AuditOutcome.Denied);
                return;
            }

            var vm = await db.VirtualMachines.AsNoTracking().FirstOrDefaultAsync(v => v.Id == job.TargetId, ct);
            if (vm is null || vm.IsMissing)
            {
                await FinalizeAsync(db, job, JobStatus.Failed, "The VM is not present in the current inventory.", "vm_not_found", null);
                return;
            }
            job.HostId = vm.HostId; // resolve the owner at execution time

            if (!Enum.TryParse<VmPowerOperation>(job.Operation, out var op))
            {
                await FinalizeAsync(db, job, JobStatus.Failed, "Unknown operation.", "invalid_job", null);
                return;
            }

            if (!_dispatcher.IsConnected(vm.HostId))
            {
                await RetryOrFailUnreachableAsync(db, job, ct);
                return;
            }

            // Persist "dispatched" before sending, conditioned on no cancellation request (atomic with cancel).
            var now = _time.GetUtcNow();
            var marked = await db.Jobs.Where(j => j.Id == job.Id && !j.CancelRequested)
                .ExecuteUpdateAsync(u => u.SetProperty(j => j.DispatchedAt, now).SetProperty(j => j.HostId, vm.HostId), ct);
            if (marked == 0)
            {
                await db.Entry(job).ReloadAsync(ct);
                await FinalizeAsync(db, job, JobStatus.Cancelled, "Cancelled before dispatch.", "cancelled", null);
                return;
            }
            await db.Entry(job).ReloadAsync(ct);
            AddEvent(db, job, "dispatched", $"Sent to host {vm.HostId}.");
            await db.SaveChangesAsync(ct);
            await _notifier.JobChangedAsync(job);

            var command = new AgentCommand
            {
                // The job id is the command id: the agent deduplicates re-deliveries of the same id.
                CommandId = job.Id,
                Kind = AgentCommandKind.VmPower,
                VmId = vm.HyperVId,
                PowerOperation = op,
                TimeoutSeconds = job.TimeoutSeconds,
                Reason = "HvPortal job " + job.Id.ToString("N"),
            };

            using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var heartbeat = HeartbeatAsync(job.Id, heartbeatCts.Token);
            AgentCommandResult result;
            try
            {
                result = await _dispatcher.SendAsync(vm.HostId, command, p => _ = ReportProgressAsync(job.Id, p),
                    TimeSpan.FromSeconds(job.TimeoutSeconds + 30), ct);
            }
            finally
            {
                await heartbeatCts.CancelAsync();
                await heartbeat;
            }
            await db.Entry(job).ReloadAsync(ct);
            var (status, code) = Map(result.Status);
            var message = result.Status == AgentCommandStatus.AlreadyInState ? "No change needed: " + result.Message : result.Message;
            await FinalizeAsync(db, job, status, message, code, result.HyperVCode);
        }
        catch (HostNotConnectedException)
        {
            await db.Jobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(u => u.SetProperty(j => j.DispatchedAt, (DateTimeOffset?)null), CancellationToken.None);
            await db.Entry(job).ReloadAsync(CancellationToken.None);
            await RetryOrFailUnreachableAsync(db, job, CancellationToken.None);
        }
        catch (Exception ex) when (ex is AgentChannelLostException or TimeoutException)
        {
            keepLock = true;
            await db.Entry(job).ReloadAsync(CancellationToken.None);
            job.Status = JobStatus.Reconciling;
            job.ReconcileStartedAt = _time.GetUtcNow();
            AddEvent(db, job, "reconciling", ex.Message + " Verifying outcome from host state.");
            await db.SaveChangesAsync(CancellationToken.None);
            await _notifier.JobChangedAsync(job);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Gateway stopping: RecoverAfterRestartAsync handles it on next start.
            keepLock = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {JobId} failed unexpectedly", jobId);
            await FinalizeAsync(db, job, JobStatus.Failed, "Internal error: " + ex.GetType().Name, "internal_error", null);
        }
        finally
        {
            if (!keepLock)
            {
                await db.ResourceLocks.Where(l => l.ResourceKey == lockKey && l.JobId == jobId).ExecuteDeleteAsync(CancellationToken.None);
            }
        }
    }

    internal async Task ReconcileAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
        var jobs = await db.Jobs.Where(j => j.Status == JobStatus.Reconciling).Take(50).ToListAsync(ct);
        foreach (var job in jobs)
        {
            var now = _time.GetUtcNow();
            var giveUp = now - (job.ReconcileStartedAt ?? now) > TimeSpan.FromMinutes(_options.ReconciliationGiveUpMinutes);
            var vm = await db.VirtualMachines.AsNoTracking().FirstOrDefaultAsync(v => v.Id == job.TargetId, ct);
            if (vm is null || job.HostId is not { } hostId || !Enum.TryParse<VmPowerOperation>(job.Operation, out var op))
            {
                await FinalizeAsync(db, job, JobStatus.Unknown, "Outcome cannot be determined: VM no longer in inventory.", "outcome_unknown", null);
                await ReleaseLockAsync(db, job);
                continue;
            }
            if (!_dispatcher.IsConnected(hostId))
            {
                if (giveUp)
                {
                    await FinalizeAsync(db, job, JobStatus.Unknown, "Host unreachable; the outcome could not be verified. Check the VM on the host.", "outcome_unknown", null);
                    await ReleaseLockAsync(db, job);
                }
                continue;
            }
            AgentCommandResult probe;
            try
            {
                probe = await _dispatcher.SendAsync(hostId, new AgentCommand { CommandId = Guid.NewGuid(), Kind = AgentCommandKind.GetVm, VmId = vm.HyperVId },
                    null, TimeSpan.FromSeconds(60), ct);
            }
            catch (Exception ex) when (ex is HostNotConnectedException or AgentChannelLostException or TimeoutException)
            {
                continue;
            }
            var state = probe.Vm?.PowerState;
            var expected = VmPowerRules.ExpectedState(op);
            var sinceDispatchMs = job.DispatchedAt is { } d ? (now - d).TotalMilliseconds : double.MaxValue;
            var verified = state == expected && (!VmPowerRules.RequiresUptimeProof(op) || probe.Vm?.UptimeMs < sinceDispatchMs);
            if (verified)
            {
                await FinalizeAsync(db, job, JobStatus.Succeeded, $"Outcome verified by reconciliation: VM is {state}.", null, null);
                await ReleaseLockAsync(db, job);
            }
            else if (giveUp || state is VmPowerState.Running or VmPowerState.Off or VmPowerState.Saved or VmPowerState.Paused)
            {
                await FinalizeAsync(db, job, JobStatus.Unknown,
                    $"The VM is {state?.ToString() ?? "not found"}; whether the operation ran cannot be confirmed. Check the host before retrying.",
                    "outcome_unknown", null);
                await ReleaseLockAsync(db, job);
            }
            // otherwise transitional state: check again next round
        }
    }

    private async Task RetryOrFailUnreachableAsync(PortalDbContext db, Job job, CancellationToken ct)
    {
        if (job.Attempt < _options.HostUnreachableRetries)
        {
            job.Status = JobStatus.Queued;
            job.LeaseOwner = null;
            job.NotBefore = _time.GetUtcNow().AddSeconds(15 * job.Attempt);
            job.ProgressMessage = $"Host not connected; retry {job.Attempt}/{_options.HostUnreachableRetries}.";
            AddEvent(db, job, "retry", job.ProgressMessage);
            await db.SaveChangesAsync(ct);
            await _notifier.JobChangedAsync(job);
        }
        else
        {
            await FinalizeAsync(db, job, JobStatus.Failed, "The host agent is not connected. The command was not sent.", "host_unreachable", null);
        }
    }

    private async Task<bool> TryAcquireLockAsync(PortalDbContext db, Job job, string key, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        await db.ResourceLocks.Where(l => l.ResourceKey == key && l.ExpiresAt < now).ExecuteDeleteAsync(ct);
        var existing = await db.ResourceLocks.AsNoTracking().FirstOrDefaultAsync(l => l.ResourceKey == key, ct);
        if (existing is not null)
        {
            return existing.JobId == job.Id;
        }
        var row = new ResourceLock { ResourceKey = key, JobId = job.Id, AcquiredAt = now, ExpiresAt = now.AddSeconds(job.TimeoutSeconds + 300) };
        db.ResourceLocks.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            db.Entry(row).State = EntityState.Detached;
            return false;
        }
    }

    private static async Task ReleaseLockAsync(PortalDbContext db, Job job) =>
        await db.ResourceLocks.Where(l => l.JobId == job.Id).ExecuteDeleteAsync();

    private async Task HeartbeatAsync(Guid jobId, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), _time, ct);
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
                var now = _time.GetUtcNow();
                await db.Jobs.Where(j => j.Id == jobId && j.Status == JobStatus.Running)
                    .ExecuteUpdateAsync(u => u.SetProperty(j => j.HeartbeatAt, now).SetProperty(j => j.LeaseExpiresAt, now.AddMinutes(2)), ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ReportProgressAsync(Guid jobId, AgentCommandProgress p)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
            var pct = Math.Clamp(p.Percent, 0, 99);
            var msg = p.Message is { Length: > 200 } m ? m[..200] : p.Message;
            await db.Jobs.Where(j => j.Id == jobId && j.Status == JobStatus.Running)
                .ExecuteUpdateAsync(u => u.SetProperty(j => j.Progress, pct).SetProperty(j => j.ProgressMessage, msg));
            var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId);
            if (job is not null)
            {
                await _notifier.JobChangedAsync(job);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Progress update failed for job {JobId}", jobId);
        }
    }

    private async Task FinalizeAsync(PortalDbContext db, Job job, JobStatus status, string? message, string? errorCode, uint? hyperVCode,
        AuditOutcome? auditOutcome = null)
    {
        var now = _time.GetUtcNow();
        job.Status = status;
        job.CompletedAt = now;
        job.ResultMessage = message is { Length: > 2000 } ? message[..2000] : message;
        job.ErrorCode = errorCode;
        job.HyperVCode = hyperVCode;
        job.LeaseOwner = null;
        if (status == JobStatus.Succeeded)
        {
            job.Progress = 100;
        }
        AddEvent(db, job, status.ToString().ToLowerInvariant(), message);
        await db.SaveChangesAsync(CancellationToken.None);

        var user = await db.Users.AsNoTracking().Where(u => u.Id == job.RequestedByUserId).Select(u => u.UserName).FirstOrDefaultAsync();
        await _audit.WriteAsync(new AuditRecord
        {
            Action = $"{job.Type}.{job.Operation.ToLowerInvariant()}",
            Outcome = auditOutcome ?? (status == JobStatus.Succeeded ? AuditOutcome.Success : AuditOutcome.Failure),
            ActorUserId = job.RequestedByUserId,
            ActorName = user,
            AuthMethod = "job",
            ResourceType = "vm",
            ResourceId = job.TargetId.ToString("D"),
            CorrelationId = job.CorrelationId,
            Details = new { jobId = job.Id, status = status.ToString(), errorCode, hyperVCode },
        });
        await _notifier.JobChangedAsync(job);
    }

    private static (JobStatus, string?) Map(AgentCommandStatus s) => s switch
    {
        AgentCommandStatus.Succeeded or AgentCommandStatus.AlreadyInState => (JobStatus.Succeeded, null),
        AgentCommandStatus.Unverified or AgentCommandStatus.TimedOut => (JobStatus.Unverified, "end_state_not_observed"),
        _ => (JobStatus.Failed, s.ToString().ToLowerInvariant()),
    };

    private void AddEvent(PortalDbContext db, Job job, string kind, string? message) =>
        db.JobEvents.Add(new JobEvent { JobId = job.Id, At = _time.GetUtcNow(), Kind = kind, Message = message is { Length: > 1000 } ? message[..1000] : message });
}
