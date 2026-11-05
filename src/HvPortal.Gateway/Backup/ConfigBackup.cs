using System.Security.Cryptography;
using System.Text.Json;
using HvPortal.Gateway.Api;
using HvPortal.Gateway.Audit;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Configuration;
using HvPortal.Gateway.Crypto;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Security;
using HvPortal.Shared;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace HvPortal.Gateway.Backup;

/// <summary>
/// Backup and restore of the portal itself from Settings (ADR-020): one passphrase-sealed file (same format as
/// `admin export-keys`) with a consistent database snapshot (SQLite online backup), the key ring and the internal CA.
/// Restore is staged (decrypted DB + key ring re-protected with DPAPI on this machine) and applied at the next start,
/// before anything opens the database; the current state is kept in db\pre-restore-&lt;timestamp&gt;\ and keys\pre-restore-*.
/// Restricted to holders of every privileged permission, with re-authentication; audited.
/// </summary>
public static class ConfigBackup
{
    public const string Format = "hvportal-backup";
    public const int FormatVersion = 1;
    public const int MinPassphrase = 20;
    public const long MaxBackupBytes = 2L << 30;
    private const string ManifestName = "manifest.json";
    private const string DbName = "db/hvportal.db";

    public sealed record Manifest(string Format, int FormatVersion, string ProductVersion, DateTimeOffset CreatedAt, string? CreatedBy, string Machine,
        string? LastMigration, int Users, int Hosts, int Vms, int Groups, long DatabaseBytes);

    private sealed record Pending(Manifest Manifest, DateTimeOffset StagedAt, string StagedBy);

