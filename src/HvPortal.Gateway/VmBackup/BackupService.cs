using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Agents;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Jobs;
using Microsoft.EntityFrameworkCore;

namespace HvPortal.Gateway.VmBackup;

/// <summary>
/// Backup runs (ADR-031). A run creates one restore point and one persistent job per VM; jobs are executed, re-authorized
/// and reconciled by the job runner like every other change, as the user who started the run (for scheduled runs: the
/// user who saved the job). When every point of a run has a final state the run is closed, old points are removed by the
/// retention of the job and the notifications are sent.
/// </summary>
public sealed class BackupService(PortalDbContext db, JobService jobs, TimeProvider time, IAgentDispatcher agents)
{
    /// <summary>Upper bound of a backup job on the host (export + copy + hashes of large VMs over a network).</summary>
    public const int BackupTimeoutSeconds = 12 * 3600;

    /// <summary>VMs protected by a job: its VMs and the VMs of its groups and their sub-groups (resolved now).</summary>
    public Task<List<VirtualMachine>> ResolveVmsAsync(Guid jobId, CancellationToken ct) => ResolveVmsAsync(db, jobId, ct);

    /// <summary>
    /// Hosts that back up into <paramref name="repo"/> but have no backup staging folder, while the repository needs one
    /// (share with credentials): the hosts of the VMs protected by its jobs and, for a cluster, every node of it (its VMs
    /// move between nodes). Empty for repositories without credentials.
    /// </summary>
    public static async Task<List<ManagedHost>> HostsMissingStagingAsync(PortalDbContext db, BackupRepository repo, CancellationToken ct)
    {
        if (repo.PasswordEncrypted is null)
        {
            return [];
        }
        var hostIds = new HashSet<Guid>();
        foreach (var jobId in await db.BackupJobs.AsNoTracking().Where(j => j.RepositoryId == repo.Id).Select(j => j.Id).ToListAsync(ct))
        {
            hostIds.UnionWith((await ResolveVmsAsync(db, jobId, ct)).Select(v => v.HostId));
        }
        if (hostIds.Count == 0)
        {
            return [];
        }
        var clusterIds = await db.Hosts.AsNoTracking().Where(h => hostIds.Contains(h.Id) && h.ClusterId != null).Select(h => h.ClusterId!.Value).Distinct().ToListAsync(ct);
        return await db.Hosts.AsNoTracking()
            .Where(h => h.Status == HostStatus.Active && h.BackupStagingPath == null
                && (hostIds.Contains(h.Id) || (h.ClusterId != null && clusterIds.Contains(h.ClusterId.Value))))
            .OrderBy(h => h.DisplayName).ToListAsync(ct);
    }

    public static async Task<List<VirtualMachine>> ResolveVmsAsync(PortalDbContext db, Guid jobId, CancellationToken ct)
    {
        var targets = await db.BackupJobTargets.AsNoTracking().Where(t => t.BackupJobId == jobId).ToListAsync(ct);
        var vmIds = targets.Where(t => t.Kind == "vm").Select(t => t.TargetId).ToHashSet();
        var groupIds = targets.Where(t => t.Kind == "group").Select(t => t.TargetId).ToHashSet();
        if (groupIds.Count > 0)
        {
            var parents = await db.LogicalGroups.AsNoTracking().Select(g => new { g.Id, g.ParentId }).ToListAsync(ct);
            var changed = true;
            for (var depth = 0; changed && depth < AccessEvaluator.MaxGroupDepth; depth++)
            {
                changed = false;
                foreach (var g in parents.Where(g => g.ParentId is { } p && groupIds.Contains(p) && !groupIds.Contains(g.Id)))
                {
                    groupIds.Add(g.Id);
                    changed = true;
                }
            }
            foreach (var m in await db.LogicalGroupMembers.AsNoTracking().Where(m => groupIds.Contains(m.GroupId)).Select(m => m.VmId).ToListAsync(ct))
            {
                vmIds.Add(m);
            }
        }
        return await db.VirtualMachines.AsNoTracking().Where(v => vmIds.Contains(v.Id) && !v.IsMissing).OrderBy(v => v.Name).ToListAsync(ct);
    }

