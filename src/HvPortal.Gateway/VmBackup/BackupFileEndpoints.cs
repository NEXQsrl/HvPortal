using System.Collections.Concurrent;
using System.Security.Cryptography;
using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Agents;
using HvPortal.Gateway.Api;
using HvPortal.Gateway.Audit;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Crypto;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Security;
using Microsoft.EntityFrameworkCore;

namespace HvPortal.Gateway.VmBackup;

/// <summary>
/// File-level restore (ADR-031): a disk of a restore point is attached read-only to a host (agent installed on the host),
/// its volumes are browsed and single files downloaded through the portal, then the disk is detached (by the user, or
/// by the agent after 2 hours without use). Only the user who attached the disk can browse it. Every download is audited.
/// The guest's file permissions apply: files the agent account may not read are reported as such.
/// </summary>
public static class BackupFileEndpoints
{
    public sealed record MountBody(string? Disk, Guid? HostId);

    private sealed record Mount(Guid MountId, Guid HostId, Guid PointId, Guid UserId, string VmName, string Disk, IReadOnlyList<MountedVolume> Volumes, DateTimeOffset CreatedUtc);

    private static readonly ConcurrentDictionary<Guid, Mount> Mounts = new();

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/backup").WithTags("Backup");
        g.MapPost("/points/{id:guid}/mount", MountAsync);
        g.MapGet("/mounts", ListMounts);
        g.MapGet("/mounts/{mountId:guid}/files", FilesAsync);
        g.MapGet("/mounts/{mountId:guid}/download", DownloadAsync);
        g.MapPost("/mounts/{mountId:guid}/dismount", DismountAsync);
    }

    private static async Task<IResult> MountAsync(Guid id, MountBody body, HttpContext http, PortalDbContext db, AccessEvaluator access, IAgentDispatcher agents,
        KeyRing keys, TimeProvider time)
    {
        var p = await db.BackupPoints.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, http.RequestAborted);
        if (p is null)
        {
            return ApiSupport.NotFound();
        }
        var scope = await BackupEndpoints.PointScopeAsync(db, p, http.RequestAborted);
        if (scope.Type == ScopeType.Vm && !await access.HasAsync(http.User.UserId(), Permissions.InventoryRead, scope, http.RequestAborted))
        {
            return ApiSupport.NotFound();
        }
        if (await http.GuardAsync(Permissions.BackupRestore, scope, "backup.files.mount") is { } denied)
        {
            return denied;
        }
        var disk = body.Disk ?? "";
        if (!BackupRules.Disks(p).Contains(disk, StringComparer.OrdinalIgnoreCase))
        {
            return ApiSupport.BadRequest("invalid_disk", "The disk is not part of the restore point.");
        }
        if (p.Status is not (BackupPointStatus.Ok or BackupPointStatus.Unverified))
        {
            return ApiSupport.Conflict("backup_point_not_usable", $"The restore point is {p.Status}.");
        }
        var repo = await db.BackupRepositories.AsNoTracking().FirstOrDefaultAsync(r => r.Id == p.RepositoryId, http.RequestAborted);
        var host = await db.Hosts.AsNoTracking().FirstOrDefaultAsync(h => h.Id == (body.HostId ?? p.HostId) && h.Status == HostStatus.Active, http.RequestAborted);
        if (repo is null || host is null)
        {
            return ApiSupport.Conflict("repository_not_found");
        }
        if (host.ConnectionMode != HostConnectionMode.Agent)
        {
            return ApiSupport.Conflict("not_supported", "Browsing backup files needs a host with its own agent. Restore the disk to a VM instead.");
        }
        // Another host than the one of the VM: attaching a disk there (and staging its copy) needs write access to the
        // files of that host, as uploading a file would.
        if (host.Id != p.HostId && await http.GuardAsync(Permissions.HostFilesWrite, ResourceRef.Host(host.Id), "backup.files.mount") is { } deniedHost)
        {
            return deniedHost;
        }
        if (BackupRules.CanUse(repo, host) is { } refused)
        {
            return ApiSupport.Conflict(refused.Code, refused.Message);
        }
        if (!agents.IsConnected(host.Id) || agents.ProtocolVersionOf(host.Id) < 9)
        {
            return ApiSupport.Error(StatusCodes.Status503ServiceUnavailable, "host_unreachable", "The host agent (0.7.0+) is not connected.");
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
        // A disk on a share opened with credentials is copied to the staging folder first: this can take a while.
        var (result, error) = await VmOperationsEndpoints.QueryAsync(agents, host.Id, HvOperation.MountBackupDisk,
            new MountBackupDiskRequest { Source = target, Folder = p.Folder, PointId = p.Id, Disk = disk }, null, http.RequestAborted, timeoutSeconds: 3600);
        if (error is not null)
        {
            await http.WriteAuditAsync("backup.files.mount", AuditOutcome.Failure, "vm", p.VmId.ToString("D"), new { pointId = p.Id, disk, host = host.DisplayName });
            return error;
        }
        var mount = HvOperations.FromElement<BackupMount>(result)!;
        Mounts[mount.MountId] = new Mount(mount.MountId, host.Id, p.Id, http.User.UserId(), p.VmName, disk, mount.Volumes, time.GetUtcNow());
        await http.WriteAuditAsync("backup.files.mount", resourceType: "vm", resourceId: p.VmId.ToString("D"),
            details: new { pointId = p.Id, disk, host = host.DisplayName, mountId = mount.MountId, volumes = mount.Volumes.Select(v => v.Root) });
        return Results.Ok(new { mount.MountId, hostId = host.Id, mount.Volumes, mount.ExpiresUtc, mount.Staged });
    }

    // Not "Task<IResult> (HttpContext)": that shape is taken as a RequestDelegate and its result is discarded (ASP0016).
    private static IResult ListMounts(HttpContext http)
    {
        var uid = http.User.UserId();
        return Results.Ok(Mounts.Values.Where(m => m.UserId == uid).Select(m => new { m.MountId, m.HostId, m.PointId, m.VmName, m.Disk, m.Volumes, m.CreatedUtc }));
    }

    /// <summary>The mount of this user, with the restore permission still held (checked at every call).</summary>
    private static async Task<(Mount? Mount, IResult? Error)> OwnMountAsync(Guid mountId, HttpContext http, PortalDbContext db, AccessEvaluator access, string action)
    {
        if (!Mounts.TryGetValue(mountId, out var m) || m.UserId != http.User.UserId())
        {
            return (null, ApiSupport.NotFound());
        }
        var point = await db.BackupPoints.AsNoTracking().FirstOrDefaultAsync(p => p.Id == m.PointId, http.RequestAborted);
        var scope = point is null ? ResourceRef.Global : await BackupEndpoints.PointScopeAsync(db, point, http.RequestAborted);
        if (await http.GuardAsync(Permissions.BackupRestore, scope, action) is { } denied)
        {
            return (null, denied);
        }
        return (m, null);
    }

    private static bool InsideMount(Mount m, string path) =>
        m.Volumes.Any(v => GroupPolicyServiceIsInside(path, v.Root));

    private static bool GroupPolicyServiceIsInside(string path, string root) => Quotas.GroupPolicyService.IsInside(HostPaths.Normalize(path), HostPaths.Normalize(root));

    private static async Task<IResult> FilesAsync(Guid mountId, string? path, HttpContext http, PortalDbContext db, AccessEvaluator access, IAgentDispatcher agents)
    {
        var (m, error) = await OwnMountAsync(mountId, http, db, access, "backup.files.list");
        if (error is not null)
        {
            return error;
        }
        if (string.IsNullOrWhiteSpace(path))
        {
            return Results.Ok(new FolderListing { Folders = m!.Volumes.Select(v => new FolderEntry(v.Label is { Length: > 0 } l ? $"{v.Root} ({l})" : v.Root, v.Root)).ToList() });
        }
        var normalized = HostPaths.Normalize(path);
        if (!HostPaths.IsValidLocalFolder(normalized) || !InsideMount(m!, normalized))
        {
            return ApiSupport.BadRequest("invalid_path");
        }
        var (outcome, failed) = await HostFileEndpoints.SendAsync(agents, m!.HostId, HvOperation.ListBackupFiles, new BackupFileListRequest(mountId, normalized), http.RequestAborted);
        return failed ?? Results.Ok(outcome!.Result);
    }

    private static async Task DownloadAsync(Guid mountId, string? path, HttpContext http, PortalDbContext db, AccessEvaluator access, IAgentDispatcher agents)
    {
        var (m, error) = await OwnMountAsync(mountId, http, db, access, "backup.files.download");
        if (error is not null)
        {
            await error.ExecuteAsync(http);
            return;
        }
        if (path is null || !HostPaths.IsValidFilePath(path) || !InsideMount(m!, path))
        {
            await ApiSupport.BadRequest("invalid_path").ExecuteAsync(http);
            return;
        }
        var ct = http.RequestAborted;
        var (first, firstError) = await HostFileEndpoints.SendAsync(agents, m!.HostId, HvOperation.ReadBackupFile, new BackupFileReadRequest(mountId, path, 0, FileTransfer.ChunkBytes), ct);
        if (firstError is not null)
        {
            await http.WriteAuditAsync("backup.files.download", AuditOutcome.Failure, "backup_point", m.PointId.ToString("D"), new { path });
            await firstError.ExecuteAsync(http);
            return;
        }
        var chunk = HvOperations.FromElement<FileChunk>(first!.Result)!;
        var total = chunk.TotalBytes;
        var name = Path.GetFileName(path);
        http.Response.ContentType = "application/octet-stream";
        http.Response.ContentLength = total;
        http.Response.Headers.ContentDisposition = $"attachment; filename=\"{HostFileEndpoints.AsciiName(name)}\"; filename*=UTF-8''{Uri.EscapeDataString(name)}";
        http.Response.Headers.CacheControl = "no-store";
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long sent = 0;
        try
        {
            while (true)
            {
                var bytes = Convert.FromBase64String(chunk.Data);
                if (chunk.Offset != sent || chunk.TotalBytes != total)
                {
                    throw new InvalidOperationException("The file changed while it was being downloaded.");
                }
                sha.AppendData(bytes);
                await http.Response.Body.WriteAsync(bytes, ct);
                sent += bytes.Length;
                if (chunk.Eof || sent >= total)
                {
                    break;
                }
                var (next, nextError) = await HostFileEndpoints.SendAsync(agents, m.HostId, HvOperation.ReadBackupFile, new BackupFileReadRequest(mountId, path, sent, FileTransfer.ChunkBytes), ct);
                if (nextError is not null || next is null)
                {
                    throw new InvalidOperationException("Reading the file on the host failed.");
                }
                chunk = HvOperations.FromElement<FileChunk>(next.Result)!;
            }
            await http.WriteAuditAsync("backup.files.download", AuditOutcome.Success, "backup_point", m.PointId.ToString("D"),
                new { vm = m.VmName, path, bytes = sent, sha256 = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant() });
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or OperationCanceledException or IOException)
        {
            await http.WriteAuditAsync("backup.files.download", AuditOutcome.Failure, "backup_point", m.PointId.ToString("D"), new { path, bytes = sent, reason = ex.Message });
            http.Abort();
        }
    }

    private static async Task<IResult> DismountAsync(Guid mountId, HttpContext http, IAgentDispatcher agents)
    {
        if (!Mounts.TryGetValue(mountId, out var m) || m.UserId != http.User.UserId())
        {
            return ApiSupport.NotFound();
        }
        var (_, error) = await HostFileEndpoints.SendAsync(agents, m.HostId, HvOperation.DismountBackupDisk, new BackupMountRef(mountId), http.RequestAborted, timeoutSeconds: 300);
        if (error is not null)
        {
            return error;
        }
        Mounts.TryRemove(mountId, out _);
        await http.WriteAuditAsync("backup.files.dismount", resourceType: "backup_point", resourceId: m.PointId.ToString("D"), details: new { mountId, m.Disk });
        return Results.NoContent();
    }
}