    private static string PendingDir(GatewayPaths p) => Path.Combine(p.Root, "restore-pending");
    private static string AppliedMarker(GatewayPaths p) => Path.Combine(p.Root, "restore-applied.json");

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/settings/backup").WithTags("Settings");
        g.MapPost("/export", ExportAsync);
        g.MapPost("/restore", RestoreAsync);
        g.MapGet("/restore", PendingAsync);
        g.MapDelete("/restore", CancelAsync);
        g.MapPost("/restart", Restart);
    }

    private static async Task<IResult?> GuardAsync(HttpContext http, AccessEvaluator access, string action)
    {
        // A backup carries every secret of the portal (sealed): only someone who already holds every privileged permission.
        if (await http.GuardAsync(Permissions.SettingsManage, ResourceRef.Global, action) is { } denied)
        {
            return denied;
        }
        if (!await access.HoldsAllAsync(http.User.UserId(), Permissions.PrivilegedNames, ResourceRef.Global, http.RequestAborted))
        {
            await http.WriteAuditAsync(action, AuditOutcome.Denied, details: new { reason = "requires every privileged permission" });
            return ApiSupport.Forbidden("forbidden", "Backup and restore require every privileged permission (platform administrator).");
        }
        return await http.RequireRecentReauthAsync();
    }

    private static string? Passphrase(HttpContext http) => http.Request.Headers["X-HvPortal-Passphrase"].FirstOrDefault();

    // ------------------------------------------------------------------ export

    public sealed record ExportRequest(string Passphrase);

    private static async Task<IResult> ExportAsync(ExportRequest req, HttpContext http, PortalDbContext db, AccessEvaluator access, KeyRing keys, GatewayPaths paths, TimeProvider time)
    {
        if (await GuardAsync(http, access, "settings.backup.export") is { } denied)
        {
            return denied;
        }
        if (req.Passphrase is not { Length: >= MinPassphrase and <= 1024 })
        {
            return ApiSupport.BadRequest("weak_passphrase", $"Use a passphrase of at least {MinPassphrase} characters.");
        }
        var snapshot = Path.Combine(paths.Root, $"backup-{Guid.NewGuid():N}.tmp");
        try
        {
            // Consistent online copy (SQLite backup API) — the service keeps running.
            if (db.Database.GetDbConnection() is not SqliteConnection source)
            {
                return ApiSupport.Error(StatusCodes.Status501NotImplemented, "unsupported_database", "Backup from the portal is available for SQLite.");
            }
            await db.Database.OpenConnectionAsync(http.RequestAborted);
            await using (var target = new SqliteConnection($"Data Source={snapshot};Pooling=False"))
            {
                await target.OpenAsync(http.RequestAborted);
                source.BackupDatabase(target);
            }
            await db.Database.CloseConnectionAsync();
            var dbBytes = await File.ReadAllBytesAsync(snapshot, http.RequestAborted);
            var manifest = new Manifest(Format, FormatVersion, ProductInfo.Version, time.GetUtcNow(), http.User.Identity?.Name, Environment.MachineName,
                (await db.Database.GetAppliedMigrationsAsync(http.RequestAborted)).LastOrDefault(),
                await db.Users.CountAsync(http.RequestAborted), await db.Hosts.CountAsync(http.RequestAborted),
                await db.VirtualMachines.CountAsync(http.RequestAborted), await db.LogicalGroups.CountAsync(http.RequestAborted), dbBytes.LongLength);
            var files = new Dictionary<string, byte[]>
            {
                [ManifestName] = JsonSerializer.SerializeToUtf8Bytes(manifest),
                [DbName] = dbBytes,
            };
            foreach (var f in new[] { "ca.bin", "server.bin" })
            {
                var p = Path.Combine(paths.Pki, f);
                if (File.Exists(p))
                {
                    files["pki/" + f] = await File.ReadAllBytesAsync(p, http.RequestAborted);
                }
            }
            var blob = keys.Export(req.Passphrase, files);
            await http.WriteAuditAsync("settings.backup.export", details: new { manifest.ProductVersion, manifest.Users, manifest.Hosts, manifest.Vms, bytes = blob.LongLength,
                sha256 = Convert.ToHexString(SHA256.HashData(blob)).ToLowerInvariant() });
            var name = $"hvportal-backup-{Environment.MachineName}-{time.GetUtcNow():yyyyMMdd-HHmmss}.hvpbak";
            http.Response.Headers.CacheControl = "no-store";
            return Results.File(blob, "application/octet-stream", name);
        }
        finally
        {
            try
            {
                File.Delete(snapshot);
            }
            catch (IOException)
            {
            }
        }
    }

    // ------------------------------------------------------------------ restore (staged)

    /// <summary>Decrypts and validates a backup. Throws <see cref="BackupException"/> with a short code.</summary>
    public static (Manifest Manifest, Dictionary<string, byte[]> Keys, string Active, Dictionary<string, byte[]> Files) Open(byte[] blob, string passphrase,
        IReadOnlyCollection<string> knownMigrations)
    {
        Dictionary<string, byte[]> keys, files;
        string active;
        try
        {
            (keys, active, files) = KeyRing.ReadExport(blob, passphrase);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or InvalidDataException or FormatException or ArgumentException)
        {
            throw new BackupException("invalid_backup", "Wrong passphrase or not a HvPortal backup.");
        }
        if (!files.TryGetValue(ManifestName, out var mb) || JsonSerializer.Deserialize<Manifest>(mb) is not { Format: Format } manifest)
        {
            throw new BackupException("invalid_backup", "This file is a key export, not a full backup (no database).");
        }
        if (manifest.FormatVersion > FormatVersion)
        {
            throw new BackupException("backup_too_new", "The backup was made by a newer HvPortal version.");
        }
        if (manifest.LastMigration is { } last && !knownMigrations.Contains(last))
        {
            throw new BackupException("backup_too_new", $"The backup database ({manifest.ProductVersion}) is newer than this gateway ({ProductInfo.Version}). Install that version first.");
        }
        if (!files.TryGetValue(DbName, out var dbBytes) || dbBytes.Length < 512 || !dbBytes.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8))
        {
            throw new BackupException("invalid_backup", "The backup does not contain a valid database.");
        }
        return (manifest, keys, active, files);
    }

    private static async Task<IResult> RestoreAsync(bool? apply, HttpContext http, PortalDbContext db, AccessEvaluator access, GatewayPaths paths, TimeProvider time)
    {
        if (await GuardAsync(http, access, "settings.backup.restore") is { } denied)
        {
            return denied;
        }
        var pass = Passphrase(http);
        if (pass is not { Length: >= 1 and <= 1024 })
        {
            return ApiSupport.BadRequest("passphrase_required");
        }
        if (http.Request.ContentLength is not { } len || len > MaxBackupBytes)
        {
            return ApiSupport.Error(StatusCodes.Status411LengthRequired, "length_required", "Upload the backup file (max 2 GB).");
        }
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = len;
        }
        using var ms = new MemoryStream((int)Math.Min(len, int.MaxValue));
        await http.Request.Body.CopyToAsync(ms, http.RequestAborted);
        (Manifest manifest, Dictionary<string, byte[]> keys, string active, Dictionary<string, byte[]> files) opened;
        try
        {
            opened = Open(ms.ToArray(), pass, db.Database.GetMigrations().ToList());
        }
        catch (BackupException ex)
        {
            await http.WriteAuditAsync("settings.backup.restore", AuditOutcome.Failure, details: new { reason = ex.Code });
            return ApiSupport.BadRequest(ex.Code, ex.Message);
        }
        if (apply != true)
        {
            // Preview only: nothing is written.
            return Results.Ok(new { staged = false, manifest = opened.manifest });
        }
        var confirm = http.Request.Headers["X-HvPortal-Confirm"].FirstOrDefault();
        if (confirm is not ("RIPRISTINA" or "RESTORE"))
        {
            return ApiSupport.BadRequest("confirmation_required", "Type RIPRISTINA (or RESTORE) to confirm.");
        }
        var dir = PendingDir(paths);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
        SecureFileSystem.EnsurePrivateDirectory(dir);
        SecureFileSystem.EnsurePrivateDirectory(Path.Combine(dir, "keys"));
        SecureFileSystem.EnsurePrivateDirectory(Path.Combine(dir, "pki"));
        // Keys are re-protected with DPAPI on this machine: nothing secret is left in clear on disk.
        KeyRing.WriteImported(Path.Combine(dir, "keys"), opened.keys, opened.active);
        foreach (var (name, bytes) in opened.files)
        {
            if (name is "pki/ca.bin" or "pki/server.bin")
            {
                SecureFileSystem.WriteAllBytesAtomic(Path.Combine(dir, "pki", Path.GetFileName(name)), bytes);
            }
        }
        SecureFileSystem.WriteAllBytesAtomic(Path.Combine(dir, "hvportal.db"), opened.files[DbName]);
        var pending = new Pending(opened.manifest, time.GetUtcNow(), http.User.Identity?.Name ?? "?");
        SecureFileSystem.WriteAllTextAtomic(Path.Combine(dir, "pending.json"), JsonSerializer.Serialize(pending));
        await http.WriteAuditAsync("settings.backup.restore.staged", AuditOutcome.Success, details: new { opened.manifest.CreatedAt, opened.manifest.ProductVersion, opened.manifest.Machine,
            opened.manifest.Users, opened.manifest.Hosts, opened.manifest.Vms });
        return Results.Ok(new { staged = true, manifest = opened.manifest, runningAsService = WindowsServiceHelpers.IsWindowsService() });
    }

    private static async Task<IResult> PendingAsync(HttpContext http, AccessEvaluator access, GatewayPaths paths)
    {
        if (await http.GuardAsync(Permissions.SettingsManage, ResourceRef.Global, "settings.backup.read") is { } denied)
        {
            return denied;
        }
        var file = Path.Combine(PendingDir(paths), "pending.json");
        var applied = AppliedMarker(paths);
        return Results.Ok(new
        {
            pending = File.Exists(file) ? JsonSerializer.Deserialize<Pending>(await File.ReadAllTextAsync(file)) : null,
            lastApplied = File.Exists(applied) ? JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(applied)) : (JsonElement?)null,
            runningAsService = WindowsServiceHelpers.IsWindowsService(),
        });
    }

    private static async Task<IResult> CancelAsync(HttpContext http, AccessEvaluator access, GatewayPaths paths)
    {
        if (await GuardAsync(http, access, "settings.backup.restore.cancel") is { } denied)
        {
            return denied;
        }
        var dir = PendingDir(paths);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
            await http.WriteAuditAsync("settings.backup.restore.cancel");
        }
        return Results.NoContent();
    }

    private static async Task<IResult> Restart(HttpContext http, AccessEvaluator access, ILoggerFactory logs)
    {
        if (await GuardAsync(http, access, "settings.backup.restart") is { } denied)
        {
            return denied;
        }
        if (!WindowsServiceHelpers.IsWindowsService())
        {
            return ApiSupport.Conflict("not_a_service", "The gateway is not running as a Windows service: restart it manually.");
        }
        await http.WriteAuditAsync("settings.backup.restart");
        logs.CreateLogger("HvPortal.Backup").LogWarning("Restart requested to apply a staged restore");
        // Non-zero exit: the service recovery actions configured by Install-Gateway.ps1 restart the service.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            Environment.Exit(1);
        });
        return Results.Accepted();
    }

    // ------------------------------------------------------------------ apply at startup

    /// <summary>Called at startup before the key ring and the database are opened.</summary>
    public static void ApplyPendingRestore(GatewayPaths paths, ILogger log)
    {
        var dir = PendingDir(paths);
        var pendingFile = Path.Combine(dir, "pending.json");
        if (!File.Exists(pendingFile))
        {
            return;
        }
        var pending = JsonSerializer.Deserialize<Pending>(File.ReadAllText(pendingFile));
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var keep = SecureFileSystem.EnsurePrivateDirectory(Path.Combine(paths.Database, "pre-restore-" + stamp)).FullName;
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var f = Path.Combine(paths.Database, "hvportal.db" + suffix);
            if (File.Exists(f))
            {
                File.Move(f, Path.Combine(keep, "hvportal.db" + suffix));
            }
        }
        var master = Path.Combine(paths.Keys, "master.json");
        if (File.Exists(master))
        {
            File.Copy(master, Path.Combine(keep, "master.json"));
        }
        foreach (var f in new[] { "ca.bin", "server.bin" })
        {
            var current = Path.Combine(paths.Pki, f);
            if (File.Exists(current))
            {
                File.Copy(current, Path.Combine(keep, f));
            }
            var staged = Path.Combine(dir, "pki", f);
            if (File.Exists(staged))
            {
                File.Copy(staged, current, overwrite: true);
            }
        }
        File.Copy(Path.Combine(dir, "keys", "master.json"), master, overwrite: true);
        File.Move(Path.Combine(dir, "hvportal.db"), Path.Combine(paths.Database, "hvportal.db"));
        SecureFileSystem.WriteAllTextAtomic(AppliedMarker(paths), JsonSerializer.Serialize(new
        {
            appliedAt = DateTimeOffset.UtcNow, backup = pending?.Manifest, stagedBy = pending?.StagedBy, previousStateKeptIn = keep, audited = false,
        }));
        Directory.Delete(dir, recursive: true);
        log.LogWarning("Restored the portal from a backup of {CreatedAt} ({Version}); previous state kept in {Keep}", pending?.Manifest.CreatedAt, pending?.Manifest.ProductVersion, keep);
    }

    /// <summary>
    /// After startup: sessions that existed when the backup was taken are revoked (nobody resumes an old session), and the
    /// applied restore is recorded once in the (restored) audit log.
    /// </summary>
    public static async Task AfterRestoreAsync(GatewayPaths paths, AuditLog audit, PortalDbContext db, TimeProvider time)
    {
        var marker = AppliedMarker(paths);
        if (!File.Exists(marker))
        {
            return;
        }
        var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(await File.ReadAllTextAsync(marker))!;
        if (doc.TryGetValue("audited", out var a) && a.ValueKind == JsonValueKind.True)
        {
            return;
        }
        var now = time.GetUtcNow();
        var revoked = await db.Sessions.Where(x => x.RevokedAt == null).ExecuteUpdateAsync(u => u.SetProperty(x => x.RevokedAt, now));
        doc["sessionsRevoked"] = JsonSerializer.SerializeToElement(revoked);
        await audit.WriteAsync(new AuditRecord { Action = "settings.backup.restore.applied", ActorName = "system", AuthMethod = "system", Details = doc });
        doc["audited"] = JsonSerializer.SerializeToElement(true);
        SecureFileSystem.WriteAllTextAtomic(marker, JsonSerializer.Serialize(doc));
    }
}

public sealed class BackupException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