    /// <summary>Starts a run of a backup job: a point and a job per VM. VMs that cannot use the repository get a failed point (visible in the run).</summary>
    public async Task<BackupRun> StartRunAsync(BackupJob job, string trigger, Guid requestedBy, CancellationToken ct)
    {
        var repo = await db.BackupRepositories.AsNoTracking().FirstOrDefaultAsync(r => r.Id == job.RepositoryId, ct);
        var now = time.GetUtcNow();
        var run = new BackupRun { BackupJobId = job.Id, Trigger = trigger, RequestedByUserId = requestedBy, StartedAt = now };
        db.BackupRuns.Add(run);
        await db.SaveChangesAsync(ct);
        if (repo is null)
        {
            run.Status = BackupRunStatus.Failed;
            run.CompletedAt = now;
            run.Message = "The repository of the job no longer exists.";
            await db.SaveChangesAsync(ct);
            return run;
        }
        var vms = await ResolveVmsAsync(job.Id, ct);
        foreach (var vm in vms)
        {
            await QueueBackupAsync(vm, repo, run, job.Id, job.CheckpointType, job.VerifyAfterCopy, requestedBy, $"Backup of {vm.Name} to {repo.Name} (job {job.Name})", ct);
        }
        run.Total = vms.Count;
        if (vms.Count == 0)
        {
            run.Message = "The job protects no VM (its VMs and groups are empty).";
        }
        await db.SaveChangesAsync(ct);
        return run;
    }

    /// <summary>"Back up now" of one VM: a run of its own (trigger adhoc), kept with the retention of no job.</summary>
    public async Task<(BackupRun Run, BackupPoint Point)> StartAdHocAsync(VirtualMachine vm, BackupRepository repo, string checkpointType, bool verify, Guid user,
        CancellationToken ct)
    {
        var run = new BackupRun { Trigger = "adhoc", RequestedByUserId = user, StartedAt = time.GetUtcNow(), Total = 1 };
        db.BackupRuns.Add(run);
        await db.SaveChangesAsync(ct);
        var point = await QueueBackupAsync(vm, repo, run, null, checkpointType, verify, user, $"Backup now of {vm.Name} to {repo.Name}", ct);
        return (run, point);
    }

    private async Task<BackupPoint> QueueBackupAsync(VirtualMachine vm, BackupRepository repo, BackupRun run, Guid? jobId, string checkpointType, bool verify,
        Guid user, string description, CancellationToken ct)
    {
        var point = new BackupPoint
        {
            RunId = run.Id,
            BackupJobId = jobId,
            RepositoryId = repo.Id,
            VmId = vm.Id,
            VmHyperVId = vm.HyperVId,
            VmName = vm.Name,
            HostId = vm.HostId,
            Folder = "",
            Status = BackupPointStatus.Creating,
            CreatedAt = time.GetUtcNow(),
            CheckpointType = checkpointType,
        };
        point.Folder = BackupRules.PointFolder(vm.Name, vm.HyperVId, point.Id);
        db.BackupPoints.Add(point);
        var host = await db.Hosts.AsNoTracking().FirstOrDefaultAsync(h => h.Id == vm.HostId, ct);
        (string Code, string Message)? refused = host is null ? ("host_not_found", "The host of the VM is no longer managed.") : BackupRules.CanUse(repo, host);
        if (refused is { } why)
        {
            point.Status = BackupPointStatus.Failed;
            point.Error = why.Message;
            point.CompletedAt = point.CreatedAt;
            await db.SaveChangesAsync(ct);
            return point;
        }
        await db.SaveChangesAsync(ct);
        // The arguments carry no credentials: the job runner takes target, folder and point from the records when the job runs.
        var args = new BackupVmRequest
        {
            VmId = vm.HyperVId,
            Target = new BackupTarget { Path = repo.Path },
            Folder = point.Folder,
            PointId = point.Id,
            CheckpointType = checkpointType,
            VerifyAfterCopy = verify,
        };
        var (job, _) = await jobs.CreateOperationJobAsync(user, new JobService.OperationJobSpec(JobService.BackupJobType, "Backup", ScopeType.Vm, vm.Id, vm.Name,
            vm.HostId, Permissions.BackupRun, new JobService.OperationParameters(HvOperations.ToElement(args), BackupPointId: point.Id), BackupTimeoutSeconds,
            description), null, ct);
        point.JobId = job.Id;
        await db.SaveChangesAsync(ct);
        return point;
    }

