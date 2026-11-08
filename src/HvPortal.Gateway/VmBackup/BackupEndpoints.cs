using System.Security.Cryptography;
using System.Text.Json;
using HvPortal.Contracts.Agent;
using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Agents;
using HvPortal.Gateway.Api;
using HvPortal.Gateway.Audit;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Crypto;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Jobs;
using HvPortal.Gateway.Security;
using Microsoft.EntityFrameworkCore;

namespace HvPortal.Gateway.VmBackup;

/// <summary>
/// Backups (ADR-031): repositories, jobs, runs, restore points, restores, "back up now". Changes on hosts are jobs;
/// reads are records of the portal or live agent calls. Permissions: backup.manage (repositories, jobs, deleting points),
/// backup.run (back up, run jobs, verify), backup.restore (restore VMs, disks and files) - on the VM, or Global for the
/// restore points of VMs that no longer exist.
/// </summary>
public static class BackupEndpoints
{
    public sealed record RepositoryBody(string? Name, string? Description, string? Path, Guid? HostId, string? UserName, string? Password);
    public sealed record JobBody(string? Name, string? Description, Guid RepositoryId, bool Enabled, string? ScheduleKind, int DaysMask, string[]? Times,
        int IntervalHours, string? TimeZoneId, int RetentionPoints, int? RetentionDays, string? CheckpointType, bool VerifyAfterCopy, int RestoreTestDays,
        string? NotifyOn, Guid[]? VmIds, Guid[]? GroupIds);
    public sealed record AdHocBody(Guid RepositoryId, string? CheckpointType, bool? VerifyAfterCopy, string? IdempotencyKey);
    public sealed record RestoreBody(string? Mode, Guid? HostId, string? Name, string? Location, string? Network, string? SwitchName, bool StartAfter, string? ConfirmName,
        string? IdempotencyKey);
    public sealed record RestoreDiskBody(string? Disk, Guid? HostId, string? DestinationFolder, Guid? AttachVmId);
    public sealed record HostRef(Guid? HostId);
    public sealed record KeepBody(bool Keep);
    public sealed record DeletePointBody(string? ConfirmName);

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/backup").WithTags("Backup");
        g.MapGet("/overview", OverviewAsync);
        g.MapGet("/repositories", RepositoriesAsync);
        g.MapPost("/repositories", CreateRepositoryAsync);
        g.MapPut("/repositories/{id:guid}", UpdateRepositoryAsync);
        g.MapDelete("/repositories/{id:guid}", DeleteRepositoryAsync);
        g.MapPost("/repositories/{id:guid}/check", CheckRepositoryAsync);
        g.MapPost("/repositories/{id:guid}/rescan", RescanRepositoryAsync);
        g.MapGet("/jobs", JobsAsync);
        g.MapPost("/jobs", CreateJobAsync);
        g.MapPut("/jobs/{id:guid}", UpdateJobAsync);
        g.MapDelete("/jobs/{id:guid}", DeleteJobAsync);
        g.MapPost("/jobs/{id:guid}/run", RunJobAsync);
        g.MapGet("/runs", RunsAsync);
        g.MapGet("/runs/{id:guid}", RunAsync);
        g.MapGet("/points", PointsAsync);
        g.MapGet("/points/{id:guid}", PointAsync);
        g.MapPost("/points/{id:guid}/verify", VerifyPointAsync);
        g.MapPost("/points/{id:guid}/delete", DeletePointAsync);
        g.MapPost("/points/{id:guid}/keep", KeepPointAsync);
        g.MapPost("/points/{id:guid}/restore", RestoreAsync);
        g.MapPost("/points/{id:guid}/restore-disk", RestoreDiskAsync);
        var vms = app.MapGroup("/api/v1/vms").WithTags("Backup");
        vms.MapGet("/{id:guid}/backups", VmBackupsAsync);
        vms.MapPost("/{id:guid}/backup", BackupNowAsync);
        app.MapGet("/api/v1/hosts/{id:guid}/backup-readiness", HostReadinessAsync).WithTags("Backup");
    }

    // ------------------------------------------------------------------ access

    private static readonly string[] BackupPermissions = [Permissions.BackupManage, Permissions.BackupRun, Permissions.BackupRestore];

    /// <summary>Who sees the backup area: any backup permission held at Global scope.</summary>
    internal static async Task<bool> CanSeeAsync(HttpContext http, AccessEvaluator access)
    {
        foreach (var p in BackupPermissions)
        {
            if (await access.HasAsync(http.User.UserId(), p, ResourceRef.Global, http.RequestAborted))
            {
                return true;
            }
        }
        return false;
    }

    private static async Task<IResult?> GuardSeeAsync(HttpContext http, AccessEvaluator access, string action)
    {
        if (await CanSeeAsync(http, access))
        {
            return null;
        }
        await http.WriteAuditAsync(action, AuditOutcome.Denied, details: new { permission = "backup.*" });
        return ApiSupport.Forbidden("forbidden", "Missing a backup permission (backup.manage, backup.run or backup.restore) at Global scope.");
    }

    /// <summary>The scope of a restore point: its VM while it exists, Global once it is gone.</summary>
    internal static async Task<ResourceRef> PointScopeAsync(PortalDbContext db, BackupPoint p, CancellationToken ct) =>
        await db.VirtualMachines.AnyAsync(v => v.Id == p.VmId, ct) ? ResourceRef.Vm(p.VmId) : ResourceRef.Global;

    private static async Task<bool> CanReadPointAsync(HttpContext http, PortalDbContext db, AccessEvaluator access, BackupPoint p)
    {
        var scope = await PointScopeAsync(db, p, http.RequestAborted);
        var uid = http.User.UserId();
        if (scope.Type == ScopeType.Vm && !await access.HasAsync(uid, Permissions.InventoryRead, scope, http.RequestAborted))
        {
            return false;
        }
        foreach (var perm in BackupPermissions)
        {
            if (await access.HasAsync(uid, perm, perm == Permissions.BackupManage ? ResourceRef.Global : scope, http.RequestAborted))
            {
                return true;
            }
        }
        return false;
    }

    // ------------------------------------------------------------------ DTOs

    private static object RepoDto(BackupRepository r, string? hostName, bool manage, int points, long bytes) => new
    {
        r.Id, r.Name, r.Description, r.Path, r.HostId, hostName,
        isShare = r.Path.StartsWith(@"\\", StringComparison.Ordinal),
        hasCredentials = r.PasswordEncrypted is not null,
        userName = manage ? r.UserName : null,
        r.CredentialUpdatedAt, r.CreatedAt, r.LastCheckedAt, r.LastCheckHostId,
        lastCheck = r.LastCheckJson is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(r.LastCheckJson),
        points, bytes,
    };

    private static object JobDto(BackupJob j, IEnumerable<BackupJobTarget> targets, BackupRun? last, string? owner) => new
    {
        j.Id, j.Name, j.Description, j.Enabled, j.RepositoryId, j.ScheduleKind, j.DaysMask, times = BackupRules.Times(j), j.IntervalHours, j.TimeZoneId,
        j.RetentionPoints, j.RetentionDays, j.CheckpointType, j.VerifyAfterCopy, j.RestoreTestDays, j.NotifyOn, j.NextRunAt, j.LastRunAt, j.LastRestoreTestAt,
        vmIds = targets.Where(t => t.Kind == "vm").Select(t => t.TargetId), groupIds = targets.Where(t => t.Kind == "group").Select(t => t.TargetId),
        runsAs = owner, lastRun = last is null ? null : RunDto(last, j.Name),
    };

    private static object RunDto(BackupRun r, string? jobName) => new
    {
        r.Id, r.BackupJobId, jobName, r.Trigger, r.StartedAt, r.CompletedAt, status = r.Status.ToString(), r.Total, r.Succeeded, r.Warnings, r.Failed, r.Message,
    };

    internal static object PointDto(BackupPoint p, string? repoName, string? jobName, bool vmExists) => new
    {
        p.Id, p.RunId, p.BackupJobId, jobName, p.RepositoryId, repositoryName = repoName, p.VmId, p.VmHyperVId, p.VmName, vmExists, p.HostId, p.Folder,
        status = p.Status.ToString(), p.CreatedAt, p.CompletedAt, p.SizeBytes, p.CheckpointType, p.CheckpointTypeUsed, p.Error, p.JobId,
        p.VerifiedAt, p.VerifyOk, p.VerifyDetail, p.TestedAt, p.TestPassed, p.TestDetail, p.Keep, disks = BackupRules.Disks(p),
    };

    // ------------------------------------------------------------------ overview

    private static async Task<IResult> OverviewAsync(HttpContext http, PortalDbContext db, AccessEvaluator access, TimeProvider time)
    {
        if (await GuardSeeAsync(http, access, "backup.overview") is { } denied)
        {
            return denied;
        }
        var now = time.GetUtcNow();
        var since = now.AddHours(-24);
        var runs = await db.BackupRuns.AsNoTracking().Where(r => r.StartedAt >= since).ToListAsync(http.RequestAborted);
        var points = await db.BackupPoints.AsNoTracking().Where(p => p.Status != BackupPointStatus.Deleted).ToListAsync(http.RequestAborted);
        var vms = await db.VirtualMachines.AsNoTracking().Where(v => !v.IsMissing).Select(v => new { v.Id, v.Name, v.HostId }).ToListAsync(http.RequestAborted);
        var protectedIds = (await ProtectedVmIdsAsync(db, http.RequestAborted)).ToHashSet();
        var latest = points.Where(p => p.Status is BackupPointStatus.Ok or BackupPointStatus.Unverified or BackupPointStatus.Failed)
            .GroupBy(p => p.VmId).Select(g => g.OrderByDescending(p => p.CreatedAt).First()).ToDictionary(p => p.VmId);
        var jobNames = await db.BackupJobs.AsNoTracking().ToDictionaryAsync(j => j.Id, j => j.Name, http.RequestAborted);
        return Results.Ok(new
        {
            last24h = new
            {
                runs = runs.Count,
                success = runs.Count(r => r.Status == BackupRunStatus.Success),
                warning = runs.Count(r => r.Status == BackupRunStatus.Warning),
                failed = runs.Count(r => r.Status == BackupRunStatus.Failed),
                running = runs.Count(r => r.Status == BackupRunStatus.Running),
            },
            repositories = await db.BackupRepositories.CountAsync(http.RequestAborted),
            jobs = await db.BackupJobs.CountAsync(http.RequestAborted),
            enabledJobs = await db.BackupJobs.CountAsync(j => j.Enabled, http.RequestAborted),
            points = points.Count(p => p.Status == BackupPointStatus.Ok),
            bytes = points.Where(p => p.Status == BackupPointStatus.Ok).Sum(p => p.SizeBytes),
            vms = vms.Count,
            protectedVms = vms.Count(v => protectedIds.Contains(v.Id)),
            // VMs in no enabled job and without a good restore point in the last 7 days.
            unprotected = vms.Where(v => !protectedIds.Contains(v.Id) && !(latest.TryGetValue(v.Id, out var l) && l.Status == BackupPointStatus.Ok && now - l.CreatedAt < TimeSpan.FromDays(7)))
                .Select(v => new { v.Id, v.Name, v.HostId }).Take(200),
            // Latest point of each VM when it is not good.
            problems = latest.Values.Where(p => p.Status != BackupPointStatus.Ok || p.TestPassed == false || p.VerifyOk == false)
                .OrderByDescending(p => p.CreatedAt).Take(50).Select(p => PointDto(p, null, p.BackupJobId is { } j ? jobNames.GetValueOrDefault(j) : null, true)),
            recentRuns = (await db.BackupRuns.AsNoTracking().OrderByDescending(r => r.StartedAt).Take(15).ToListAsync(http.RequestAborted))
                .Select(r => RunDto(r, r.BackupJobId is { } j ? jobNames.GetValueOrDefault(j) : null)),
        });
    }

    /// <summary>VMs covered by an enabled job (directly or through a group).</summary>
    internal static async Task<List<Guid>> ProtectedVmIdsAsync(PortalDbContext db, CancellationToken ct)
    {
        var enabled = await db.BackupJobs.AsNoTracking().Where(j => j.Enabled).Select(j => j.Id).ToListAsync(ct);
        var ids = new HashSet<Guid>();
        foreach (var j in enabled)
        {
            foreach (var vm in await BackupService.ResolveVmsAsync(db, j, ct))
            {
                ids.Add(vm.Id);
            }
        }
        return ids.ToList();
    }

    // ------------------------------------------------------------------ repositories

    private static async Task<IResult> RepositoriesAsync(HttpContext http, PortalDbContext db, AccessEvaluator access)
    {
        if (await GuardSeeAsync(http, access, "backup.repositories.list") is { } denied)
        {
            return denied;
        }
        var manage = await access.HasAsync(http.User.UserId(), Permissions.BackupManage, ResourceRef.Global, http.RequestAborted);
        var hosts = await db.Hosts.AsNoTracking().ToDictionaryAsync(h => h.Id, h => h.DisplayName, http.RequestAborted);
        var usage = await db.BackupPoints.AsNoTracking().Where(p => p.Status == BackupPointStatus.Ok || p.Status == BackupPointStatus.Unverified)
            .GroupBy(p => p.RepositoryId).Select(g => new { g.Key, n = g.Count(), bytes = g.Sum(p => p.SizeBytes) }).ToDictionaryAsync(x => x.Key, http.RequestAborted);
        var repos = await db.BackupRepositories.AsNoTracking().OrderBy(r => r.Name).ToListAsync(http.RequestAborted);
        return Results.Ok(repos.Select(r => RepoDto(r, r.HostId is { } h ? hosts.GetValueOrDefault(h) : null, manage,
            usage.TryGetValue(r.Id, out var u) ? u.n : 0, usage.TryGetValue(r.Id, out var b) ? b.bytes : 0)));
    }

    private static async Task<(string? Error, string? Detail)> ValidateRepositoryAsync(PortalDbContext db, RepositoryBody b, BackupRepository? existing, CancellationToken ct)
    {
        if (!BackupRules.NameRegex().IsMatch(b.Name?.Trim() ?? "")) return ("invalid_repository_name", null);
        if (b.Description is { Length: > 500 }) return ("description_too_long", null);
        var path = existing?.Path ?? (b.Path?.Trim() is { Length: > 0 } p ? (p.StartsWith(@"\\", StringComparison.Ordinal) ? p.TrimEnd('\\') : HostPaths.Normalize(p)) : "");
        if (!OperationValidation.IsValidRepositoryPath(path)) return ("invalid_repository_path", @"A local folder such as D:\Backups (not a system folder) or a share such as \\nas01\backups.");
        var share = path.StartsWith(@"\\", StringComparison.Ordinal);
        var hostId = existing?.HostId ?? b.HostId;
        if (!share)
        {
            if (hostId is not { } h || await db.Hosts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == h, ct) is not { } host)
            {
                return ("host_required", "A local folder belongs to one host: choose it.");
            }
            if (InventoryEndpoints.StorageFoldersOf(host) is { Count: > 0 } allowed && !StorageFolderRules.IsInside(path, allowed))
            {
                return ("location_not_allowed", "The folder is outside the storage folders allowed on that host.");
            }
        }
        else if (b.HostId is not null && existing is null)
        {
            return ("share_has_no_host", "A share can be used by every host: no host is chosen.");
        }
        if (!string.IsNullOrEmpty(b.UserName) || !string.IsNullOrEmpty(b.Password))
        {
            if (!share) return ("credentials_only_for_shares", null);
            if (!RemoteHosts.IsValidUserName(b.UserName)) return ("invalid_repository_credentials", @"User name such as NAS01\backup or backup@nas.local.");
            if (b.Password is not null && b.Password.Length > 0 && !RemoteHosts.IsValidPassword(b.Password)) return ("invalid_repository_credentials", null);
        }
        return (null, path);
    }

    private static async Task<IResult> CreateRepositoryAsync(RepositoryBody body, HttpContext http, PortalDbContext db, KeyRing keys, TimeProvider time)
    {
        // Repository credentials: like the other settings with secrets, they need a recent re-authentication.
        if ((await http.GuardAsync(Permissions.BackupManage, ResourceRef.Global, "backup.repository.create") ?? await http.RequireRecentReauthAsync()) is { } denied)
        {
            return denied;
        }
        var (error, pathOrDetail) = await ValidateRepositoryAsync(db, body, null, http.RequestAborted);
        if (error is not null)
        {
            return ApiSupport.BadRequest(error, pathOrDetail);
        }
        var name = body.Name!.Trim();
        if (await db.BackupRepositories.AnyAsync(r => r.Name == name, http.RequestAborted))
        {
            return ApiSupport.Conflict("repository_name_exists");
        }
        if (!string.IsNullOrEmpty(body.UserName) && string.IsNullOrEmpty(body.Password))
        {
            return ApiSupport.BadRequest("invalid_repository_credentials", "Enter the password of the share user.");
        }
        var repo = new BackupRepository
        {
            Name = name, Description = string.IsNullOrWhiteSpace(body.Description) ? null : body.Description.Trim(), Path = pathOrDetail!,
            HostId = pathOrDetail!.StartsWith(@"\\", StringComparison.Ordinal) ? null : body.HostId, CreatedAt = time.GetUtcNow(), CreatedByUserId = http.User.UserId(),
        };
        if (!string.IsNullOrEmpty(body.UserName))
        {
            repo.UserName = body.UserName;
            repo.PasswordEncrypted = BackupRules.Protect(keys, repo.Id, body.Password!);
            repo.CredentialUpdatedAt = repo.CreatedAt;
        }
        db.BackupRepositories.Add(repo);
        await db.SaveChangesAsync(http.RequestAborted);
        await http.WriteAuditAsync("backup.repository.create", resourceType: "backup_repository", resourceId: repo.Id.ToString("D"),
            details: new { repo.Name, repo.Path, repo.HostId, credentials = repo.PasswordEncrypted is not null });
        return Results.Ok(RepoDto(repo, null, true, 0, 0));
    }

    private static async Task<IResult> UpdateRepositoryAsync(Guid id, RepositoryBody body, HttpContext http, PortalDbContext db, KeyRing keys, TimeProvider time)
    {
        if ((await http.GuardAsync(Permissions.BackupManage, ResourceRef.Global, "backup.repository.update") ?? await http.RequireRecentReauthAsync()) is { } denied)
        {
            return denied;
        }
        var repo = await db.BackupRepositories.FirstOrDefaultAsync(r => r.Id == id, http.RequestAborted);
        if (repo is null)
        {
            return ApiSupport.NotFound();
        }
        // The path never changes: the restore points live there.
        var (error, detail) = await ValidateRepositoryAsync(db, body with { Path = repo.Path, HostId = repo.HostId }, repo, http.RequestAborted);
        if (error is not null)
        {
            return ApiSupport.BadRequest(error, detail);
        }
        var name = body.Name!.Trim();
        if (await db.BackupRepositories.AnyAsync(r => r.Name == name && r.Id != id, http.RequestAborted))
        {
            return ApiSupport.Conflict("repository_name_exists");
        }
        var before = new { repo.Name, repo.Description, repo.UserName, credentials = repo.PasswordEncrypted is not null };
        repo.Name = name;
        repo.Description = string.IsNullOrWhiteSpace(body.Description) ? null : body.Description.Trim();
        // Password: null = keep; "" with an empty user = remove the credentials; a value = replace.
        if (string.IsNullOrEmpty(body.UserName))
        {
            repo.UserName = null;
            repo.PasswordEncrypted = null;
            repo.CredentialUpdatedAt = time.GetUtcNow();
        }
        else
        {
            if (string.IsNullOrEmpty(body.Password) && repo.PasswordEncrypted is null)
            {
                return ApiSupport.BadRequest("invalid_repository_credentials", "Enter the password of the share user.");
            }
            repo.UserName = body.UserName;
            if (!string.IsNullOrEmpty(body.Password))
            {
                repo.PasswordEncrypted = BackupRules.Protect(keys, repo.Id, body.Password);
                repo.CredentialUpdatedAt = time.GetUtcNow();
            }
        }
        await db.SaveChangesAsync(http.RequestAborted);
        await http.WriteAuditAsync("backup.repository.update", resourceType: "backup_repository", resourceId: id.ToString("D"),
            details: new { before, after = new { repo.Name, repo.Description, repo.UserName, credentials = repo.PasswordEncrypted is not null } });
        return Results.Ok(RepoDto(repo, null, true, 0, 0));
    }

    private static async Task<IResult> DeleteRepositoryAsync(Guid id, HttpContext http, PortalDbContext db)
    {
        if (await http.GuardAsync(Permissions.BackupManage, ResourceRef.Global, "backup.repository.delete") is { } denied)
        {
            return denied;
        }
        var repo = await db.BackupRepositories.FirstOrDefaultAsync(r => r.Id == id, http.RequestAborted);
        if (repo is null)
        {
            return ApiSupport.NotFound();
        }
        if (await db.BackupJobs.AnyAsync(j => j.RepositoryId == id, http.RequestAborted))
        {
            return ApiSupport.Conflict("repository_in_use", "Backup jobs use this repository: change or delete them first.");
        }
        if (await db.BackupPoints.AnyAsync(p => p.RepositoryId == id && p.Status != BackupPointStatus.Deleted && p.Status != BackupPointStatus.Failed, http.RequestAborted))
        {
            return ApiSupport.Conflict("repository_has_points", "The repository holds restore points: delete them first (the files are never left without a record).");
        }
        db.BackupRepositories.Remove(repo);
        await db.BackupPoints.Where(p => p.RepositoryId == id).ExecuteDeleteAsync(http.RequestAborted);
        await db.SaveChangesAsync(http.RequestAborted);
        await http.WriteAuditAsync("backup.repository.delete", resourceType: "backup_repository", resourceId: id.ToString("D"), details: new { repo.Name, repo.Path });
        return Results.NoContent();
    }

    /// <summary>
    /// The host that checks or scans a repository: the given one, the repository's own host, or (shares) a connected host
    /// that can use it (with credentials: one with a backup staging folder), else the first connected host, so that the
    /// caller can say why it cannot.
    /// </summary>
    private static async Task<ManagedHost?> CheckingHostAsync(PortalDbContext db, IAgentDispatcher agents, BackupRepository repo, Guid? requested, CancellationToken ct)
    {
        var hosts = await db.Hosts.AsNoTracking().Where(h => h.Status == HostStatus.Active).OrderBy(h => h.DisplayName).ToListAsync(ct);
        if (hosts.FirstOrDefault(h => h.Id == (requested ?? repo.HostId)) is { } chosen)
        {
            return chosen;
        }
        if (repo.HostId is not null)
        {
            return null;
        }
        var connected = hosts.Where(h => agents.IsConnected(h.Id) && agents.ProtocolVersionOf(h.Id) >= 9).ToList();
        return connected.FirstOrDefault(h => BackupRules.CanUse(repo, h) is null) ?? connected.FirstOrDefault();
    }

    private static async Task<IResult> CheckRepositoryAsync(Guid id, HostRef body, HttpContext http, PortalDbContext db, IAgentDispatcher agents, KeyRing keys, TimeProvider time)
    {
        if (await http.GuardAsync(Permissions.BackupManage, ResourceRef.Global, "backup.repository.check") is { } denied)
        {
            return denied;
        }
        var repo = await db.BackupRepositories.FirstOrDefaultAsync(r => r.Id == id, http.RequestAborted);
        if (repo is null)
        {
            return ApiSupport.NotFound();
        }
        if (await CheckingHostAsync(db, agents, repo, body.HostId, http.RequestAborted) is not { } host)
        {
            return ApiSupport.Error(StatusCodes.Status503ServiceUnavailable, "host_unreachable", "No connected host (agent 0.7.0+) can check this repository.");
        }
        var (result, error) = await ReadinessAsync(db, agents, keys, host, repo, http.RequestAborted);
        if (error is not null)
        {
            return error;
        }
        repo.LastCheckedAt = time.GetUtcNow();
        repo.LastCheckHostId = host.Id;
        repo.LastCheckJson = JsonSerializer.Serialize(new { host = host.DisplayName, checks = result!.Checks }, AgentProtocol.Json);
        await db.SaveChangesAsync(http.RequestAborted);
        await http.WriteAuditAsync("backup.repository.check", resourceType: "backup_repository", resourceId: id.ToString("D"),
            details: new { host = host.DisplayName, critical = result.Checks.Count(c => c.Status == "Critical") });
        return Results.Ok(new { host = host.DisplayName, hostId = host.Id, checks = result.Checks });
    }

    /// <summary>GetBackupReadiness on a host, optionally for a repository; the VMs of the host are checked too.</summary>
    internal static async Task<(BackupReadiness? Result, IResult? Error)> ReadinessAsync(PortalDbContext db, IAgentDispatcher agents, KeyRing keys, ManagedHost host,
        BackupRepository? repo, CancellationToken ct)
    {
        if (!agents.IsConnected(host.Id))
        {
            return (null, ApiSupport.Error(StatusCodes.Status503ServiceUnavailable, "host_unreachable", $"The agent of {host.DisplayName} is not connected."));
        }
        if (agents.ProtocolVersionOf(host.Id) < 9)
        {
            return (null, ApiSupport.Error(StatusCodes.Status501NotImplemented, "agent_outdated", $"Backups need agent 0.7.0 or later on {host.DisplayName}. Update the agent."));
        }
        BackupTarget? target = null;
        if (repo is not null)
        {
            if (BackupRules.CanUse(repo, host) is { } refused)
            {
                return (new BackupReadiness([new BackupCheck("repository", "Critical", refused.Message, null)]), null);
            }
            try
            {
                target = BackupRules.Target(repo, host, keys);
            }
            catch (CryptographicException)
            {
                return (null, ApiSupport.Conflict("repository_credentials_unreadable", "The stored credentials of the repository cannot be decrypted: enter them again."));
            }
        }
        var vmIds = await db.VirtualMachines.AsNoTracking().Where(v => v.HostId == host.Id && !v.IsMissing).Select(v => v.HyperVId).Take(500).ToListAsync(ct);
        var (result, error) = await VmOperationsEndpoints.QueryAsync(agents, host.Id, HvOperation.GetBackupReadiness,
            new BackupReadinessRequest { Target = target, VmIds = vmIds }, null, ct, timeoutSeconds: 180);
        if (error is not null)
        {
            return (null, error);
        }
        var readiness = HvOperations.FromElement<BackupReadiness>(result) ?? new BackupReadiness([]);
        var checks = readiness.Checks.ToList();
        if (host.BackupStagingPath is null)
        {
            checks.Add(new BackupCheck("staging-folder", repo?.PasswordEncrypted is not null ? "Critical" : "Warning",
                "No backup staging folder is set for this host: needed for shares with credentials, restore tests and browsing files of shares.",
                "Host > Settings > Backup staging folder"));
        }
        return (new BackupReadiness(checks), null);
    }

    private static async Task<IResult> HostReadinessAsync(Guid id, Guid? repositoryId, HttpContext http, PortalDbContext db, AccessEvaluator access, IAgentDispatcher agents, KeyRing keys)
    {
        var uid = http.User.UserId();
        if (!await access.HasAsync(uid, Permissions.HostsManage, ResourceRef.Global, http.RequestAborted)
            && await http.GuardAsync(Permissions.BackupManage, ResourceRef.Global, "backup.readiness") is { } denied)
        {
            return denied;
        }
        var host = await db.Hosts.AsNoTracking().FirstOrDefaultAsync(h => h.Id == id && h.Status == HostStatus.Active, http.RequestAborted);
        if (host is null)
        {
            return ApiSupport.NotFound();
        }
        var repo = repositoryId is { } r ? await db.BackupRepositories.AsNoTracking().FirstOrDefaultAsync(x => x.Id == r, http.RequestAborted) : null;
        var (result, error) = await ReadinessAsync(db, agents, keys, host, repo, http.RequestAborted);
        return error ?? Results.Ok(new { host = host.DisplayName, checks = result!.Checks });
    }

    /// <summary>
    /// Restore points found in the repository and unknown to the portal (portal reinstalled, database restored) are added
    /// back, as Unverified: verify them before relying on them.
    /// </summary>
    private static async Task<IResult> RescanRepositoryAsync(Guid id, HostRef body, HttpContext http, PortalDbContext db, IAgentDispatcher agents, KeyRing keys, TimeProvider time)
    {
        if (await http.GuardAsync(Permissions.BackupManage, ResourceRef.Global, "backup.repository.rescan") is { } denied)
        {
            return denied;
        }
        var repo = await db.BackupRepositories.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, http.RequestAborted);
        if (repo is null)
        {
            return ApiSupport.NotFound();
        }
        if (await CheckingHostAsync(db, agents, repo, body.HostId, http.RequestAborted) is not { } host || !agents.IsConnected(host.Id)
            || agents.ProtocolVersionOf(host.Id) < 9)
        {
            return ApiSupport.Error(StatusCodes.Status503ServiceUnavailable, "host_unreachable", "No connected host (agent 0.7.0+) can read this repository.");
        }
        // Say why (for example: a share with credentials needs a backup staging folder on the host).
        if (BackupRules.CanUse(repo, host) is { } refused)
        {
            return ApiSupport.Conflict(refused.Code, refused.Message);
        }
        BackupTarget target;
        try
        {
            target = BackupRules.Target(repo, host, keys);
        }
        catch (CryptographicException)
        {
            return ApiSupport.Conflict("repository_credentials_unreadable", "The stored credentials of the repository cannot be decrypted: enter them again.");
        }
        var (result, error) = await VmOperationsEndpoints.QueryAsync(agents, host.Id, HvOperation.ListBackups, new BackupPointRequest { Target = target }, null,
            http.RequestAborted, timeoutSeconds: 300);
        if (error is not null)
        {
            return error;
        }
        var found = result is { ValueKind: JsonValueKind.Array } arr ? arr.EnumerateArray().ToList() : [];
        var known = (await db.BackupPoints.AsNoTracking().Where(p => p.RepositoryId == id).Select(p => p.Id).ToListAsync(http.RequestAborted)).ToHashSet();
        var vms = await db.VirtualMachines.AsNoTracking().Select(v => new { v.Id, v.HyperVId, v.HostId }).ToListAsync(http.RequestAborted);
        var added = 0;
        foreach (var item in found)
        {
            var folder = item.TryGetProperty("folder", out var f) ? f.GetString() : null;
            BackupManifest? m;
            try
            {
                m = item.TryGetProperty("manifest", out var me) ? me.Deserialize<BackupManifest>(AgentProtocol.Json) : null;
            }
            catch (JsonException)
            {
                continue;
            }
            if (m is null || folder is null || !OperationValidation.IsValidPointFolder(folder, m.PointId) || known.Contains(m.PointId))
            {
                continue;
            }
            // The manifest found on the share is not signed: whoever can write to the repository could have written it.
            // An imported point is therefore attached to NO portal VM (a new id, so its scope is global): only backup
            // administrators see and restore it, and only as a new VM; it can never replace an existing VM.
            var vm = vms.FirstOrDefault(v => v.HyperVId == m.VmId);
            db.BackupPoints.Add(new BackupPoint
            {
                Id = m.PointId, RepositoryId = id, VmId = Guid.NewGuid(), VmHyperVId = m.VmId, VmName = m.VmName ?? "?", HostId = vm?.HostId ?? host.Id,
                Folder = folder, Status = BackupPointStatus.Unverified, CreatedAt = m.CreatedUtc, CompletedAt = m.CompletedUtc, SizeBytes = m.TotalBytes,
                CheckpointType = m.CheckpointTypeRequested, CheckpointTypeUsed = m.CheckpointTypeUsed, ManifestJson = BackupRules.StoreManifest(m),
                Error = "Imported from the repository (not recorded by this portal): attached to no VM, restorable only as a new VM by backup administrators. "
                    + "Its manifest is not signed: check where it comes from, and verify it before relying on it.",
            });
            known.Add(m.PointId);
            added++;
        }
        await db.SaveChangesAsync(http.RequestAborted);
        await http.WriteAuditAsync("backup.repository.rescan", resourceType: "backup_repository", resourceId: id.ToString("D"),
            details: new { host = host.DisplayName, found = found.Count, added });
        return Results.Ok(new { found = found.Count, added, at = time.GetUtcNow() });
    }

    // ------------------------------------------------------------------ jobs

    private static async Task<IResult> JobsAsync(HttpContext http, PortalDbContext db, AccessEvaluator access)
    {
        if (await GuardSeeAsync(http, access, "backup.jobs.list") is { } denied)
        {
            return denied;
        }
        var jobs = await db.BackupJobs.AsNoTracking().OrderBy(j => j.Name).ToListAsync(http.RequestAborted);
        var targets = (await db.BackupJobTargets.AsNoTracking().ToListAsync(http.RequestAborted)).ToLookup(t => t.BackupJobId);
        var users = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.UserName, http.RequestAborted);
        var result = new List<object>();
        foreach (var j in jobs)
        {
            var last = await db.BackupRuns.AsNoTracking().Where(r => r.BackupJobId == j.Id).OrderByDescending(r => r.StartedAt).FirstOrDefaultAsync(http.RequestAborted);
            result.Add(JobDto(j, targets[j.Id], last, users.GetValueOrDefault(j.CreatedByUserId)));
        }
        return Results.Ok(result);
    }

    private static async Task<(string? Code, string? Detail)> ApplyJobBodyAsync(HttpContext http, PortalDbContext db, AccessEvaluator access, BackupJob job, JobBody b,
        CancellationToken ct)
    {
        var name = b.Name?.Trim() ?? "";
        if (!BackupRules.NameRegex().IsMatch(name)) return ("invalid_job_name", null);
        if (b.Description is { Length: > 500 }) return ("description_too_long", null);
        if (!await db.BackupRepositories.AnyAsync(r => r.Id == b.RepositoryId, ct)) return ("unknown_repository", null);
        var kind = b.ScheduleKind ?? "daily";
        if (!BackupRules.ScheduleKinds.Contains(kind)) return ("invalid_schedule", null);
        var times = (b.Times ?? []).Select(t => t.Trim()).Distinct().ToList();
        if (kind == "daily" && (times.Count is 0 or > 24 || times.Any(t => !BackupRules.IsValidTime(t)) || (b.DaysMask & 0x7F) == 0)) return ("invalid_schedule", "Choose at least one day and times as HH:mm.");
        if (kind == "hourly" && b.IntervalHours is < 1 or > 24) return ("invalid_schedule", "Every 1 to 24 hours.");
        var zone = string.IsNullOrWhiteSpace(b.TimeZoneId) ? "UTC" : b.TimeZoneId.Trim();
        if (!BackupRules.IsKnownZone(zone)) return ("invalid_time_zone", null);
        if (b.RetentionPoints is < 1 or > BackupRules.MaxRetentionPoints) return ("invalid_retention", null);
        if (b.RetentionDays is < 1 or > 3650) return ("invalid_retention", null);
        var checkpoint = b.CheckpointType ?? BackupCheckpointTypes.Production;
        if (!BackupCheckpointTypes.All.Contains(checkpoint)) return ("invalid_checkpoint_type", null);
        if (b.RestoreTestDays is < 0 or > 365) return ("invalid_restore_test", null);
        var notify = b.NotifyOn ?? "warning";
        if (!BackupRules.NotifyValues.Contains(notify)) return ("invalid_notify", null);
        var vmIds = (b.VmIds ?? []).Distinct().ToList();
        var groupIds = (b.GroupIds ?? []).Distinct().ToList();
        if (vmIds.Count + groupIds.Count is 0 or > 500) return ("invalid_targets", "Choose the VMs and/or groups to protect.");
        var uid = http.User.UserId();
        // Runs execute as the user who saves the job: they must be allowed to back up everything it protects.
        foreach (var v in vmIds)
        {
            if (!await db.VirtualMachines.AnyAsync(x => x.Id == v, ct) || !await access.HasAsync(uid, Permissions.BackupRun, ResourceRef.Vm(v), ct)) return ("target_not_allowed", "You may not back up one of the VMs.");
        }
        foreach (var g in groupIds)
        {
            if (!await db.LogicalGroups.AnyAsync(x => x.Id == g, ct) || !await access.HasAsync(uid, Permissions.BackupRun, ResourceRef.Group(g), ct)) return ("target_not_allowed", "You may not back up one of the groups.");
        }
        job.Name = name;
        job.Description = string.IsNullOrWhiteSpace(b.Description) ? null : b.Description.Trim();
        job.RepositoryId = b.RepositoryId;
        job.Enabled = b.Enabled;
        job.ScheduleKind = kind;
        job.DaysMask = b.DaysMask & 0x7F;
        job.TimesJson = JsonSerializer.Serialize(times);
        job.IntervalHours = Math.Clamp(b.IntervalHours, 1, 24);
        job.TimeZoneId = zone;
        job.RetentionPoints = b.RetentionPoints;
        job.RetentionDays = b.RetentionDays;
        job.CheckpointType = checkpoint;
        job.VerifyAfterCopy = b.VerifyAfterCopy;
        job.RestoreTestDays = b.RestoreTestDays;
        job.NotifyOn = notify;
        job.CreatedByUserId = uid;
        await db.BackupJobTargets.Where(t => t.BackupJobId == job.Id).ExecuteDeleteAsync(ct);
        db.BackupJobTargets.AddRange(vmIds.Select(v => new BackupJobTarget { BackupJobId = job.Id, Kind = "vm", TargetId = v }));
        db.BackupJobTargets.AddRange(groupIds.Select(g => new BackupJobTarget { BackupJobId = job.Id, Kind = "group", TargetId = g }));
        return (null, null);
    }

    private static async Task<IResult> CreateJobAsync(JobBody body, HttpContext http, PortalDbContext db, AccessEvaluator access, TimeProvider time)
    {
        if (await http.GuardAsync(Permissions.BackupManage, ResourceRef.Global, "backup.job.create") is { } denied)
        {
            return denied;
        }
        if (await db.BackupJobs.AnyAsync(j => j.Name == (body.Name ?? "").Trim(), http.RequestAborted))
        {
            return ApiSupport.Conflict("job_name_exists");
        }
        var job = new BackupJob { Name = "", CreatedAt = time.GetUtcNow() };
        if (await ApplyJobBodyAsync(http, db, access, job, body, http.RequestAborted) is { Code: { } code } e)
        {
            return ApiSupport.BadRequest(code, e.Detail);
        }
        job.NextRunAt = job.Enabled ? BackupRules.NextRun(job, time.GetUtcNow()) : null;
        db.BackupJobs.Add(job);
        await db.SaveChangesAsync(http.RequestAborted);
        await http.WriteAuditAsync("backup.job.create", resourceType: "backup_job", resourceId: job.Id.ToString("D"),
            details: new { job.Name, job.RepositoryId, job.ScheduleKind, job.RetentionPoints, vms = body.VmIds?.Length ?? 0, groups = body.GroupIds?.Length ?? 0 });
        return Results.Ok(new { job.Id, job.NextRunAt });
    }

    private static async Task<IResult> UpdateJobAsync(Guid id, JobBody body, HttpContext http, PortalDbContext db, AccessEvaluator access, TimeProvider time)
    {
        if (await http.GuardAsync(Permissions.BackupManage, ResourceRef.Global, "backup.job.update") is { } denied)
        {
            return denied;
        }
        var job = await db.BackupJobs.FirstOrDefaultAsync(j => j.Id == id, http.RequestAborted);
        if (job is null)
        {
            return ApiSupport.NotFound();
        }
        if (await db.BackupJobs.AnyAsync(j => j.Id != id && j.Name == (body.Name ?? "").Trim(), http.RequestAborted))
        {
            return ApiSupport.Conflict("job_name_exists");
        }
        var before = new { job.Name, job.Enabled, job.RepositoryId, job.ScheduleKind, job.RetentionPoints, job.RetentionDays };
        if (await ApplyJobBodyAsync(http, db, access, job, body, http.RequestAborted) is { Code: { } code } e)
        {
            return ApiSupport.BadRequest(code, e.Detail);
        }
        job.NextRunAt = job.Enabled ? BackupRules.NextRun(job, time.GetUtcNow()) : null;
        await db.SaveChangesAsync(http.RequestAborted);
        await http.WriteAuditAsync("backup.job.update", resourceType: "backup_job", resourceId: id.ToString("D"),
            details: new { before, after = new { job.Name, job.Enabled, job.RepositoryId, job.ScheduleKind, job.RetentionPoints, job.RetentionDays } });
        return Results.Ok(new { job.Id, job.NextRunAt });
    }

    private static async Task<IResult> DeleteJobAsync(Guid id, HttpContext http, PortalDbContext db)
    {
        if (await http.GuardAsync(Permissions.BackupManage, ResourceRef.Global, "backup.job.delete") is { } denied)
        {
            return denied;
        }
        var job = await db.BackupJobs.FirstOrDefaultAsync(j => j.Id == id, http.RequestAborted);
        if (job is null)
        {
            return ApiSupport.NotFound();
        }
        // Its restore points stay (they can still be restored or deleted one by one); no retention applies to them anymore.
        await db.BackupJobTargets.Where(t => t.BackupJobId == id).ExecuteDeleteAsync(http.RequestAborted);
        db.BackupJobs.Remove(job);
        await db.SaveChangesAsync(http.RequestAborted);
        await http.WriteAuditAsync("backup.job.delete", resourceType: "backup_job", resourceId: id.ToString("D"), details: new { job.Name });
        return Results.NoContent();
    }

    private static async Task<IResult> RunJobAsync(Guid id, HttpContext http, PortalDbContext db, AccessEvaluator access, BackupService service)
    {
        var uid = http.User.UserId();
        if (!await access.HasAsync(uid, Permissions.BackupManage, ResourceRef.Global, http.RequestAborted)
            && await http.GuardAsync(Permissions.BackupRun, ResourceRef.Global, "backup.job.run") is { } denied)
        {
            return denied;
        }
        var job = await db.BackupJobs.FirstOrDefaultAsync(j => j.Id == id, http.RequestAborted);
        if (job is null)
        {
            return ApiSupport.NotFound();
        }
        if (await db.BackupRuns.AnyAsync(r => r.BackupJobId == id && r.Status == BackupRunStatus.Running, http.RequestAborted))
        {
            return ApiSupport.Conflict("run_in_progress", "The job is already running.");
        }
        var run = await service.StartRunAsync(job, "manual", uid, http.RequestAborted);
        job.LastRunAt = run.StartedAt;
        await db.SaveChangesAsync(http.RequestAborted);
        await http.WriteAuditAsync("backup.job.run", resourceType: "backup_job", resourceId: id.ToString("D"), details: new { job.Name, runId = run.Id, vms = run.Total });
        return Results.Accepted($"/api/v1/backup/runs/{run.Id}", new { runId = run.Id, run.Total });
    }

    // ------------------------------------------------------------------ runs and points

    private static async Task<IResult> RunsAsync(Guid? jobId, int? limit, HttpContext http, PortalDbContext db, AccessEvaluator access)
    {
        if (await GuardSeeAsync(http, access, "backup.runs.list") is { } denied)
        {
            return denied;
        }
        var q = db.BackupRuns.AsNoTracking().AsQueryable();
        if (jobId is { } j)
        {
            q = q.Where(r => r.BackupJobId == j);
        }
        var runs = await q.OrderByDescending(r => r.StartedAt).Take(Math.Clamp(limit ?? 100, 1, 500)).ToListAsync(http.RequestAborted);
        var names = await db.BackupJobs.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, http.RequestAborted);
        return Results.Ok(runs.Select(r => RunDto(r, r.BackupJobId is { } id ? names.GetValueOrDefault(id) : null)));
    }

    private static async Task<IResult> RunAsync(Guid id, HttpContext http, PortalDbContext db, AccessEvaluator access)
    {
        if (await GuardSeeAsync(http, access, "backup.runs.get") is { } denied)
        {
            return denied;
        }
        var run = await db.BackupRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, http.RequestAborted);
        if (run is null)
        {
            return ApiSupport.NotFound();
        }
        var jobName = run.BackupJobId is { } j ? await db.BackupJobs.Where(x => x.Id == j).Select(x => x.Name).FirstOrDefaultAsync(http.RequestAborted) : null;
        var points = await db.BackupPoints.AsNoTracking().Where(p => p.RunId == id).OrderBy(p => p.VmName).ToListAsync(http.RequestAborted);
        var vmIds = (await db.VirtualMachines.AsNoTracking().Select(v => v.Id).ToListAsync(http.RequestAborted)).ToHashSet();
        return Results.Ok(new { run = RunDto(run, jobName), points = points.Select(p => PointDto(p, null, jobName, vmIds.Contains(p.VmId))) });
    }

    private static async Task<IResult> PointsAsync(Guid? vmId, Guid? repositoryId, Guid? jobId, HttpContext http, PortalDbContext db, AccessEvaluator access)
    {
        if (await GuardSeeAsync(http, access, "backup.points.list") is { } denied)
        {
            return denied;
        }
        var q = db.BackupPoints.AsNoTracking().Where(p => p.Status != BackupPointStatus.Deleted);
        if (vmId is { } v) q = q.Where(p => p.VmId == v);
        if (repositoryId is { } r) q = q.Where(p => p.RepositoryId == r);
        if (jobId is { } j) q = q.Where(p => p.BackupJobId == j);
        var points = await q.OrderByDescending(p => p.CreatedAt).Take(2000).ToListAsync(http.RequestAborted);
        var repos = await db.BackupRepositories.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, http.RequestAborted);
        var jobs = await db.BackupJobs.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, http.RequestAborted);
        var vmIds = (await db.VirtualMachines.AsNoTracking().Select(x => x.Id).ToListAsync(http.RequestAborted)).ToHashSet();
        var visible = await access.FilterVmsAsync(http.User.UserId(), Permissions.InventoryRead, points.Select(p => p.VmId).Where(vmIds.Contains).Distinct(), http.RequestAborted);
        // Points of VMs the user cannot see are left out; points of deleted VMs are shown to Global backup readers (checked above).
        return Results.Ok(points.Where(p => !vmIds.Contains(p.VmId) || visible.Contains(p.VmId))
            .Select(p => PointDto(p, repos.GetValueOrDefault(p.RepositoryId), p.BackupJobId is { } bj ? jobs.GetValueOrDefault(bj) : null, vmIds.Contains(p.VmId))));
    }

    private static async Task<IResult> PointAsync(Guid id, HttpContext http, PortalDbContext db, AccessEvaluator access)
    {
        var p = await db.BackupPoints.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, http.RequestAborted);
        if (p is null || !await CanReadPointAsync(http, db, access, p))
        {
            return ApiSupport.NotFound();
        }
        var repo = await db.BackupRepositories.AsNoTracking().Where(r => r.Id == p.RepositoryId).Select(r => r.Name).FirstOrDefaultAsync(http.RequestAborted);
        var job = p.BackupJobId is { } j ? await db.BackupJobs.AsNoTracking().Where(x => x.Id == j).Select(x => x.Name).FirstOrDefaultAsync(http.RequestAborted) : null;
        var manifest = p.ManifestJson is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(p.ManifestJson);
        return Results.Ok(new { point = PointDto(p, repo, job, await db.VirtualMachines.AnyAsync(v => v.Id == p.VmId, http.RequestAborted)), manifest });
    }

    private static async Task<IResult> VmBackupsAsync(Guid id, HttpContext http, PortalDbContext db, AccessEvaluator access)
    {
        var vm = await VmOperationsEndpoints.ReadableVmAsync(id, http, db, access);
        if (vm is null)
        {
            return ApiSupport.NotFound();
        }
        var uid = http.User.UserId();
        var r = ResourceRef.Vm(id);
        var canRun = await access.HasAsync(uid, Permissions.BackupRun, r, http.RequestAborted);
        var canRestore = await access.HasAsync(uid, Permissions.BackupRestore, r, http.RequestAborted);
        var canManage = await access.HasAsync(uid, Permissions.BackupManage, ResourceRef.Global, http.RequestAborted);
        if (!canRun && !canRestore && !canManage)
        {
            return Results.Ok(new { points = Array.Empty<object>(), canRun, canRestore, canManage, repositories = Array.Empty<object>(), allowed = false });
        }
        var points = await db.BackupPoints.AsNoTracking().Where(p => p.VmId == id && p.Status != BackupPointStatus.Deleted).OrderByDescending(p => p.CreatedAt)
            .Take(500).ToListAsync(http.RequestAborted);
        var repos = await db.BackupRepositories.AsNoTracking().ToListAsync(http.RequestAborted);
        var jobs = await db.BackupJobs.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, http.RequestAborted);
        var host = await db.Hosts.AsNoTracking().FirstAsync(h => h.Id == vm.HostId, http.RequestAborted);
        return Results.Ok(new
        {
            allowed = true, canRun, canRestore, canManage,
            points = points.Select(p => PointDto(p, repos.FirstOrDefault(x => x.Id == p.RepositoryId)?.Name, p.BackupJobId is { } j ? jobs.GetValueOrDefault(j) : null, true)),
            // Repositories usable for "back up now" from this VM's host.
            repositories = repos.Where(x => BackupRules.CanUse(x, host) is null).OrderBy(x => x.Name).Select(x => new { x.Id, x.Name, x.Path }),
        });
    }

    private static async Task<IResult> BackupNowAsync(Guid id, AdHocBody body, HttpContext http, PortalDbContext db, AccessEvaluator access, IAgentDispatcher agents,
        BackupService service)
    {
        var vm = await VmOperationsEndpoints.ReadableVmAsync(id, http, db, access);
        if (vm is null)
        {
            return ApiSupport.NotFound();
        }
        if (await http.GuardAsync(Permissions.BackupRun, ResourceRef.Vm(id), "backup.vm.request") is { } denied)
        {
            return denied;
        }
        if (vm.IsMissing)
        {
            return ApiSupport.Conflict("vm_missing");
        }
        var repo = await db.BackupRepositories.AsNoTracking().FirstOrDefaultAsync(r => r.Id == body.RepositoryId, http.RequestAborted);
        if (repo is null)
        {
            return ApiSupport.BadRequest("unknown_repository");
        }
        var host = await db.Hosts.AsNoTracking().FirstAsync(h => h.Id == vm.HostId, http.RequestAborted);
        if (BackupRules.CanUse(repo, host) is { } refused)
        {
            return ApiSupport.Conflict(refused.Code, refused.Message);
        }
        if (agents.IsConnected(vm.HostId) && agents.ProtocolVersionOf(vm.HostId) < 9)
        {
            return ApiSupport.Error(StatusCodes.Status501NotImplemented, "agent_outdated", "Backups need agent 0.7.0 or later on this host. Update the agent.");
        }
        var checkpoint = body.CheckpointType ?? BackupCheckpointTypes.Production;
        if (!BackupCheckpointTypes.All.Contains(checkpoint))
        {
            return ApiSupport.BadRequest("invalid_checkpoint_type");
        }
        if (await db.BackupPoints.AnyAsync(p => p.VmId == id && p.Status == BackupPointStatus.Creating, http.RequestAborted))
        {
            return ApiSupport.Conflict("backup_in_progress", "A backup of this VM is already in progress.");
        }
        var (run, point) = await service.StartAdHocAsync(vm, repo, checkpoint, body.VerifyAfterCopy ?? true, http.User.UserId(), http.RequestAborted);
        await http.WriteAuditAsync("backup.vm.request", resourceType: "vm", resourceId: id.ToString("D"),
            details: new { repository = repo.Name, runId = run.Id, pointId = point.Id, jobId = point.JobId, checkpoint });
        return Results.Accepted($"/api/v1/jobs/{point.JobId}", new { jobId = point.JobId, pointId = point.Id, runId = run.Id });
    }

    private static async Task<(BackupPoint? Point, BackupRepository? Repo, IResult? Error)> LoadPointAsync(Guid id, HttpContext http, PortalDbContext db, AccessEvaluator access)
    {
        var p = await db.BackupPoints.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, http.RequestAborted);
        if (p is null || !await CanReadPointAsync(http, db, access, p))
        {
            return (null, null, ApiSupport.NotFound());
        }
        var repo = await db.BackupRepositories.AsNoTracking().FirstOrDefaultAsync(r => r.Id == p.RepositoryId, http.RequestAborted);
        return repo is null ? (null, null, ApiSupport.Conflict("repository_not_found", "The repository of the restore point no longer exists.")) : (p, repo, null);
    }

    private static async Task<IResult> VerifyPointAsync(Guid id, HttpContext http, PortalDbContext db, AccessEvaluator access, BackupService service, JobService jobs)
    {
        var (p, repo, error) = await LoadPointAsync(id, http, db, access);
        if (error is not null)
        {
            return error;
        }
        if (await http.GuardAsync(Permissions.BackupRun, await PointScopeAsync(db, p!, http.RequestAborted), "backup.point.verify") is { } denied)
        {
            return denied;
        }
        if (p!.Status is not (BackupPointStatus.Ok or BackupPointStatus.Unverified))
        {
            return ApiSupport.Conflict("backup_point_not_usable", $"The restore point is {p.Status}.");
        }
        if (await service.HostForAsync(p, repo!, http.RequestAborted) is not { } host)
        {
            return ApiSupport.Error(StatusCodes.Status503ServiceUnavailable, "host_unreachable", "No connected host (agent 0.7.0+) can read the repository of this restore point.");
        }
        var args = new BackupPointRequest { Target = new BackupTarget { Path = repo!.Path }, Folder = p.Folder, PointId = p.Id };
        return await VmOperationsEndpoints.QueueAsync(http, jobs, new JobService.OperationJobSpec(JobService.BackupVerifyJobType, "Verify", ScopeType.Host, host.Id, p.VmName,
            host.Id, Permissions.BackupRun, new JobService.OperationParameters(HvOperations.ToElement(args), BackupPointId: p.Id), BackupService.BackupTimeoutSeconds,
            $"Verify restore point of {p.VmName} ({p.CreatedAt:u})"), null, "backup.point.verify", new { pointId = p.Id });
    }

    private static async Task<IResult> DeletePointAsync(Guid id, DeletePointBody body, HttpContext http, PortalDbContext db, AccessEvaluator access, BackupService service)
    {
        var (p, repo, error) = await LoadPointAsync(id, http, db, access);
        if (error is not null)
        {
            return error;
        }
        if (await http.GuardAsync(Permissions.BackupManage, ResourceRef.Global, "backup.point.delete") is { } denied)
        {
            return denied;
        }
        if (!string.Equals(body.ConfirmName, p!.VmName, StringComparison.Ordinal))
        {
            return ApiSupport.BadRequest("confirmation_required", "Type the name of the VM to confirm.");
        }
        if (p.Status is BackupPointStatus.Creating or BackupPointStatus.Deleting or BackupPointStatus.Deleted)
        {
            return ApiSupport.Conflict("backup_point_busy", $"The restore point is {p.Status}.");
        }
        var job = await service.QueueRemoveAsync(p, repo!, http.User.UserId(), "requested in the portal", http.RequestAborted);
        if (job is null)
        {
            return ApiSupport.Error(StatusCodes.Status503ServiceUnavailable, "host_unreachable", "No connected host (agent 0.7.0+) can reach the repository of this restore point.");
        }
        await http.WriteAuditAsync("backup.point.delete.request", resourceType: "vm", resourceId: p.VmId.ToString("D"), details: new { pointId = p.Id, jobId = job.Id, p.CreatedAt });
        return Results.Accepted($"/api/v1/jobs/{job.Id}", new { jobId = job.Id });
    }

    private static async Task<IResult> KeepPointAsync(Guid id, KeepBody body, HttpContext http, PortalDbContext db)
    {
        if (await http.GuardAsync(Permissions.BackupManage, ResourceRef.Global, "backup.point.keep") is { } denied)
        {
            return denied;
        }
        var p = await db.BackupPoints.FirstOrDefaultAsync(x => x.Id == id, http.RequestAborted);
        if (p is null)
        {
            return ApiSupport.NotFound();
        }
        p.Keep = body.Keep;
        await db.SaveChangesAsync(http.RequestAborted);
        await http.WriteAuditAsync("backup.point.keep", resourceType: "vm", resourceId: p.VmId.ToString("D"), details: new { pointId = id, body.Keep });
        return Results.Ok(new { p.Id, p.Keep });
    }

    private static async Task<IResult> RestoreAsync(Guid id, RestoreBody body, HttpContext http, PortalDbContext db, AccessEvaluator access, IAgentDispatcher agents,
        BackupService service)
    {
        var (p, repo, error) = await LoadPointAsync(id, http, db, access);
        if (error is not null)
        {
            return error;
        }
        var scope = await PointScopeAsync(db, p!, http.RequestAborted);
        if (await http.GuardAsync(Permissions.BackupRestore, scope, "backup.restore.request") is { } denied)
        {
            return denied;
        }
        if (p!.Status is not (BackupPointStatus.Ok or BackupPointStatus.Unverified))
        {
            return ApiSupport.Conflict("backup_point_not_usable", $"The restore point is {p.Status}.");
        }
        var mode = body.Mode ?? RestoreModes.NewVm;
        if (!RestoreModes.All.Contains(mode))
        {
            return ApiSupport.BadRequest("invalid_restore_mode");
        }
        var key = VmOperationsEndpoints.IdempotencyKey(http, body.IdempotencyKey);
        if (key == "!")
        {
            return ApiSupport.BadRequest("invalid_idempotency_key");
        }
        VirtualMachine? replace = null;
        ManagedHost? host;
        if (mode == RestoreModes.Replace)
        {
            replace = await db.VirtualMachines.AsNoTracking().FirstOrDefaultAsync(v => v.Id == p.VmId && !v.IsMissing, http.RequestAborted);
            if (replace is null || replace.HyperVId != p.VmHyperVId)
            {
                return ApiSupport.Conflict("vm_not_found", "The VM of this restore point no longer exists: restore it as a new VM.");
            }
            if (!string.Equals(body.ConfirmName, replace.Name, StringComparison.Ordinal))
            {
                return ApiSupport.BadRequest("confirmation_required", "Replacing discards the current state of the VM. Type the VM name to confirm.");
            }
            // Destructive: the password is confirmed again, as for the other critical actions.
            if (await http.RequireRecentReauthAsync() is { } reauth)
            {
                return reauth;
            }
            host = await db.Hosts.AsNoTracking().FirstAsync(h => h.Id == replace.HostId, http.RequestAborted);
        }
        else
        {
            host = await db.Hosts.AsNoTracking().FirstOrDefaultAsync(h => h.Id == (body.HostId ?? p.HostId) && h.Status == HostStatus.Active, http.RequestAborted);
            if (host is null)
            {
                return ApiSupport.BadRequest("invalid_destination_host");
            }
            if (await http.GuardAsync(Permissions.VmCreate, ResourceRef.Host(host.Id), "backup.restore.request") is { } deniedHost)
            {
                return deniedHost;
            }
        }
        if (BackupRules.CanUse(repo!, host) is { } refused)
        {
            return ApiSupport.Conflict(refused.Code, refused.Message);
        }
        if (!agents.IsConnected(host.Id))
        {
            return ApiSupport.Error(StatusCodes.Status503ServiceUnavailable, "host_unreachable", "The host agent is not connected.");
        }
        if (agents.ProtocolVersionOf(host.Id) < 9)
        {
            return ApiSupport.Error(StatusCodes.Status501NotImplemented, "agent_outdated", "Restores need agent 0.7.0 or later on this host. Update the agent.");
        }
        string? name = null, location = null;
        var network = mode == RestoreModes.Test ? RestoreNetworks.Disconnected : body.Network ?? RestoreNetworks.Same;
        if (mode != RestoreModes.Replace)
        {
            name = (body.Name ?? "").Trim();
            if (mode == RestoreModes.Test && name.Length == 0)
            {
                name = "hvp-test-" + p.Id.ToString("N")[..8];
            }
            if (!OperationValidation.IsValidVmName(name))
            {
                return ApiSupport.BadRequest("invalid_vm_name");
            }
            if (await db.VirtualMachines.AnyAsync(v => v.HostId == host.Id && !v.IsMissing && v.Name == name, http.RequestAborted))
            {
                return ApiSupport.Conflict("vm_name_exists", "A VM with this name already exists on the host.");
            }
            location = string.IsNullOrWhiteSpace(body.Location)
                ? (mode == RestoreModes.Test && host.BackupStagingPath is { } staging ? staging + @"\restore-tests" : null)
                : HostPaths.Normalize(body.Location);
            if (HostPaths.ValidateLocation(location) is { } badLocation)
            {
                return ApiSupport.BadRequest(badLocation);
            }
            if (location is not null && InventoryEndpoints.StorageFoldersOf(host) is { Count: > 0 } allowed && !StorageFolderRules.IsInside(location, allowed))
            {
                return ApiSupport.BadRequest("location_not_allowed", "The folder is outside the storage folders allowed on this host.");
            }
        }
        if (!RestoreNetworks.All.Contains(network) || (network == RestoreNetworks.Switch && !VmOperationsEndpoints.SwitchNames(host).Contains(body.SwitchName ?? "", StringComparer.Ordinal)))
        {
            return ApiSupport.BadRequest("invalid_restore_network");
        }
        var startAfter = mode != RestoreModes.Test && body.StartAfter;
        try
        {
            var (job, created) = await service.QueueRestoreAsync(p, host, mode, name, location, network, body.SwitchName, startAfter, replace, http.User.UserId(),
                mode switch
                {
                    RestoreModes.Replace => $"Restore {p.VmName} over itself from {p.CreatedAt:u}",
                    RestoreModes.Test => $"Restore test of {p.VmName} ({p.CreatedAt:u}) on {host.DisplayName}",
                    _ => $"Restore {p.VmName} ({p.CreatedAt:u}) as {name} on {host.DisplayName}",
                }, http.RequestAborted, key);
            if (created)
            {
                await http.WriteAuditAsync("backup.restore.request", resourceType: "vm", resourceId: p.VmId.ToString("D"),
                    details: new { pointId = p.Id, jobId = job.Id, mode, host = host.DisplayName, name, location, network });
            }
            return Results.Accepted($"/api/v1/jobs/{job.Id}", new { jobId = job.Id, created });
        }
        catch (IdempotencyConflictException ex)
        {
            return ApiSupport.Conflict("idempotency_conflict", ex.Message);
        }
    }

    private static async Task<IResult> RestoreDiskAsync(Guid id, RestoreDiskBody body, HttpContext http, PortalDbContext db, AccessEvaluator access, IAgentDispatcher agents,
        JobService jobs)
    {
        var (p, repo, error) = await LoadPointAsync(id, http, db, access);
        if (error is not null)
        {
            return error;
        }
        if (await http.GuardAsync(Permissions.BackupRestore, await PointScopeAsync(db, p!, http.RequestAborted), "backup.restore_disk.request") is { } denied)
        {
            return denied;
        }
        var disk = body.Disk ?? "";
        if (!BackupRules.Disks(p!).Contains(disk, StringComparer.OrdinalIgnoreCase))
        {
            return ApiSupport.BadRequest("invalid_disk", "The disk is not part of the restore point.");
        }
        VirtualMachine? attach = null;
        if (body.AttachVmId is { } attachId)
        {
            attach = await db.VirtualMachines.AsNoTracking().FirstOrDefaultAsync(v => v.Id == attachId && !v.IsMissing, http.RequestAborted);
            if (attach is null)
            {
                return ApiSupport.BadRequest("invalid_vm");
            }
            if (await http.GuardAsync(Permissions.VmConfigStorage, ResourceRef.Vm(attachId), "backup.restore_disk.request") is { } deniedAttach)
            {
                return deniedAttach;
            }
        }
        var hostId = attach?.HostId ?? body.HostId ?? p!.HostId;
        var host = await db.Hosts.AsNoTracking().FirstOrDefaultAsync(h => h.Id == hostId && h.Status == HostStatus.Active, http.RequestAborted);
        if (host is null)
        {
            return ApiSupport.BadRequest("invalid_destination_host");
        }
        if (attach is null && await http.GuardAsync(Permissions.HostFilesWrite, ResourceRef.Host(host.Id), "backup.restore_disk.request") is { } deniedFiles)
        {
            return deniedFiles;
        }
        if (BackupRules.CanUse(repo!, host) is { } refused)
        {
            return ApiSupport.Conflict(refused.Code, refused.Message);
        }
        if (!agents.IsConnected(host.Id) || agents.ProtocolVersionOf(host.Id) < 9)
        {
            return ApiSupport.Error(StatusCodes.Status503ServiceUnavailable, "host_unreachable", "The host agent (0.7.0+) is not connected.");
        }
        var folder = string.IsNullOrWhiteSpace(body.DestinationFolder) ? null : HostPaths.Normalize(body.DestinationFolder);
        if (folder is null || HostPaths.ValidateLocation(folder) is not null)
        {
            return ApiSupport.BadRequest("invalid_location", @"A local folder of the host such as D:\Restored.");
        }
        if (InventoryEndpoints.StorageFoldersOf(host) is { Count: > 0 } allowed && !StorageFolderRules.IsInside(folder, allowed))
        {
            return ApiSupport.BadRequest("location_not_allowed", "The folder is outside the storage folders allowed on this host.");
        }
        var args = new RestoreDiskRequest
        {
            Source = new BackupTarget { Path = repo!.Path }, Folder = p!.Folder, PointId = p.Id, Disk = disk, DestinationFolder = folder, AttachVmId = attach?.HyperVId,
        };
        return await VmOperationsEndpoints.QueueAsync(http, jobs, new JobService.OperationJobSpec(JobService.RestoreDiskJobType, "Disk", ScopeType.Host, host.Id, p.VmName,
            host.Id, Permissions.BackupRestore, new JobService.OperationParameters(HvOperations.ToElement(args), BackupPointId: p.Id), BackupService.BackupTimeoutSeconds,
            $"Restore disk {disk} of {p.VmName} ({p.CreatedAt:u}) to {folder}{(attach is null ? "" : " and attach it to " + attach.Name)}"), null,
            "backup.restore_disk.request", new { pointId = p.Id, disk, folder, attachVm = attach?.Name });
    }
}
