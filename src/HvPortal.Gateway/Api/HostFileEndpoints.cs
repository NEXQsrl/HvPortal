using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using HvPortal.Contracts.Agent;
using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Agents;
using HvPortal.Gateway.Audit;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Quotas;
using HvPortal.Gateway.Security;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

namespace HvPortal.Gateway.Api;

/// <summary>
/// Host file explorer (agent 0.3.3+, ADR-018): browse folders, download and upload files. Every call maps to a typed
/// agent operation with the same folder rules as the folder browser (system/hidden/linked excluded, checked by gateway
/// and agent). Transfers are relayed in 4 MB chunks without being stored on the gateway; uploads are verified end to
/// end with SHA-256 before the file appears on the host. Downloads and uploads are audited.
/// </summary>
public static class HostFileEndpoints
{
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/hosts/{id:guid}/files").WithTags("Host files");
        g.MapGet("/", ListAsync);
        g.MapGet("/download", DownloadAsync);
        g.MapPost("/upload", UploadAsync);
    }

    /// <summary>
    /// Host scope (permission on the host): every folder that is not a system folder. Folder scope (<paramref name="groupId"/>,
    /// ADR-024): the permission is held on a VM folder whose limits open this host, and only the storage paths of that
    /// folder are reachable; they are returned as Roots and every path must be inside one of them.
    /// </summary>
    private static async Task<(IResult? Error, List<string>? Roots)> CheckAsync(Guid id, Guid? groupId, string permission, string action, HttpContext http,
        PortalDbContext db, AccessEvaluator access, IAgentDispatcher agents, GroupPolicyService policies)
    {
        var ct = http.RequestAborted;
        var uid = http.User.UserId();
        var host = await db.Hosts.AsNoTracking().FirstOrDefaultAsync(h => h.Id == id && h.Status == HostStatus.Active, ct);
        List<string>? roots = null;
        if (host is not null && groupId is { } g)
        {
            if (!await db.LogicalGroups.AnyAsync(x => x.Id == g, ct) || !await access.HasAsync(uid, Permissions.InventoryRead, ResourceRef.Group(g), ct))
            {
                return (ApiSupport.NotFound(), null);
            }
            if (!await policies.HoldsViaGroupAsync(uid, permission, g, id, ct))
            {
                await http.WriteAuditAsync(action, AuditOutcome.Denied, "host", id.ToString("D"), new { groupId = g, permission });
                return (ApiSupport.Forbidden("forbidden", $"Missing permission '{permission}' on the folder, or the folder is not enabled on this host."), null);
            }
            roots = GroupPolicyService.BrowsableRoots(await policies.ChainAsync(g, ct), id);
            if (roots.Count == 0)
            {
                return (ApiSupport.Forbidden("storage_not_allowed", "The folder has no storage path on this host."), null);
            }
        }
        else
        {
            if (host is null || !await access.HasAsync(uid, Permissions.InventoryRead, ResourceRef.Host(id), ct))
            {
                return (ApiSupport.NotFound(), null);
            }
            if (await http.GuardAsync(permission, ResourceRef.Host(id), action) is { } denied)
            {
                return (denied, null);
            }
        }
        if (!agents.IsConnected(id))
        {
            return (ApiSupport.Error(StatusCodes.Status503ServiceUnavailable, "host_unreachable", "The host agent is not connected."), null);
        }
        if (agents.ProtocolVersionOf(id) < 5)
        {
            return (ApiSupport.Error(StatusCodes.Status501NotImplemented, "agent_outdated", "The file explorer needs agent 0.3.3 or later on this host. Update the agent."), null);
        }
        return (null, roots);
    }

    private static async Task<IResult?> OutsideRootsAsync(HttpContext http, string action, Guid hostId, List<string>? roots, string path)
    {
        if (roots is null || roots.Any(r => GroupPolicyService.IsInside(path, r)))
        {
            return null;
        }
        await http.WriteAuditAsync(action, AuditOutcome.Denied, "host", hostId.ToString("D"), new { path, reason = "outside the folder's storage" });
        return ApiSupport.Forbidden("storage_not_allowed", "The path is outside the storage paths of the folder.");
    }

    private static async Task<(HvOperationOutcome? Outcome, IResult? Error)> SendAsync(IAgentDispatcher agents, Guid hostId, HvOperation op, object args, CancellationToken ct)
    {
        try
        {
            var r = await agents.SendAsync(hostId, new AgentCommand
            {
                CommandId = Guid.NewGuid(),
                Kind = AgentCommandKind.HyperVOperation,
                Operation = op,
                OperationArgs = HvOperations.ToElement(args),
                TimeoutSeconds = 120,
            }, null, TimeSpan.FromSeconds(135), ct);
            if (r.Operation is { Ok: true } ok)
            {
                return (ok, null);
            }
            return (null, MapError(r.Operation?.Error ?? r.Message ?? r.Status.ToString()));
        }
        catch (HostNotConnectedException)
        {
            return (null, ApiSupport.Error(StatusCodes.Status503ServiceUnavailable, "host_unreachable", "The host agent is not connected."));
        }
        catch (Exception ex) when (ex is TimeoutException or AgentChannelLostException)
        {
            return (null, ApiSupport.Error(StatusCodes.Status504GatewayTimeout, "host_timeout", ex.Message));
        }
    }

    /// <summary>The agent prefixes file errors with a code ("file_exists: ...").</summary>
    internal static IResult MapError(string message)
    {
        var colon = message.IndexOf(':', StringComparison.Ordinal);
        var code = colon > 0 && message[..colon].All(c => c is '_' or (>= 'a' and <= 'z')) ? message[..colon] : "host_operation_failed";
        var detail = colon > 0 && code != "host_operation_failed" ? message[(colon + 1)..].Trim() : message;
        var status = code switch
        {
            "file_exists" => StatusCodes.Status409Conflict,
            "file_in_use" => StatusCodes.Status409Conflict,
            "not_found" => StatusCodes.Status404NotFound,
            "invalid_path" or "invalid_file_name" or "location_not_allowed" or "invalid_size" or "invalid_range" => StatusCodes.Status400BadRequest,
            "insufficient_space" => StatusCodes.Status507InsufficientStorage,
            "access_denied" => StatusCodes.Status403Forbidden,
            "too_many_uploads" => StatusCodes.Status429TooManyRequests,
            _ => StatusCodes.Status502BadGateway,
        };
        return ApiSupport.Error(status, code, detail);
    }

    private static string? ValidateFolder(string? path) =>
        path is null || !HostPaths.IsValidLocalFolder(HostPaths.Normalize(path)) ? "invalid_path"
        : HostPaths.IsBlocked(HostPaths.Normalize(path)) ? "location_not_allowed" : null;

    private static async Task<IResult> ListAsync(Guid id, string? path, Guid? groupId, HttpContext http, PortalDbContext db, AccessEvaluator access,
        IAgentDispatcher agents, GroupPolicyService policies)
    {
        var (error, roots) = await CheckAsync(id, groupId, Permissions.HostFilesRead, "host.files.list", http, db, access, agents, policies);
        if (error is not null)
        {
            return error;
        }
        if (string.IsNullOrWhiteSpace(path) && roots is not null)
        {
            // Folder scope: the starting points are the storage paths of the folder, not the drives of the host.
            return Results.Ok(new FolderListing { Folders = roots.Select(r => new FolderEntry(r, r)).ToList() });
        }
        if (string.IsNullOrWhiteSpace(path))
        {
            var (drives, e1) = await SendAsync(agents, id, HvOperation.BrowseFolders, new FolderBrowseRequest(null, false), http.RequestAborted);
            return e1 ?? Results.Ok(drives!.Result);
        }
        if (ValidateFolder(path) is { } invalid)
        {
            return ApiSupport.BadRequest(invalid);
        }
        if (await OutsideRootsAsync(http, "host.files.list", id, roots, HostPaths.Normalize(path)) is { } outside)
        {
            return outside;
        }
        var (listing, e2) = await SendAsync(agents, id, HvOperation.ListFolder, new FileListRequest(HostPaths.Normalize(path)), http.RequestAborted);
        return e2 ?? Results.Ok(listing!.Result);
    }

    private static async Task DownloadAsync(Guid id, string? path, Guid? groupId, HttpContext http, PortalDbContext db, AccessEvaluator access,
        IAgentDispatcher agents, GroupPolicyService policies)
    {
        var (error, roots) = await CheckAsync(id, groupId, Permissions.HostFilesRead, "host.files.download", http, db, access, agents, policies);
        if (error is not null)
        {
            await error.ExecuteAsync(http);
            return;
        }
        if (path is null || !HostPaths.IsValidFilePath(path) || HostPaths.IsBlocked(path))
        {
            await ApiSupport.BadRequest(path is not null && HostPaths.IsValidFilePath(path) ? "location_not_allowed" : "invalid_path").ExecuteAsync(http);
            return;
        }
        if (await OutsideRootsAsync(http, "host.files.download", id, roots, path) is { } outside)
        {
            await outside.ExecuteAsync(http);
            return;
        }
        var ct = http.RequestAborted;
        var (first, firstError) = await SendAsync(agents, id, HvOperation.ReadFile, new FileReadRequest(path, 0, FileTransfer.ChunkBytes), ct);
        if (firstError is not null)
        {
            await http.WriteAuditAsync("host.files.download", AuditOutcome.Failure, "host", id.ToString("D"), new { path });
            await firstError.ExecuteAsync(http);
            return;
        }
        var chunk = HvOperations.FromElement<FileChunk>(first!.Result)!;
        var total = chunk.TotalBytes;
        var name = Path.GetFileName(path);
        http.Response.ContentType = "application/octet-stream";
        http.Response.ContentLength = total;
        http.Response.Headers.ContentDisposition = $"attachment; filename=\"{AsciiName(name)}\"; filename*=UTF-8''{Uri.EscapeDataString(name)}";
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
                var (next, nextError) = await SendAsync(agents, id, HvOperation.ReadFile, new FileReadRequest(path, sent, FileTransfer.ChunkBytes), ct);
                if (nextError is not null || next is null)
                {
                    throw new InvalidOperationException("Reading the file on the host failed.");
                }
                chunk = HvOperations.FromElement<FileChunk>(next.Result)!;
            }
            await http.WriteAuditAsync("host.files.download", AuditOutcome.Success, "host", id.ToString("D"),
                new { path, bytes = sent, sha256 = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant() });
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or OperationCanceledException or IOException)
        {
            await http.WriteAuditAsync("host.files.download", AuditOutcome.Failure, "host", id.ToString("D"), new { path, bytes = sent, reason = ex.Message });
            // Headers are sent: abort so the browser reports a failed download instead of a truncated file.
            http.Abort();
        }
    }

    private static string AsciiName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(c is >= ' ' and <= '~' and not '"' and not '\\' ? c : '_');
        }
        return sb.ToString();
    }

    private static async Task<IResult> UploadAsync(Guid id, string? folder, string? name, bool? overwrite, Guid? groupId, HttpContext http, PortalDbContext db,
        AccessEvaluator access, IAgentDispatcher agents, GroupPolicyService policies)
    {
        var (error, roots) = await CheckAsync(id, groupId, Permissions.HostFilesWrite, "host.files.upload", http, db, access, agents, policies);
        if (error is not null)
        {
            return error;
        }
        var target = folder is null ? null : HostPaths.Normalize(folder);
        var size = http.Request.ContentLength;
        var request = new FileWriteBeginRequest(target ?? "", name ?? "", size ?? -1, overwrite == true);
        if (size is null)
        {
            return ApiSupport.Error(StatusCodes.Status411LengthRequired, "length_required", "Content-Length is required.");
        }
        if (OperationValidation.Validate(request) is { } invalid)
        {
            return ApiSupport.BadRequest(invalid);
        }
        if (await OutsideRootsAsync(http, "host.files.upload", id, roots, target!) is { } outside)
        {
            return outside;
        }
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = size;
        }
        var ct = http.RequestAborted;
        var audit = new { folder = target, name, size, overwrite = overwrite == true };
        var (begin, beginError) = await SendAsync(agents, id, HvOperation.WriteFileBegin, request, ct);
        if (beginError is not null)
        {
            await http.WriteAuditAsync("host.files.upload", AuditOutcome.Failure, "host", id.ToString("D"), audit);
            return beginError;
        }
        var started = HvOperations.FromElement<FileWriteBeginResult>(begin!.Result)!;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(FileTransfer.ChunkBytes);
        long offset = 0;
        try
        {
            while (offset < size)
            {
                var want = (int)Math.Min(FileTransfer.ChunkBytes, size.Value - offset);
                var read = await http.Request.Body.ReadAtLeastAsync(buffer.AsMemory(0, want), want, throwOnEndOfStream: false, ct);
                if (read < want)
                {
                    throw new IOException("The upload ended before the announced size.");
                }
                sha.AppendData(buffer, 0, read);
                var (_, chunkError) = await SendAsync(agents, id, HvOperation.WriteFileChunk,
                    new FileWriteChunkRequest(started.UploadId, offset, Convert.ToBase64String(buffer, 0, read)), ct);
                if (chunkError is not null)
                {
                    await AbortAsync(agents, id, started.UploadId);
                    await http.WriteAuditAsync("host.files.upload", AuditOutcome.Failure, "host", id.ToString("D"), new { audit, path = started.Path, bytes = offset });
                    return chunkError;
                }
                offset += read;
            }
            var hash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            var (commit, commitError) = await SendAsync(agents, id, HvOperation.WriteFileCommit, new FileWriteCommitRequest(started.UploadId, hash), ct);
            if (commitError is not null)
            {
                await http.WriteAuditAsync("host.files.upload", AuditOutcome.Failure, "host", id.ToString("D"), new { audit, path = started.Path, sha256 = hash });
                return commitError;
            }
            var result = HvOperations.FromElement<FileWriteResult>(commit!.Result)!;
            await http.WriteAuditAsync("host.files.upload", AuditOutcome.Success, "host", id.ToString("D"), new { path = result.Path, bytes = result.SizeBytes, result.Sha256, audit.overwrite });
            return Results.Ok(result);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or BadHttpRequestException)
        {
            await AbortAsync(agents, id, started.UploadId);
            await http.WriteAuditAsync("host.files.upload", AuditOutcome.Failure, "host", id.ToString("D"), new { audit, path = started.Path, bytes = offset, reason = ex.Message });
            return ApiSupport.BadRequest("upload_interrupted", "The upload was interrupted; nothing was written on the host.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task AbortAsync(IAgentDispatcher agents, Guid hostId, Guid uploadId)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await SendAsync(agents, hostId, HvOperation.WriteFileAbort, new FileWriteAbortRequest(uploadId), cts.Token);
    }
}
