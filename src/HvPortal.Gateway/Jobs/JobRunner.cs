using System.Collections.Concurrent;
using System.Text.Json;
using HvPortal.Contracts.Agent;
using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Agents;
using HvPortal.Gateway.Audit;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Configuration;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Quotas;
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

    /// <summary>VM operations are serialized per VM; VM creation is serialized per host (name uniqueness, disk file names).</summary>
    internal static string LockKey(Job job) => job.TargetType == ScopeType.Host
        ? "host-create:" + job.TargetId.ToString("N")
        : "vm:" + job.TargetId.ToString("N");

    private static JobService.OperationParameters? ReadParameters(Job job)
    {
        if (job.ParametersJson is null)
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<JobService.OperationParameters>(job.ParametersJson, AgentProtocol.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Rebuilds the agent arguments at execution time: server-controlled fields (Hyper-V VM id, ISO library roots)
    /// are re-stamped from current data and the whole request is validated again. Null = no longer valid.
    /// </summary>
    internal static JsonElement? PrepareArguments(HvOperation op, JsonElement args, VirtualMachine? vm, ManagedHost host)
    {
        var roots = OperationPolicy.IsoRoots(host);
        switch (op)
        {
            case HvOperation.SetVmConfig when vm is not null && HvOperations.FromElement<VmConfigChange>(args) is { } c:
                var change = c with { VmId = vm.HyperVId, IsoRoots = roots };
                if (change.DvdIsoPath is { Length: > 0 } iso && !OperationPolicy.IsInsideLibrary(iso, roots))
                {
                    return null;
                }
                return OperationValidation.Validate(change) is null ? HvOperations.ToElement(change) : null;
            case HvOperation.NewVm when HvOperations.FromElement<NewVmRequest>(args) is { } n:
                // Template fields are valid only as stamped from the template record (StampTemplateAsync, just before).
                var create = (n.TemplateId is null ? Templates.TemplateRules.WithoutTemplate(n) : n) with { IsoRoots = roots };
                if (create.IsoPath is { Length: > 0 } iso2 && !OperationPolicy.IsInsideLibrary(iso2, roots))
                {
                    return null;
                }
                return OperationValidation.Validate(create) is null ? HvOperations.ToElement(create) : null;
            case HvOperation.NewCheckpoint or HvOperation.RestoreCheckpoint or HvOperation.RemoveCheckpoint
                when vm is not null && HvOperations.FromElement<CheckpointRequest>(args) is { } cp:
                return HvOperations.ToElement(cp with { VmId = vm.HyperVId });
            case HvOperation.NewSwitch when HvOperations.FromElement<NewSwitchRequest>(args) is { } ns:
                return OperationValidation.Validate(ns) is null ? HvOperations.ToElement(ns) : null;
            case HvOperation.RemoveSwitch when HvOperations.FromElement<SwitchRef>(args) is { } rs:
                return OperationValidation.Validate(rs) is null ? HvOperations.ToElement(rs) : null;
            case HvOperation.NewTemplate when vm is not null && HvOperations.FromElement<NewTemplateRequest>(args) is { } nt:
                var capture = nt with { VmId = vm.HyperVId };
                return OperationValidation.Validate(capture) is null ? HvOperations.ToElement(capture) : null;
            case HvOperation.RemoveTemplate when HvOperations.FromElement<TemplateFolderRequest>(args) is { } rt:
                return OperationValidation.Validate(rt) is null ? HvOperations.ToElement(rt) : null;
            default:
                return null;
        }
    }

    private async Task RunJobAsync(Guid jobId, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
        var access = scope.ServiceProvider.GetRequiredService<AccessEvaluator>();
        var job = await db.Jobs.FirstAsync(j => j.Id == jobId, ct);
        var lockKey = LockKey(job);
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
                job.ProgressMessage = job.TargetType == ScopeType.Host
                    ? "Waiting for another VM creation on the same host."
                    : "Waiting for another operation on the same VM.";
                await db.SaveChangesAsync(ct);
                return;
            }

            var hvOperation = JobService.HvOperationFor(job.Type, job.Operation);
            var parameters = hvOperation is null ? null : ReadParameters(job);
            if (hvOperation is not null && parameters is null)
            {
                await FinalizeAsync(db, job, JobStatus.Failed, "Invalid job parameters.", "invalid_job", null);
                return;
            }

            // Re-validate authorization at execution time: user enabled, every permission the operation needs.
            var resource = job.TargetType == ScopeType.Host ? ResourceRef.Host(job.TargetId) : ResourceRef.Vm(job.TargetId);
            var required = new List<string> { job.RequiredPermission };
            required.AddRange(parameters?.Permissions ?? []);
            if (hvOperation == HvOperation.SetVmConfig && HvOperations.FromElement<VmConfigChange>(parameters!.Args) is { } requested)
            {
                required.AddRange(OperationPolicy.PermissionsFor(requested));
            }
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == job.RequestedByUserId, ct);
            var policies = scope.ServiceProvider.GetRequiredService<GroupPolicyService>();
            var authorized = user is { IsEnabled: true } && await access.HoldsAllAsync(job.RequestedByUserId, required.Distinct(), resource, ct);
            if (!authorized && user is { IsEnabled: true } && job.TargetType == ScopeType.Host && parameters?.GroupId is { } viaGroup)
            {
                // Created inside a folder by one of its users: the permission is held on the folder, whose limits open this host (ADR-024).
                authorized = await policies.HoldsViaGroupAsync(job.RequestedByUserId, job.RequiredPermission, viaGroup, job.TargetId, ct);
            }
            if (!authorized)
            {
                await FinalizeAsync(db, job, JobStatus.Failed, "The requesting user is no longer authorized for this operation.", "authorization_revoked", null,
                    AuditOutcome.Denied);
                return;
            }

            VirtualMachine? vm = null;
            ManagedHost? host;
            if (job.TargetType == ScopeType.Host)
            {
                host = await db.Hosts.AsNoTracking().FirstOrDefaultAsync(h => h.Id == job.TargetId, ct);
                if (host is null || host.Status != HostStatus.Active)
                {
                    await FinalizeAsync(db, job, JobStatus.Failed, "The host is not enrolled.", "host_not_found", null);
                    return;
                }
            }
            else
            {
                vm = await db.VirtualMachines.AsNoTracking().FirstOrDefaultAsync(v => v.Id == job.TargetId, ct);
                if (vm is null || vm.IsMissing)
                {
                    await FinalizeAsync(db, job, JobStatus.Failed, "The VM is not present in the current inventory.", "vm_not_found", null);
                    return;
                }
                var vmHostId = vm.HostId;
                host = await db.Hosts.AsNoTracking().FirstOrDefaultAsync(h => h.Id == vmHostId, ct);
                if (host is null)
                {
                    await FinalizeAsync(db, job, JobStatus.Failed, "The VM's host is no longer managed.", "host_not_found", null);
                    return;
                }
            }
            var hostId = host.Id;
            job.HostId = hostId; // resolve the owner at execution time

            // A template is created by who manages templates on the HOST of the VM (the permission has no meaning on folders).
            if (hvOperation == HvOperation.NewTemplate
                && !await access.HasAsync(job.RequestedByUserId, Permissions.TemplatesManage, ResourceRef.Host(hostId), ct))
            {
                await FinalizeAsync(db, job, JobStatus.Failed, "The requesting user is no longer authorized for this operation.", "authorization_revoked", null,
                    AuditOutcome.Denied);
                return;
            }
            // Template jobs: folder, library and disk files are taken from the template record now, whatever the job stored.
            if (hvOperation is { } templateOp && parameters is not null)
            {
                var (templateError, withTemplate) = await StampTemplateAsync(db, templateOp, job, parameters.Args, hostId, ct);
                if (templateError is not null)
                {
                    await FinalizeAsync(db, job, JobStatus.Failed, templateError, "template_not_available", null);
                    return;
                }
                parameters = parameters with { Args = withTemplate };
            }

            // Limits of the VM folders are checked again now (other operations may have used the quota meanwhile);
            // server-controlled fields decided by the limits (VM location, new disk folder) are re-stamped.
            if (hvOperation is { } limited && parameters is not null)
            {
                var (violation, stamped) = await ApplyFolderLimitsAsync(policies, limited, parameters, vm, hostId, job.Id, ct);
                if (violation is not null)
                {
                    await FinalizeAsync(db, job, JobStatus.Failed, violation.Message, violation.Code, null, AuditOutcome.Denied);
                    return;
                }
                parameters = parameters with { Args = stamped };
            }

            AgentCommand command;
            if (job.Type is JobService.PowerJobType or JobService.DeleteJobType)
            {
                var isDeleteJob = job.Type == JobService.DeleteJobType;
                var powerOp = default(VmPowerOperation);
                if (!isDeleteJob && !Enum.TryParse(job.Operation, out powerOp))
                {
                    await FinalizeAsync(db, job, JobStatus.Failed, "Unknown operation.", "invalid_job", null);
                    return;
                }
                command = new AgentCommand
                {
                    // The job id is the command id: the agent deduplicates re-deliveries of the same id.
                    CommandId = job.Id,
                    Kind = isDeleteJob ? AgentCommandKind.DeleteVm : AgentCommandKind.VmPower,
                    VmId = vm!.HyperVId,
                    PowerOperation = isDeleteJob ? null : powerOp,
                    TimeoutSeconds = job.TimeoutSeconds,
                    Reason = "HvPortal job " + job.Id.ToString("N"),
                };
            }
            else if (hvOperation is { } hv && PrepareArguments(hv, parameters!.Args, vm, host) is { } args)
            {
                command = new AgentCommand
                {
                    CommandId = job.Id,
                    Kind = AgentCommandKind.HyperVOperation,
                    Operation = hv,
                    OperationArgs = args,
                    VmId = vm?.HyperVId,
                    TimeoutSeconds = job.TimeoutSeconds,
                    Reason = "HvPortal job " + job.Id.ToString("N"),
                };
            }
            else
            {
                await FinalizeAsync(db, job, JobStatus.Failed,
                    "The operation is no longer valid (for example the ISO is outside the host's current ISO library).", "invalid_job", null);
                return;
            }

            if (!_dispatcher.IsConnected(hostId))
            {
                await RetryOrFailUnreachableAsync(db, job, ct);
                return;
            }

            // Persist "dispatched" before sending, conditioned on no cancellation request (atomic with cancel).
            var now = _time.GetUtcNow();
            var marked = await db.Jobs.Where(j => j.Id == job.Id && !j.CancelRequested)
                .ExecuteUpdateAsync(u => u.SetProperty(j => j.DispatchedAt, now).SetProperty(j => j.HostId, hostId), ct);
            if (marked == 0)
            {
                await db.Entry(job).ReloadAsync(ct);
                await FinalizeAsync(db, job, JobStatus.Cancelled, "Cancelled before dispatch.", "cancelled", null);
                return;
            }
            await db.Entry(job).ReloadAsync(ct);
            AddEvent(db, job, "dispatched", $"Sent to host {host.DisplayName}.");
            await db.SaveChangesAsync(ct);
            await _notifier.JobChangedAsync(job);

            using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var heartbeat = HeartbeatAsync(job.Id, heartbeatCts.Token);
            AgentCommandResult result;
            try
            {
                result = await _dispatcher.SendAsync(hostId, command, p => _ = ReportProgressAsync(job.Id, p),
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
            NewVmResult? created = null;
            if (result.Operation is { } outcome)
            {
                job.ResultJson = outcome.Result is { } r && r.GetRawText() is { Length: <= 16000 } raw ? raw : null;
                message = Describe(outcome);
                if (outcome.Ok && hvOperation == HvOperation.NewVm)
                {
                    created = HvOperations.FromElement<NewVmResult>(outcome.Result);
                    if (created?.Warning is { Length: > 0 } warning)
                    {
                        message = warning;
                    }
                }
            }
            // The new VM joins its folder before the job is reported as finished: until then the job itself counts
            // against the folder quotas, afterwards the VM does.
            if (created is not null && parameters?.GroupId is { } groupId)
            {
                await PlaceCreatedVmAsync(db, job, hostId, created, groupId);
            }
            if (status == JobStatus.Succeeded && job.Type == JobService.NetworkJobType)
            {
                await RecordSwitchOwnershipAsync(db, job, hostId, parameters!, result.Operation?.Result);
            }
            await FinalizeAsync(db, job, status, message, code, result.HyperVCode);
            if (job.Type == JobService.DeleteJobType && status == JobStatus.Succeeded)
            {
                await InventoryCleanup.RemoveVmAsync(db, vm!.Id, vm.HostId, "vm.deleted", _time.GetUtcNow(), CancellationToken.None);
            }
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

    /// <summary>
    /// The template a capture/delete job is about: the one whose folder the job stored when it was requested (the
    /// folder name is the template id). The record is only used to read where the template is; it is never the client's.
    /// </summary>
    private static async Task<VmTemplate?> TemplateOfAsync(PortalDbContext db, Job job, Guid? hostId, CancellationToken ct)
    {
        if (ReadParameters(job) is not { Args: { ValueKind: JsonValueKind.Object } args }
            || !args.TryGetProperty("folder", out var f) || f.ValueKind != JsonValueKind.String || f.GetString() is not { Length: > 0 } folder)
        {
            return null;
        }
        return await db.VmTemplates.FirstOrDefaultAsync(t => t.FolderPath == folder && (hostId == null || t.HostId == hostId), ct);
    }

    private static async Task<(string? Error, JsonElement Args)> StampTemplateAsync(PortalDbContext db, HvOperation op, Job job, JsonElement args, Guid hostId,
        CancellationToken ct)
    {
        switch (op)
        {
            case HvOperation.NewVm when HvOperations.FromElement<NewVmRequest>(args) is { TemplateId: { } templateId } spec:
                var source = await db.VmTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == templateId && t.HostId == hostId, ct);
                if (source is not { Status: TemplateStatus.Ready } || Templates.TemplateRules.Stamp(spec, source) is not { } stamped)
                {
                    return ("The template is no longer available on this host.", args);
                }
                return (null, HvOperations.ToElement(stamped));
            case HvOperation.NewTemplate when HvOperations.FromElement<NewTemplateRequest>(args) is { } capture:
                var creating = await TemplateOfAsync(db, job, hostId, ct);
                return creating is not { Status: TemplateStatus.Creating }
                    ? ("The template record no longer exists.", args)
                    : (null, HvOperations.ToElement(capture with { Folder = creating.FolderPath, Roots = [creating.LibraryPath] }));
            case HvOperation.RemoveTemplate:
                var deleting = await TemplateOfAsync(db, job, hostId, ct);
                return deleting is not { Status: TemplateStatus.Deleting }
                    ? ("The template record no longer exists.", args)
                    : (null, HvOperations.ToElement(new TemplateFolderRequest { Folder = deleting.FolderPath, Roots = [deleting.LibraryPath] }));
            default:
                return (null, args);
        }
    }

    private static async Task<(PolicyViolation? Violation, JsonElement Args)> ApplyFolderLimitsAsync(GroupPolicyService policies, HvOperation op,
        JobService.OperationParameters parameters, VirtualMachine? vm, Guid hostId, Guid jobId, CancellationToken ct)
    {
        switch (op)
        {
            case HvOperation.NewVm when parameters.GroupId is { } g && HvOperations.FromElement<NewVmRequest>(parameters.Args) is { } spec:
                var (v1, stampedSpec) = await policies.CheckNewVmAsync(g, hostId, spec, jobId, ct);
                return (v1, HvOperations.ToElement(stampedSpec));
            case HvOperation.SetVmConfig when vm is not null && HvOperations.FromElement<VmConfigChange>(parameters.Args) is { } change:
                var (v2, stampedChange) = await policies.CheckConfigAsync(vm, change, ct);
                return (v2, HvOperations.ToElement(stampedChange));
            case HvOperation.NewSwitch when parameters.GroupId is { } ng:
                return (await policies.CheckNewSwitchAsync(ng, jobId, ct), parameters.Args);
            default:
                return (null, parameters.Args);
        }
    }

    /// <summary>A private network created for a folder belongs to it (quota, deletion by its users); a removed switch belongs to nobody.</summary>
    private async Task RecordSwitchOwnershipAsync(PortalDbContext db, Job job, Guid hostId, JobService.OperationParameters parameters, JsonElement? result)
    {
        try
        {
            if (job.Operation == "Create" && parameters.GroupId is { } groupId && HvOperations.FromElement<NewSwitchRequest>(parameters.Args) is { } created)
            {
                Guid? switchId = result is { ValueKind: JsonValueKind.Object } r && r.TryGetProperty("id", out var idp) && Guid.TryParse(idp.GetString(), out var parsed) ? parsed : null;
                if (await db.LogicalGroups.AnyAsync(g => g.Id == groupId) && !await db.GroupNetworks.AnyAsync(n => n.HostId == hostId && n.SwitchName == created.Name))
                {
                    db.GroupNetworks.Add(new GroupNetwork
                    {
                        GroupId = groupId, HostId = hostId, SwitchName = created.Name, SwitchId = switchId, CreatedAt = _time.GetUtcNow(), CreatedByUserId = job.RequestedByUserId,
                    });
                    await db.SaveChangesAsync();
                    await _notifier.GroupsChangedAsync();
                }
            }
            else if (job.Operation == "Delete" && HvOperations.FromElement<SwitchRef>(parameters.Args) is { Name: { } name })
            {
                if (await db.GroupNetworks.Where(n => n.HostId == hostId && n.SwitchName == name).ExecuteDeleteAsync() > 0)
                {
                    await _notifier.GroupsChangedAsync();
                }
            }
        }
        catch (DbUpdateException ex)
        {
            _logger.LogWarning(ex, "Could not record the ownership of the switch of job {JobId}", job.Id);
        }
    }

    private static string? Describe(HvOperationOutcome outcome)
    {
        var applied = outcome.Applied.Count == 0 ? null : string.Join(", ", outcome.Applied.Take(20));
        if (outcome.Ok)
        {
            return applied is null ? "Completed." : "Applied: " + applied + ".";
        }
        return applied is null
            ? outcome.Error ?? "The operation failed."
            : $"Partially applied ({applied}). Then failed: {outcome.Error}";
    }

    /// <summary>
    /// After a VM was created with a target group: waits for the VM to appear in inventory (the agent pushes a snapshot
    /// right after the operation) and adds it to the group, if the group still exists and the requester still manages groups.
    /// </summary>
    private async Task PlaceCreatedVmAsync(PortalDbContext db, Job job, Guid hostId, NewVmResult created, Guid groupId)
    {
        try
        {
            VirtualMachine? vm = null;
            // The agent pushes a fresh inventory right after a mutating command; if it has not arrived after ~1.5 s,
            // ask for one explicitly, then keep polling briefly.
            for (var i = 0; i < 12 && vm is null; i++)
            {
                if (i == 3 && _dispatcher.IsConnected(hostId))
                {
                    var inv = await _dispatcher.SendAsync(hostId, new AgentCommand { CommandId = Guid.NewGuid(), Kind = AgentCommandKind.CollectInventory },
                        null, TimeSpan.FromSeconds(90), CancellationToken.None);
                    if (inv.Inventory is { } snapshot)
                    {
                        using var s = _scopes.CreateScope();
                        await s.ServiceProvider.GetRequiredService<InventoryIngestor>().ApplyAsync(hostId, snapshot, CancellationToken.None);
                    }
                }
                vm = await db.VirtualMachines.AsNoTracking().FirstOrDefaultAsync(v => v.HostId == hostId && v.HyperVId == created.VmId);
                if (vm is null)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), _time);
                }
            }
            using var scope = _scopes.CreateScope();
            var access = scope.ServiceProvider.GetRequiredService<AccessEvaluator>();
            var placeDb = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
            var check = vm is null ? "vm_not_in_inventory"
                : await Api.GroupEndpoints.CheckNewVmPlacementAsync(placeDb, access, job.RequestedByUserId, groupId, hostId, CancellationToken.None);
            if (check is null && !await placeDb.LogicalGroupMembers.AnyAsync(m => m.GroupId == groupId && m.VmId == vm!.Id))
            {
                placeDb.LogicalGroupMembers.Add(new LogicalGroupMember { GroupId = groupId, VmId = vm!.Id });
                await placeDb.SaveChangesAsync();
                AddEvent(placeDb, job, "group", "Added to the requested group.");
                await placeDb.SaveChangesAsync();
                await _notifier.GroupsChangedAsync();
            }
            else if (check is not null)
            {
                AddEvent(placeDb, job, "group", $"The VM was created but not added to the group ({check}).");
                await placeDb.SaveChangesAsync();
            }
        }
        catch (Exception ex) when (ex is HostNotConnectedException or AgentChannelLostException or TimeoutException or DbUpdateException)
        {
            _logger.LogWarning(ex, "Could not place created VM for job {JobId} into group {GroupId}", job.Id, groupId);
        }
    }

    internal async Task ReconcileAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
        var jobs = await db.Jobs.Where(j => j.Status == JobStatus.Reconciling).OrderBy(j => j.ReconcileStartedAt).Take(50).ToListAsync(ct);
        foreach (var job in jobs)
        {
            var now = _time.GetUtcNow();
            if (job.Type == JobService.NetworkJobType)
            {
                // Creating an external switch on the adapter the agent uses drops its channel for a moment: verify from the host.
                await ReconcileSwitchAsync(db, job, now, ct);
                continue;
            }
            if (JobService.HvOperationFor(job.Type, job.Operation) is not null)
            {
                // Configuration changes cannot be verified generically from inventory state, and are never re-sent.
                await FinalizeAsync(db, job, JobStatus.Unknown,
                    "The command was sent but its result was not received. Check the VM on the host before retrying.", "outcome_unknown", null);
                await ReleaseLockAsync(db, job);
                continue;
            }
            var giveUp = now - (job.ReconcileStartedAt ?? now) > TimeSpan.FromMinutes(_options.ReconciliationGiveUpMinutes);
            var vm = await db.VirtualMachines.AsNoTracking().FirstOrDefaultAsync(v => v.Id == job.TargetId, ct);
            var isDelete = job.Type == JobService.DeleteJobType;
            var op = default(VmPowerOperation);
            if (vm is null || job.HostId is not { } hostId || (!isDelete && !Enum.TryParse(job.Operation, out op)))
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
            if (isDelete)
            {
                if (probe.Status == AgentCommandStatus.NotFound)
                {
                    await FinalizeAsync(db, job, JobStatus.Succeeded, "Outcome verified by reconciliation: the VM no longer exists on the host.", null, null);
                    await ReleaseLockAsync(db, job);
                    await InventoryCleanup.RemoveVmAsync(db, vm.Id, hostId, "vm.deleted", now, ct);
                }
                else
                {
                    await FinalizeAsync(db, job, JobStatus.Unknown, "The VM still exists on the host; the delete did not complete.", "outcome_unknown", null);
                    await ReleaseLockAsync(db, job);
                }
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

    /// <summary>Outcome of a switch job whose result was lost: the switch exists (create) or is gone (delete) on the host.</summary>
    private async Task ReconcileSwitchAsync(PortalDbContext db, Job job, DateTimeOffset now, CancellationToken ct)
    {
        var parameters = ReadParameters(job);
        var giveUp = now - (job.ReconcileStartedAt ?? now) > TimeSpan.FromMinutes(_options.ReconciliationGiveUpMinutes);
        if (parameters is null || job.HostId is not { } hostId)
        {
            await FinalizeAsync(db, job, JobStatus.Unknown, "Outcome cannot be determined. Check the virtual switches on the host.", "outcome_unknown", null);
            await ReleaseLockAsync(db, job);
            return;
        }
        List<SwitchInfo>? switches = null;
        if (_dispatcher.IsConnected(hostId))
        {
            try
            {
                var probe = await _dispatcher.SendAsync(hostId, new AgentCommand
                {
                    CommandId = Guid.NewGuid(), Kind = AgentCommandKind.HyperVOperation, Operation = HvOperation.ListSwitches,
                    OperationArgs = HvOperations.ToElement(new { }), TimeoutSeconds = 60,
                }, null, TimeSpan.FromSeconds(75), ct);
                if (probe.Operation is { Ok: true, Result: { } r })
                {
                    switches = r.Deserialize<List<SwitchInfo>>(AgentProtocol.Json);
                }
            }
            catch (Exception ex) when (ex is HostNotConnectedException or AgentChannelLostException or TimeoutException or JsonException)
            {
                switches = null;
            }
        }
        if (switches is null)
        {
            if (giveUp)
            {
                await FinalizeAsync(db, job, JobStatus.Unknown, "Host unreachable; the outcome could not be verified. Check the virtual switches on the host.", "outcome_unknown", null);
                await ReleaseLockAsync(db, job);
            }
            return;
        }
        var creating = job.Operation == "Create";
        var name = creating ? HvOperations.FromElement<NewSwitchRequest>(parameters.Args)?.Name : HvOperations.FromElement<SwitchRef>(parameters.Args)?.Name;
        var found = switches.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));
        if (creating == (found is not null))
        {
            await RecordSwitchOwnershipAsync(db, job, hostId, parameters, found is null ? null : HvOperations.ToElement(new { id = found.Id }));
            await FinalizeAsync(db, job, JobStatus.Succeeded,
                creating ? "Outcome verified by reconciliation: the switch exists on the host." : "Outcome verified by reconciliation: the switch no longer exists on the host.", null, null);
        }
        else
        {
            await FinalizeAsync(db, job, JobStatus.Unknown,
                creating ? "The switch is not present on the host: the creation did not complete." : "The switch still exists on the host: the removal did not complete.",
                "outcome_unknown", null);
        }
        await ReleaseLockAsync(db, job);
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
        if (job.Type is JobService.TemplateCaptureJobType or JobService.TemplateDeleteJobType
            && await TemplateOfAsync(db, job, null, CancellationToken.None) is { } template)
        {
            // Saved with the job: the template never shows a state its job did not reach.
            Templates.TemplateRules.ApplyJobOutcome(db, template, job, status, message);
        }
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
            ResourceType = job.TargetType == ScopeType.Host ? "host" : "vm",
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