    /// <summary>Closes the runs whose points all have a final state. Returns them (for retention and notifications).</summary>
    public async Task<List<BackupRun>> CompleteRunsAsync(CancellationToken ct)
    {
        var finished = new List<BackupRun>();
        var running = await db.BackupRuns.Where(r => r.Status == BackupRunStatus.Running).ToListAsync(ct);
        foreach (var run in running)
        {
            var points = await db.BackupPoints.AsNoTracking().Where(p => p.RunId == run.Id).Select(p => new { p.Status, p.VmName, p.Error }).ToListAsync(ct);
            if (points.Any(p => p.Status == BackupPointStatus.Creating))
            {
                continue;
            }
            run.Succeeded = points.Count(p => p.Status is BackupPointStatus.Ok or BackupPointStatus.Deleted or BackupPointStatus.Deleting);
            run.Warnings = points.Count(p => p.Status == BackupPointStatus.Unverified);
            run.Failed = points.Count(p => p.Status == BackupPointStatus.Failed);
            run.Total = points.Count;
            run.Status = run.Total == 0 ? BackupRunStatus.Warning
                : run.Failed > 0 && run.Succeeded == 0 && run.Warnings == 0 ? BackupRunStatus.Failed
                : run.Failed > 0 || run.Warnings > 0 ? BackupRunStatus.Warning
                : BackupRunStatus.Success;
            run.CompletedAt = time.GetUtcNow();
            var failedNames = points.Where(p => p.Status == BackupPointStatus.Failed).Select(p => $"{p.VmName}: {p.Error}").Take(10).ToList();
            run.Message ??= failedNames.Count > 0 ? string.Join(" | ", failedNames) : null;
            if (run.Message is { Length: > 2000 } long1)
            {
                run.Message = long1[..2000];
            }
            finished.Add(run);
        }
        await db.SaveChangesAsync(ct);
        return finished;
    }

    /// <summary>
    /// Retention of a job: per VM, the newest <see cref="BackupJob.RetentionPoints"/> usable points are kept (and none older
    /// than RetentionDays); kept points (Keep) are never removed. Removal is a job on a host that can reach the repository.
    /// </summary>
    public async Task<int> ApplyRetentionAsync(BackupJob job, CancellationToken ct)
    {
        var repo = await db.BackupRepositories.AsNoTracking().FirstOrDefaultAsync(r => r.Id == job.RepositoryId, ct);
        if (repo is null)
        {
            return 0;
        }
        var now = time.GetUtcNow();
        var points = await db.BackupPoints.Where(p => p.BackupJobId == job.Id && (p.Status == BackupPointStatus.Ok || p.Status == BackupPointStatus.Unverified
            || p.Status == BackupPointStatus.Failed)).ToListAsync(ct);
        var removed = 0;
        foreach (var perVm in points.GroupBy(p => p.VmId))
        {
            var good = perVm.Where(p => p.Status == BackupPointStatus.Ok).OrderByDescending(p => p.CreatedAt).ToList();
            var withinCount = good.Take(Math.Clamp(job.RetentionPoints, 1, BackupRules.MaxRetentionPoints)).Select(p => p.Id).ToHashSet();
            foreach (var p in perVm.OrderBy(p => p.CreatedAt))
            {
                // Kept points, and the newest good point of a VM whatever its age, are never removed.
                if (p.Keep || (good.Count > 0 && p.Id == good[0].Id))
                {
                    continue;
                }
                var remove = (p.Status == BackupPointStatus.Ok && !withinCount.Contains(p.Id))
                             || (job.RetentionDays is { } days && now - p.CreatedAt > TimeSpan.FromDays(days))
                             // Failed and unverified points older than the newest good one have been superseded.
                             || (p.Status is BackupPointStatus.Failed or BackupPointStatus.Unverified && good.Count > 0 && p.CreatedAt < good[0].CreatedAt);
                if (remove && await QueueRemoveAsync(p, repo, job.CreatedByUserId, $"Retention of job {job.Name}", ct) is not null)
                {
                    removed++;
                }
            }
        }
        return removed;
    }

