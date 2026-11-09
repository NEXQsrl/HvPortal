using System.Security.Cryptography;
using System.Text.Json;
using HvPortal.Contracts.Agent;
using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Agents;
using HvPortal.Gateway.Api;
using HvPortal.Gateway.Audit;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Configuration;
using HvPortal.Gateway.Crypto;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Networks;
using HvPortal.Gateway.Quotas;
using HvPortal.Gateway.VmBackup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HvPortal.Gateway.Jobs;

/// <summary>Protocol 9 jobs (v0.7.0): VM move, backups and restores, networks with VLAN, agent updates.</summary>
public sealed partial class JobRunner
{
    /// <summary>Jobs that may run for hours: they have their own concurrency budget (MaxConcurrentLongJobs).</summary>
    internal static bool IsLongJob(string type) =>
        type is JobService.BackupJobType or JobService.BackupVerifyJobType or JobService.RestoreJobType or JobService.RestoreDiskJobType
            or JobService.MigrateJobType or JobService.AgentUpdateJobType or JobService.TemplateCaptureJobType or JobService.MaintenanceJobType;

    private static async Task<ResourceRef> BackupResourceAsync(PortalDbContext db, Guid pointId, CancellationToken ct)
    {
        var vmId = await db.BackupPoints.AsNoTracking().Where(p => p.Id == pointId).Select(p => (Guid?)p.VmId).FirstOrDefaultAsync(ct);
        return vmId is { } id && await db.VirtualMachines.AnyAsync(v => v.Id == id, ct) ? ResourceRef.Vm(id) : ResourceRef.Global;
    }

    /// <summary>Permissions on a second resource: the destination host of a move, the host a VM is restored to, the VM a disk is attached to.</summary>
    private static async Task<bool> HoldsExtraPermissionsAsync(PortalDbContext db, AccessEvaluator access, HvOperation op, Job job,
        JobService.OperationParameters parameters, CancellationToken ct)
    {
        var user = job.RequestedByUserId;
        switch (op)
        {
            case HvOperation.MoveVm or HvOperation.MoveClusterVm when parameters.DestinationHostId is { } destination:
                return await access.HasAsync(user, Permissions.VmCreate, ResourceRef.Host(destination), ct);
            case HvOperation.RestoreBackup when job.Operation is "NewVm" or "Test":
                return await access.HasAsync(user, Permissions.VmCreate, ResourceRef.Host(job.TargetId), ct);
            case HvOperation.RestoreBackupDisk when HvOperations.FromElement<RestoreDiskRequest>(parameters.Args) is { AttachVmId: { } attach }:
                var attachTo = await db.VirtualMachines.AsNoTracking().Where(v => v.HostId == job.TargetId && v.HyperVId == attach && !v.IsMissing)
                    .Select(v => (Guid?)v.Id).FirstOrDefaultAsync(ct);
                return attachTo is { } portalId && await access.HasAsync(user, Permissions.VmConfigStorage, ResourceRef.Vm(portalId), ct);
            case HvOperation.RestoreBackupDisk:
                // A disk copied to a host folder without attaching it: write access to the files of that host.
                return await access.HasAsync(user, Permissions.HostFilesWrite, ResourceRef.Host(job.TargetId), ct);
            default:
                return true;
        }
    }

