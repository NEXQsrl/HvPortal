using System.Globalization;
using HvPortal.Gateway.Api;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Configuration;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Security;
using HvPortal.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HvPortal.Gateway.Backup;

/// <summary>
/// Automatic local copies of the SQLite database (ADR-023): once a day by default, kept for 7 days in
/// &lt;data&gt;\db\backups\hvportal-auto-&lt;UTC timestamp&gt;.db. Each copy is taken with the SQLite online backup API (the
/// service keeps running) and verified with PRAGMA integrity_check before it is kept; a copy that fails the check is
/// discarded and older copies are NOT pruned, so a damaged live database never pushes out the last good copy.
/// These files protect against database corruption only: they live on the same disk and, like the live database, are
/// useless without the key ring. Disaster recovery still needs the sealed backup of Settings (ADR-020).
/// </summary>
public sealed class DatabaseBackupService : BackgroundService
{
    public const string FilePrefix = "hvportal-auto-";
    private const string StampFormat = "yyyyMMdd-HHmmss";

    private readonly IServiceScopeFactory _scopes;
    private readonly GatewayPaths _paths;
    private readonly DatabaseOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<DatabaseBackupService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset? _lastAttemptAt;
    private string? _lastError;

    public DatabaseBackupService(IServiceScopeFactory scopes, GatewayPaths paths, IOptions<GatewayOptions> options, TimeProvider time,
        ILogger<DatabaseBackupService> logger)
    {
        _scopes = scopes;
        _paths = paths;
        _options = options.Value.Database;
        _time = time;
        _logger = logger;
    }

    public sealed record BackupFile(string Name, DateTimeOffset CreatedAt, long SizeBytes);

    public sealed record Status(bool Enabled, int RetentionDays, int IntervalHours, string Directory, DateTimeOffset? LastAttemptAt, string? LastError,
        IReadOnlyList<BackupFile> Files);

    public string BackupDirectory => Path.Combine(_paths.Database, "backups");

    private int RetentionDays => Math.Clamp(_options.AutoBackupRetentionDays, 1, 365);
    private int IntervalHours => Math.Clamp(_options.AutoBackupIntervalHours, 1, 24 * 7);

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/settings/backup/auto").WithTags("Settings");
        g.MapGet("/", async (HttpContext http, DatabaseBackupService service) =>
            await http.GuardAsync(Permissions.SettingsManage, ResourceRef.Global, "settings.backup.auto.read") ?? Results.Ok(service.GetStatus()));
        g.MapPost("/run", async (HttpContext http, DatabaseBackupService service) =>
        {
            if ((await http.GuardAsync(Permissions.SettingsManage, ResourceRef.Global, "settings.backup.auto.run") ?? await http.RequireRecentReauthAsync()) is { } denied)
            {
                return denied;
            }
            var (file, error) = await service.RunAsync(http.RequestAborted);
            await http.WriteAuditAsync("settings.backup.auto.run", file is null ? AuditOutcome.Failure : AuditOutcome.Success,
                details: new { file = file?.Name, bytes = file?.SizeBytes, error });
            return file is null
                ? ApiSupport.Error(StatusCodes.Status500InternalServerError, "database_backup_failed", error ?? "The database copy failed.")
                : Results.Ok(service.GetStatus());
        });
    }

    public Status GetStatus() =>
        new(_options.AutoBackupEnabled, RetentionDays, IntervalHours, BackupDirectory, _lastAttemptAt, _lastError, List());

    /// <summary>Verified copies, newest first. The time comes from the file name (it does not change when files are copied around).</summary>
    public IReadOnlyList<BackupFile> List()
    {
        if (!Directory.Exists(BackupDirectory))
        {
            return [];
        }
        var list = new List<BackupFile>();
        foreach (var f in new DirectoryInfo(BackupDirectory).EnumerateFiles(FilePrefix + "*.db"))
        {
            var stamp = Path.GetFileNameWithoutExtension(f.Name)[FilePrefix.Length..];
            if (DateTime.TryParseExact(stamp, StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
            {
                list.Add(new BackupFile(f.Name, new DateTimeOffset(at, TimeSpan.Zero), f.Length));
            }
        }
        return list.OrderByDescending(x => x.CreatedAt).ToList();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.AutoBackupEnabled)
        {
            _logger.LogInformation("Automatic database backups are disabled (Gateway:Database:AutoBackupEnabled).");
            return;
        }
        try
        {
            // Not during start-up (migrations, agents reconnecting).
            await Task.Delay(TimeSpan.FromMinutes(2), _time, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                var last = List().FirstOrDefault();
                if (last is null || _time.GetUtcNow() - last.CreatedAt >= TimeSpan.FromHours(IntervalHours))
                {
                    await RunAsync(stoppingToken);
                }
                await Task.Delay(TimeSpan.FromMinutes(30), _time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Gateway stopping.
        }
    }

    /// <summary>Takes a verified copy now and prunes copies older than the retention. Returns the new file or the error.</summary>
    public async Task<(BackupFile? File, string? Error)> RunAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        var now = _time.GetUtcNow();
        _lastAttemptAt = now;
        string? temp = null;
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
            if (db.Database.GetDbConnection() is not SqliteConnection source)
            {
                return (null, _lastError = "Automatic copies are available for SQLite only.");
            }
            SecureFileSystem.EnsurePrivateDirectory(BackupDirectory);
            var name = FilePrefix + now.UtcDateTime.ToString(StampFormat, CultureInfo.InvariantCulture) + ".db";
            var final = Path.Combine(BackupDirectory, name);
            temp = final + ".tmp";
            File.Delete(temp);
            await db.Database.OpenConnectionAsync(ct);
            try
            {
                await using var target = new SqliteConnection($"Data Source={temp};Pooling=False");
                await target.OpenAsync(ct);
                source.BackupDatabase(target);
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
            var check = await IntegrityAsync(temp, ct);
            if (check != "ok")
            {
                File.Delete(temp);
                _lastError = "The copy failed the integrity check and was discarded (older copies are kept): " + check;
                _logger.LogError("Automatic database backup failed the integrity check: {Result}", check);
                return (null, _lastError);
            }
            File.Move(temp, final, overwrite: true);
            temp = null;
            _lastError = null;
            Prune(now);
            var info = new FileInfo(final);
            _logger.LogInformation("Automatic database backup written: {File} ({Bytes} bytes)", name, info.Length);
            return (new BackupFile(name, now, info.Length), null);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _lastError = ex.Message;
            _logger.LogError(ex, "Automatic database backup failed");
            return (null, _lastError);
        }
        finally
        {
            if (temp is not null)
            {
                try
                {
                    File.Delete(temp);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Left behind; overwritten by the next run.
                }
            }
            _gate.Release();
        }
    }

    private static async Task<string> IntegrityAsync(string file, CancellationToken ct)
    {
        await using var c = new SqliteConnection($"Data Source={file};Mode=ReadOnly;Pooling=False");
        await c.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check;";
        var result = Convert.ToString(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) ?? "no result";
        return result.Length > 500 ? result[..500] : result;
    }

    /// <summary>Removes copies older than the retention; the newest copy is always kept.</summary>
    private void Prune(DateTimeOffset now)
    {
        var limit = now - TimeSpan.FromDays(RetentionDays);
        foreach (var old in List().Skip(1).Where(f => f.CreatedAt < limit))
        {
            try
            {
                File.Delete(Path.Combine(BackupDirectory, old.Name));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not remove the old database copy {File}", old.Name);
            }
        }
    }

    public override void Dispose()
    {
        _gate.Dispose();
        base.Dispose();
    }
}
