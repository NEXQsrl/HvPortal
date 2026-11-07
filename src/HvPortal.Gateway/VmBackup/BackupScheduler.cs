using HvPortal.Gateway.Api;
using HvPortal.Gateway.Audit;
using HvPortal.Gateway.Configuration;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HvPortal.Gateway.VmBackup;

/// <summary>
/// Clock of the backups: starts the scheduled runs, closes the finished ones (status, retention, notification), queues
/// the periodic restore tests, notifies failed restore tests and, once a day, agents older than the gateway's package.
/// </summary>
public sealed class BackupScheduler(IServiceScopeFactory scopes, NotificationService notifications, AuditLog audit, TimeProvider time,
    IOptions<GatewayOptions> options, ILogger<BackupScheduler> logger) : BackgroundService
{
    private DateTimeOffset _lastTestCheck = DateTimeOffset.MinValue;
    private DateTimeOffset _lastOutdatedNotice = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Backup.SchedulerEnabled)
        {
            return;
        }
        _lastTestCheck = time.GetUtcNow();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Backup scheduler pass failed");
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(options.Value.Backup.IntervalSeconds, 5, 300)), time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One pass (also called by tests).</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<BackupService>();
        var now = time.GetUtcNow();

        var enabled = await db.BackupJobs.Where(j => j.Enabled).ToListAsync(ct);
        foreach (var job in enabled)
        {
            if (job.ScheduleKind == "manual")
            {
                job.NextRunAt = null;
                continue;
            }
            if (job.NextRunAt is null)
            {
                job.NextRunAt = BackupRules.NextRun(job, now);
                continue;
            }
            if (job.NextRunAt > now)
            {
                continue;
            }
            if (await db.BackupRuns.AnyAsync(r => r.BackupJobId == job.Id && r.Status == BackupRunStatus.Running, ct))
            {
                logger.LogWarning("Backup job {Job}: the previous run is still running, this start is skipped", job.Name);
            }
            else
            {
                var run = await service.StartRunAsync(job, "schedule", job.CreatedByUserId, ct);
                job.LastRunAt = now;
                await audit.WriteAsync(new AuditRecord
                {
                    Action = "backup.job.run", AuthMethod = "schedule", ActorUserId = job.CreatedByUserId, ResourceType = "backup_job", ResourceId = job.Id.ToString("D"),
                    Details = new { job = job.Name, runId = run.Id, vms = run.Total },
                }, ct);
            }
            job.NextRunAt = BackupRules.NextRun(job, now);
        }
        await db.SaveChangesAsync(ct);

        foreach (var run in await service.CompleteRunsAsync(ct))
        {
            var job = run.BackupJobId is { } jobId ? await db.BackupJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct) : null;
            if (job is not null)
            {
                await service.ApplyRetentionAsync(job, ct);
            }
            await NotifyRunAsync(db, run, job, ct);
        }

        foreach (var job in enabled.Where(j => j.RestoreTestDays > 0 && (j.LastRestoreTestAt is null || now - j.LastRestoreTestAt > TimeSpan.FromDays(j.RestoreTestDays))))
        {
            if (await service.QueueRestoreTestsAsync(job, ct) > 0)
            {
                job.LastRestoreTestAt = now;
            }
        }
        await db.SaveChangesAsync(ct);

        await NotifyFailedTestsAsync(db, now, ct);
        if (now - _lastOutdatedNotice > TimeSpan.FromDays(1))
        {
            _lastOutdatedNotice = now;
            await NotifyOutdatedAgentsAsync(db, scope.ServiceProvider, ct);
        }
    }

    private static bool ShouldNotify(string notifyOn, BackupRunStatus status) => notifyOn switch
    {
        "always" => true,
        "warning" => status is BackupRunStatus.Warning or BackupRunStatus.Failed,
        "failure" => status == BackupRunStatus.Failed,
        _ => false,
    };

    private async Task NotifyRunAsync(PortalDbContext db, BackupRun run, BackupJob? job, CancellationToken ct)
    {
        // "Back up now" runs are reported only when they fail.
        if (run.Notified || !ShouldNotify(job?.NotifyOn ?? "failure", run.Status))
        {
            return;
        }
        var points = await db.BackupPoints.AsNoTracking().Where(p => p.RunId == run.Id).OrderBy(p => p.VmName).ToListAsync(ct);
        var name = job?.Name ?? "Back up now";
        var lines = points.Select(p => $"- {p.VmName}: {p.Status}{(p.Error is { } e ? " — " + e : p.SizeBytes > 0 ? $" ({BackupJobOutcome.FormatSize(p.SizeBytes)})" : "")}");
        var text = $"Backup {name}: {run.Status}. {run.Succeeded} ok, {run.Warnings} to verify, {run.Failed} failed, started {run.StartedAt:u}.\n\n"
                   + string.Join("\n", lines) + (Link("/backup?run=" + run.Id.ToString("D")) is { } link ? "\n\n" + link : "");
        var errors = await notifications.SendAsync(new NotificationService.Message("backup.run", run.Status switch
        {
            BackupRunStatus.Failed => "critical",
            BackupRunStatus.Warning => "warning",
            _ => "ok",
        }, $"Backup {name}: {run.Status}", text, new { runId = run.Id, job = name, run.Status, run.Succeeded, run.Warnings, run.Failed }), ct);
        await db.BackupRuns.Where(r => r.Id == run.Id).ExecuteUpdateAsync(u => u.SetProperty(r => r.Notified, true), ct);
        if (errors.Count > 0)
        {
            await audit.WriteAsync(new AuditRecord { Action = "notifications.send", Outcome = AuditOutcome.Failure, AuthMethod = "system", Details = new { @event = "backup.run", errors } }, ct);
        }
    }

    private async Task NotifyFailedTestsAsync(PortalDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var since = _lastTestCheck;
        _lastTestCheck = now;
        var failed = await db.Jobs.AsNoTracking()
            .Where(j => j.Type == JobService.RestoreJobType && j.Operation == "Test" && j.Status != JobStatus.Succeeded
                        && j.Status != JobStatus.Queued && j.Status != JobStatus.Running && j.Status != JobStatus.Reconciling && j.CompletedAt > since)
            .ToListAsync(ct);
        if (failed.Count == 0)
        {
            return;
        }
        var settings = await notifications.LoadAsync(ct);
        if (!settings.RestoreTestFailed)
        {
            return;
        }
        var text = string.Join("\n", failed.Select(j => $"- {j.TargetName}: {j.Status} — {j.ResultMessage}"));
        await notifications.SendAsync(new NotificationService.Message("backup.restore_test", "critical", $"Restore test failed ({failed.Count})", text), ct, settings);
    }

    private async Task NotifyOutdatedAgentsAsync(PortalDbContext db, IServiceProvider services, CancellationToken ct)
    {
        var package = DownloadEndpoints.Describe(services.GetRequiredService<IOptions<GatewayOptions>>().Value);
        if (package?.Version is not { } version)
        {
            return;
        }
        var settings = await notifications.LoadAsync(ct);
        if (!settings.AgentsOutdated)
        {
            return;
        }
        var hosts = await db.Hosts.AsNoTracking().Where(h => h.Status == HostStatus.Active && h.ConnectionMode == HostConnectionMode.Agent && h.AgentVersion != null)
            .Select(h => new { h.DisplayName, h.AgentVersion }).ToListAsync(ct);
        var managers = await db.ManagementAgents.AsNoTracking().Where(a => a.Status == HostStatus.Active && a.AgentVersion != null)
            .Select(a => new { a.DisplayName, a.AgentVersion }).ToListAsync(ct);
        var outdated = hosts.Concat(managers).Where(x => !AgentVersions.AtLeast(x.AgentVersion, version)).ToList();
        if (outdated.Count == 0)
        {
            return;
        }
        var text = $"{outdated.Count} agent(s) older than {version}, the version of this gateway:\n"
                   + string.Join("\n", outdated.Select(x => $"- {x.DisplayName}: {x.AgentVersion}"))
                   + (Link("/guide/update") is { } link ? "\n\nUpdate them from the portal: " + link : "");
        await notifications.SendAsync(new NotificationService.Message("agents.outdated", "warning", $"{outdated.Count} agent(s) to update", text), ct, settings);
    }

    private string? Link(string path)
    {
        var o = options.Value;
        var name = o.PublicNames.FirstOrDefault();
        return name is null ? null : $"https://{name}{(o.UiPort == 443 ? "" : ":" + o.UiPort)}{path}";
    }
}