    /// <summary>Deletion of a point: a job on its host (or any host that can reach a share). Null when no host can do it now.</summary>
    public async Task<Job?> QueueRemoveAsync(BackupPoint point, BackupRepository repo, Guid user, string reason, CancellationToken ct)
    {
        if (await HostForAsync(point, repo, ct) is not { } host)
        {
            return null;
        }
        var tracked = await db.BackupPoints.FirstAsync(p => p.Id == point.Id, ct);
        var previous = tracked.Status;
        tracked.Status = BackupPointStatus.Deleting;
        await db.SaveChangesAsync(ct);
        try
        {
            var args = new BackupPointRequest { Target = new BackupTarget { Path = repo.Path }, Folder = point.Folder, PointId = point.Id };
            var (job, _) = await jobs.CreateOperationJobAsync(user, new JobService.OperationJobSpec(JobService.BackupRemoveJobType, "Delete", ScopeType.Host, host.Id,
                point.VmName, host.Id, Permissions.BackupManage, new JobService.OperationParameters(HvOperations.ToElement(args), BackupPointId: point.Id), 3600,
                $"Delete restore point of {point.VmName} ({point.CreatedAt:u}): {reason}"), null, ct);
            tracked.JobId = job.Id;
            await db.SaveChangesAsync(ct);
            return job;
        }
        catch
        {
            tracked.Status = previous;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// The host that works on a point: its own host when connected, else (shares only) another connected host that can use
    /// the repository. Null when none is available.
    /// </summary>
    public async Task<ManagedHost?> HostForAsync(BackupPoint point, BackupRepository repo, CancellationToken ct, Guid? preferred = null)
    {
        var hosts = await db.Hosts.AsNoTracking().Where(h => h.Status == HostStatus.Active).ToListAsync(ct);
        bool Usable(ManagedHost h) => agents.IsConnected(h.Id) && agents.ProtocolVersionOf(h.Id) >= 9 && BackupRules.CanUse(repo, h) is null;
        return hosts.FirstOrDefault(h => h.Id == (preferred ?? point.HostId) && Usable(h))
               ?? (repo.HostId is null ? hosts.FirstOrDefault(Usable) : null);
    }

    /// <summary>Restore tests every N days: the newest good point of each VM of the job, restored isolated, started, checked, removed.</summary>
    public async Task<int> QueueRestoreTestsAsync(BackupJob job, CancellationToken ct)
    {
        var repo = await db.BackupRepositories.AsNoTracking().FirstOrDefaultAsync(r => r.Id == job.RepositoryId, ct);
        if (repo is null)
        {
            return 0;
        }
        var newest = (await db.BackupPoints.AsNoTracking().Where(p => p.BackupJobId == job.Id && p.Status == BackupPointStatus.Ok).ToListAsync(ct))
            .GroupBy(p => p.VmId).Select(g => g.OrderByDescending(p => p.CreatedAt).First()).ToList();
        var queued = 0;
        foreach (var p in newest)
        {
            if (await HostForAsync(p, repo, ct) is not { BackupStagingPath: { } staging } host)
            {
                continue;
            }
            await QueueRestoreAsync(p, host, RestoreModes.Test, "hvp-test-" + p.Id.ToString("N")[..8], staging + @"\restore-tests", RestoreNetworks.Disconnected, null,
                false, null, job.CreatedByUserId, $"Scheduled restore test (job {job.Name})", ct);
            queued++;
        }
        return queued;
    }

    /// <summary>Restore job: Replace targets the VM (lock per VM), NewVm and Test target the host.</summary>
    public async Task<(Job Job, bool Created)> QueueRestoreAsync(BackupPoint point, ManagedHost host, string mode, string? newName, string? location, string network,
        string? switchName, bool startAfter, VirtualMachine? replace, Guid user, string description, CancellationToken ct, string? idempotencyKey = null)
    {
        var args = new RestoreBackupRequest
        {
            Source = new BackupTarget { Path = "x" },
            Folder = point.Folder,
            PointId = point.Id,
            Mode = mode,
            NewName = mode == RestoreModes.Replace ? null : newName,
            Location = mode == RestoreModes.Replace ? null : location,
            Network = network,
            SwitchName = network == RestoreNetworks.Switch ? switchName : null,
            StartAfter = startAfter,
            ReplaceVmId = replace?.HyperVId,
        };
        var spec = mode == RestoreModes.Replace && replace is not null
            ? new JobService.OperationJobSpec(JobService.RestoreJobType, mode, ScopeType.Vm, replace.Id, replace.Name, replace.HostId, Permissions.BackupRestore,
                new JobService.OperationParameters(HvOperations.ToElement(args), BackupPointId: point.Id), BackupTimeoutSeconds, description)
            : new JobService.OperationJobSpec(JobService.RestoreJobType, mode, ScopeType.Host, host.Id, newName ?? point.VmName, host.Id, Permissions.BackupRestore,
                new JobService.OperationParameters(HvOperations.ToElement(args), BackupPointId: point.Id), BackupTimeoutSeconds, description);
        return await jobs.CreateOperationJobAsync(user, spec, idempotencyKey, ct);
    }
}
