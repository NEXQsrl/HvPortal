using System.Text.Json;
using HvPortal.Contracts.Agent;
using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Jobs;
using Microsoft.EntityFrameworkCore;

namespace HvPortal.Gateway.VmBackup;

/// <summary>
/// What a finished job means for its restore point. A point is Ok only with the manifest the agent wrote; a lost
/// result makes it Unverified (verify it before relying on it), never Ok.
/// </summary>
public static class BackupJobOutcome
{
    /// <summary>Result of a job that reached the host (called before the job is finalized). Returns the job status, error code and message to keep.</summary>
    public static async Task<(JobStatus Status, string? Code, string? Message)> ApplyResultAsync(PortalDbContext db, HvOperation op, Job job, Guid pointId,
        JobStatus status, string? code, HvOperationOutcome? outcome, string? message, DateTimeOffset now)
    {
        var point = await db.BackupPoints.FirstOrDefaultAsync(p => p.Id == pointId);
        if (point is null)
        {
            return (status, code, message);
        }
        var ok = status == JobStatus.Succeeded && outcome is { Ok: true };
        switch (op)
        {
            case HvOperation.BackupVm when ok:
                var manifest = Read<BackupManifest>(outcome!.Result);
                if (manifest is null || manifest.PointId != point.Id || manifest.Files.Count == 0)
                {
                    point.Status = BackupPointStatus.Unverified;
                    point.Error = "The agent reported success without a valid manifest: verify the restore point.";
                    await db.SaveChangesAsync();
                    return (JobStatus.Unverified, "manifest_missing", point.Error);
                }
                point.Status = BackupPointStatus.Ok;
                point.CompletedAt = now;
                point.SizeBytes = manifest.TotalBytes;
                point.CheckpointTypeUsed = manifest.CheckpointTypeUsed is { Length: > 40 } u ? u[..40] : manifest.CheckpointTypeUsed;
                point.ManifestJson = BackupRules.StoreManifest(manifest);
                point.Error = null;
                // Verified when the copy was read back from the repository (or exported there by Hyper-V and hashed in place).
                point.VerifiedAt = now;
                point.VerifyOk = true;
                point.VerifyDetail = $"{manifest.Files.Count} files, SHA-256 recorded at backup time.";
                await db.SaveChangesAsync();
                var warnings = manifest.Warnings.Count > 0 ? " Warnings: " + string.Join(" ", manifest.Warnings) : "";
                return (warnings.Length > 0 ? JobStatus.Succeeded : status, code,
                    $"Restore point {FormatSize(manifest.TotalBytes)} ({manifest.Files.Count} files, checkpoint {manifest.CheckpointTypeUsed ?? "?"}).{warnings}");
            case HvOperation.VerifyBackup when ok:
                var check = Read<BackupVerifyResult>(outcome!.Result);
                point.VerifiedAt = now;
                point.VerifyOk = check?.Ok == true;
                point.VerifyDetail = check is null ? "No result." : check.Ok
                    ? $"{check.FilesChecked} files ({FormatSize(check.BytesChecked)}) match their SHA-256."
                    : Truncate("Problems: " + string.Join("; ", check.Problems.Take(20)), 2000);
                await db.SaveChangesAsync();
                return check?.Ok == true ? (status, code, point.VerifyDetail) : (JobStatus.Failed, "verify_failed", point.VerifyDetail);
            case HvOperation.RemoveBackup when ok:
                point.Status = BackupPointStatus.Deleted;
                await db.SaveChangesAsync();
                return (status, code, "Restore point deleted from the repository.");
            case HvOperation.RestoreBackup when ok:
                var restored = Read<RestoreBackupResult>(outcome!.Result);
                var extra = restored?.Warnings.Count > 0 ? " " + string.Join(" ", restored.Warnings) : "";
                if (restored?.Mode == RestoreModes.Test)
                {
                    point.TestedAt = now;
                    point.TestPassed = restored.TestPassed == true;
                    point.TestDetail = Truncate(restored.TestDetail, 1000);
                    await db.SaveChangesAsync();
                    return restored.TestPassed == true
                        ? (status, code, "Restore test passed: " + restored.TestDetail + extra)
                        : (JobStatus.Failed, "restore_test_failed", "Restore test failed: " + restored.TestDetail + extra);
                }
                return (status, code, $"Restored as {restored?.Name}{(restored?.Path is { } path ? " in " + path : "")}.{extra}");
            default:
                return (status, code, message);
        }
    }

    /// <summary>Every final state of a job (also refused, unreachable or lost ones): the point never stays "in progress".</summary>
    public static async Task ApplyFinalAsync(PortalDbContext db, Job job, Guid pointId, JobStatus status, string? message)
    {
        var point = await db.BackupPoints.FirstOrDefaultAsync(p => p.Id == pointId);
        if (point is null)
        {
            return;
        }
        switch (job.Type)
        {
            case JobService.BackupJobType when point.Status == BackupPointStatus.Creating:
                point.Status = status switch
                {
                    JobStatus.Unknown or JobStatus.Unverified or JobStatus.Reconciling => BackupPointStatus.Unverified,
                    JobStatus.Succeeded => BackupPointStatus.Unverified,
                    _ => BackupPointStatus.Failed,
                };
                point.CompletedAt = job.CompletedAt;
                point.Error = Truncate(message, 2000);
                break;
            case JobService.BackupRemoveJobType when point.Status == BackupPointStatus.Deleting:
                // The files may be partly gone after a failure: never shown as a good point again without a check.
                point.Status = status == JobStatus.Succeeded ? BackupPointStatus.Deleted
                    : status == JobStatus.Failed && job.DispatchedAt is null ? BackupPointStatus.Ok : BackupPointStatus.Unverified;
                point.Error = status == JobStatus.Succeeded ? null : Truncate("Delete: " + message, 2000);
                break;
        }
    }

    private static T? Read<T>(JsonElement? e)
    {
        try
        {
            return e is { } v ? v.Deserialize<T>(AgentProtocol.Json) : default;
        }
        catch (JsonException)
        {
            return default;
        }
    }

    internal static string FormatSize(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.#} GB" : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.#} MB" : $"{bytes} B";

    private static string? Truncate(string? s, int max) => s is { Length: > 0 } && s.Length > max ? s[..max] : s;
}