    /// <summary>Server-controlled fields of protocol 9 operations, from the portal's records at execution time.</summary>
    private static async Task<((string Code, string Message)? Error, JsonElement Args)> StampProtocol9Async(IServiceProvider services, PortalDbContext db,
        HvOperation op, Job job, JobService.OperationParameters parameters, VirtualMachine? vm, ManagedHost host, CancellationToken ct)
    {
        var args = parameters.Args;
        switch (op)
        {
            case HvOperation.NewVm when HvOperations.FromElement<NewVmRequest>(args) is { NetworkId: not null } spec:
                var (vmNetError, withNetwork) = await NetworkRules.ResolveAsync(db, host.Id, spec, ct);
                return (vmNetError, vmNetError is null ? HvOperations.ToElement(withNetwork) : args);
            case HvOperation.SetVmConfig when HvOperations.FromElement<VmConfigChange>(args) is { } change:
                var (netError, resolved) = await NetworkRules.ResolveAsync(db, host.Id, change, ct);
                return (netError, netError is null ? HvOperations.ToElement(resolved) : args);
            case HvOperation.MoveVm when HvOperations.FromElement<MoveVmRequest>(args) is { } move:
                if (parameters.DestinationHostId is not { } destinationId
                    || await db.Hosts.AsNoTracking().FirstOrDefaultAsync(h => h.Id == destinationId && h.Status == HostStatus.Active, ct) is not { } destination)
                {
                    return (("destination_not_found", "The destination host is no longer managed by the portal."), args);
                }
                if (MigrationRules.DestinationName(destination) is not { } name)
                {
                    return (("destination_unknown_name", "The DNS name of the destination host is not known yet (no inventory)."), args);
                }
                if (move.DestinationPath is { } path && InventoryEndpoints.StorageFoldersOf(destination) is { Count: > 0 } allowed && !StorageFolderRules.IsInside(path, allowed))
                {
                    return (("location_not_allowed", "The destination folder is outside the storage folders allowed on the destination host."), args);
                }
                // The limits of the VM's folders (hosts, storage paths, quotas) are checked again when the job runs.
                if (vm is not null && await services.GetRequiredService<GroupPolicyService>().CheckMoveAsync(vm, destinationId, move.IncludeStorage ? move.DestinationPath : null, ct)
                    is { } moveViolation)
                {
                    return ((moveViolation.Code, moveViolation.Message), args);
                }
                return (null, HvOperations.ToElement(move with { DestinationHost = name }));
            case HvOperation.MoveVmStorage when HvOperations.FromElement<MoveVmStorageRequest>(args) is { } storageMove:
                if (InventoryEndpoints.StorageFoldersOf(host) is { Count: > 0 } hostAllowed && !StorageFolderRules.IsInside(storageMove.DestinationPath, hostAllowed))
                {
                    return (("location_not_allowed", "The destination folder is outside the storage folders allowed on this host."), args);
                }
                if (vm is not null && await services.GetRequiredService<GroupPolicyService>().CheckMoveAsync(vm, host.Id, storageMove.DestinationPath, ct) is { } storageViolation)
                {
                    return ((storageViolation.Code, storageViolation.Message), args);
                }
                return (null, args);
            case HvOperation.BackupVm or HvOperation.VerifyBackup or HvOperation.RemoveBackup or HvOperation.RestoreBackup or HvOperation.RestoreBackupDisk:
                if (parameters.BackupPointId is not { } pointId
                    || await db.BackupPoints.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pointId, ct) is not { } point)
                {
                    return (("backup_point_not_found", "The restore point no longer exists."), args);
                }
                if (await db.BackupRepositories.AsNoTracking().FirstOrDefaultAsync(r => r.Id == point.RepositoryId, ct) is not { } repo)
                {
                    return (("repository_not_found", "The repository of the restore point no longer exists."), args);
                }
                if (BackupRules.CanUse(repo, host) is { } refused)
                {
                    return (refused, args);
                }
                BackupTarget target;
                try
                {
                    target = BackupRules.Target(repo, host, services.GetRequiredService<KeyRing>());
                }
                catch (CryptographicException)
                {
                    return (("repository_credentials_unreadable", "The stored credentials of the repository cannot be decrypted: enter them again (Backup > Repositories)."), args);
                }
                switch (op)
                {
                    case HvOperation.BackupVm when HvOperations.FromElement<BackupVmRequest>(args) is { } backup:
                        return (null, HvOperations.ToElement(backup with { Target = target, Folder = point.Folder, PointId = point.Id }));
                    case HvOperation.VerifyBackup or HvOperation.RemoveBackup:
                        return (null, HvOperations.ToElement(new BackupPointRequest { Target = target, Folder = point.Folder, PointId = point.Id }));
                    case HvOperation.RestoreBackup when HvOperations.FromElement<RestoreBackupRequest>(args) is { } restore:
                        if (point.Status is not (BackupPointStatus.Ok or BackupPointStatus.Unverified))
                        {
                            return (("backup_point_not_usable", $"The restore point is {point.Status}."), args);
                        }
                        return (null, HvOperations.ToElement(restore with { Source = target, Folder = point.Folder, PointId = point.Id }));
                    case HvOperation.RestoreBackupDisk when HvOperations.FromElement<RestoreDiskRequest>(args) is { } disk:
                        if (!BackupRules.Disks(point).Contains(disk.Disk, StringComparer.OrdinalIgnoreCase))
                        {
                            return (("invalid_disk", "The disk is not part of the restore point."), args);
                        }
                        return (null, HvOperations.ToElement(disk with { Source = target, Folder = point.Folder, PointId = point.Id }));
                    default:
                        return (("invalid_job", "Invalid job parameters."), args);
                }
            default:
                return (null, args);
        }
    }

    /// <summary>What a finished protocol 9 job changes in the portal (VM moved, restore point state). May turn a host success into a failure (e.g. a failed restore test).</summary>
    private async Task<(JobStatus Status, string? Code, string? Message)> ApplyProtocol9ResultAsync(PortalDbContext db, HvOperation op, Job job,
        JobService.OperationParameters parameters, VirtualMachine? vm, Guid hostId, JobStatus status, string? code, HvOperationOutcome? outcome, string? message)
    {
        try
        {
            if (op == HvOperation.MoveVm && status == JobStatus.Succeeded && vm is not null && parameters.DestinationHostId is { } destination)
            {
                await MigrationRules.ApplyMoveAsync(db, vm, destination, _time.GetUtcNow(), CancellationToken.None);
                await _notifier.GroupsChangedAsync();
                return (status, code, $"Moved to {MigrationRules.DestinationLabel(await db.Hosts.AsNoTracking().FirstOrDefaultAsync(h => h.Id == destination))}.");
            }
            if (op == HvOperation.MoveVmStorage && status == JobStatus.Succeeded && vm is not null
                && outcome?.Result is { ValueKind: JsonValueKind.Object } r && r.TryGetProperty("path", out var p) && p.GetString() is { Length: > 0 and <= 520 } path)
            {
                await db.VirtualMachines.Where(v => v.Id == vm.Id).ExecuteUpdateAsync(u => u.SetProperty(v => v.ConfigurationPath, path));
                return (status, code, "Files moved to " + path + ".");
            }
            if (parameters.BackupPointId is { } pointId && op is HvOperation.BackupVm or HvOperation.VerifyBackup or HvOperation.RemoveBackup or HvOperation.RestoreBackup)
            {
                return await BackupJobOutcome.ApplyResultAsync(db, op, job, pointId, status, code, outcome, message, _time.GetUtcNow());
            }
        }
        catch (DbUpdateException ex)
        {
            _logger.LogWarning(ex, "Could not record the outcome of job {JobId}", job.Id);
        }
        return (status, code, message);
    }

    // ------------------------------------------------------------------ agent update (ADR-029)

    private static readonly TimeSpan UpdateReconnectWait = TimeSpan.FromMinutes(10);

    private async Task RunAgentUpdateAsync(IServiceProvider services, PortalDbContext db, AccessEvaluator access, Job job, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == job.RequestedByUserId, ct);
        if (user is not { IsEnabled: true } || !await access.HasAsync(job.RequestedByUserId, Permissions.HostsManage, ResourceRef.Global, ct))
        {
            await FinalizeAsync(db, job, JobStatus.Failed, "The requesting user is no longer authorized for this operation.", "authorization_revoked", null, AuditOutcome.Denied);
            return;
        }
        var management = job.Operation == "ManagementAgent";
        var target = ReadParameters(job)?.UpdateVersion;
        var package = DownloadEndpoints.Describe(services.GetRequiredService<IOptions<GatewayOptions>>().Value);
        if (package?.Version is not { } version || target is null || !string.Equals(version, target, StringComparison.Ordinal))
        {
            await FinalizeAsync(db, job, JobStatus.Failed, "The agent package of this gateway changed or is missing: request the update again.", "package_changed", null);
            return;
        }
        if (_dispatcher.AgentOf(job.TargetId) is not { } before)
        {
            await RetryOrFailUnreachableAsync(db, job, ct);
            return;
        }
        if (AgentVersions.AtLeast(before.Version, version))
        {
            await FinalizeAsync(db, job, JobStatus.Succeeded, $"No change needed: the agent already runs {before.Version}.", null, null);
            return;
        }
        if (before.Protocol < 9)
        {
            await FinalizeAsync(db, job, JobStatus.Failed,
                $"The agent runs {before.Version ?? "an old version"}: updates from the portal need agent 0.7.0 or later. Update it once by hand (Guide > Update).", "agent_outdated", null);
            return;
        }
        var now = _time.GetUtcNow();
        await db.Jobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(u => u.SetProperty(j => j.DispatchedAt, now), ct);
        await db.Entry(job).ReloadAsync(ct);
        AddEvent(db, job, "dispatched", $"Package {version} ({package.Sha256[..12]}…) offered to the agent.");
        await db.SaveChangesAsync(ct);
        await _notifier.JobChangedAsync(job);

        var command = new AgentCommand
        {
            CommandId = job.Id,
            Kind = AgentCommandKind.UpdateAgent,
            TimeoutSeconds = 600,
            Update = new AgentUpdateRequest { Version = version, Sha256 = package.Sha256, SizeBytes = package.SizeBytes },
        };
        AgentCommandResult? result = null;
        try
        {
            result = management
                ? await _dispatcher.SendToManagementAgentAsync(job.TargetId, command, TimeSpan.FromMinutes(20), ct)
                : await _dispatcher.SendAsync(job.TargetId, command, null, TimeSpan.FromMinutes(20), ct);
        }
        catch (HostNotConnectedException)
        {
            await RetryOrFailUnreachableAsync(db, job, ct);
            return;
        }
        catch (Exception ex) when (ex is AgentChannelLostException or TimeoutException)
        {
            // The service may stop for the update before its answer leaves: the reconnection tells.
            AddEvent(db, job, "reconnecting", "The channel closed after the package was offered: waiting for the agent to reconnect.");
            await db.SaveChangesAsync(ct);
        }
        if (result is not null && result.Status != AgentCommandStatus.Succeeded)
        {
            await FinalizeAsync(db, job, JobStatus.Failed, result.Message ?? result.Status.ToString(), result.Status.ToString().ToLowerInvariant(), null);
            return;
        }
        if (result is not null)
        {
            AddEvent(db, job, "staged", result.Message);
            job.ProgressMessage = "Waiting for the agent to restart with version " + version + ".";
            job.Progress = 50;
            await db.SaveChangesAsync(ct);
            await _notifier.JobChangedAsync(job);
        }

        var deadline = _time.GetUtcNow() + UpdateReconnectWait;
        while (_time.GetUtcNow() < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), _time, ct);
            await db.Jobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(u => u.SetProperty(j => j.HeartbeatAt, _time.GetUtcNow())
                .SetProperty(j => j.LeaseExpiresAt, _time.GetUtcNow().AddMinutes(2)), ct);
            if (_dispatcher.AgentOf(job.TargetId) is { } after && AgentVersions.AtLeast(after.Version, version))
            {
                await db.Entry(job).ReloadAsync(ct);
                await FinalizeAsync(db, job, JobStatus.Succeeded, $"The agent reconnected with version {after.Version}.", null, null);
                return;
            }
        }
        await db.Entry(job).ReloadAsync(ct);
        var reported = management
            ? await db.ManagementAgents.AsNoTracking().Where(a => a.Id == job.TargetId).Select(a => a.LastUpdateResult).FirstOrDefaultAsync(ct)
            : await db.Hosts.AsNoTracking().Where(h => h.Id == job.TargetId).Select(h => h.AgentLastUpdateResult).FirstOrDefaultAsync(ct);
        // Only an outcome written after this job started counts: "<time> failed ..." left by an earlier attempt does not.
        if (reported is not null && reported.Contains(" failed ", StringComparison.Ordinal) && AgentVersions.IsAfter(reported, job.StartedAt ?? job.CreatedAt))
        {
            await FinalizeAsync(db, job, JobStatus.Failed, "The updater refused or could not install the package: " + reported, "update_failed", null);
        }
        else
        {
            await FinalizeAsync(db, job, JobStatus.Unknown,
                $"The agent did not reconnect with version {version} within {UpdateReconnectWait.TotalMinutes:0} minutes. Check the host (event log, {AgentUpdaterLog}).", "outcome_unknown", null);
        }
    }

    private const string AgentUpdaterLog = @"C:\ProgramData\HvPortal\Agent\update\last-result.json";
}

/// <summary>Comparison of agent versions ("0.7.0", "0.7.0.0", "0.7.0+abc").</summary>
public static class AgentVersions
{
    public static Version? Parse(string? v)
    {
        var core = v?.Split('+', '-')[0];
        return System.Version.TryParse(core, out var parsed) ? new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0)) : null;
    }

    public static bool AtLeast(string? agent, string? target) => Parse(agent) is { } a && Parse(target) is { } t && a >= t;

    /// <summary>The time at the start of an updater outcome ("&lt;ISO time&gt; &lt;status&gt; ...") is after <paramref name="since"/>.</summary>
    internal static bool IsAfter(string reported, DateTimeOffset since)
    {
        var space = reported.IndexOf(' ', StringComparison.Ordinal);
        return space > 0 && DateTimeOffset.TryParse(reported[..space], System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var at) && at >= since.AddMinutes(-1);
    }
}
