namespace HvPortal.Agent.HyperV;

/// <summary>
/// Detaches restore point disks that are no longer used: those left attached by a previous run of the agent (at start)
/// and those idle for more than <see cref="BackupMounts.IdleLimit"/> (every 10 minutes).
/// </summary>
public sealed class BackupMountJanitor(PowerShellOperationRunner runner, ILogger<BackupMountJanitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var leftover in BackupMounts.Leftovers())
        {
            await DetachAsync(leftover, "left attached by a previous run", stoppingToken);
        }
        BackupMounts.ClearLeftovers();
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                foreach (var idle in BackupMounts.Idle(DateTimeOffset.UtcNow))
                {
                    if (BackupMounts.Remove(idle.MountId) is { } removed)
                    {
                        await DetachAsync(removed, "idle", stoppingToken);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task DetachAsync(BackupMounts.Entry entry, string reason, CancellationToken ct)
    {
        try
        {
            var outcome = await runner.DismountAsync(entry, ct);
            if (outcome.Ok)
            {
                logger.LogInformation("Detached backup disk {Path} ({Reason})", entry.DiskPath, reason);
            }
            else
            {
                logger.LogWarning("Could not detach backup disk {Path} ({Reason}): {Error}", entry.DiskPath, reason, outcome.Error);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not detach backup disk {Path}", entry.DiskPath);
        }
    }
}
