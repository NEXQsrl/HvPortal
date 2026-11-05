using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using HvPortal.Gateway.Audit;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Backup;
using HvPortal.Gateway.Configuration;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Security;
using HvPortal.Gateway.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HvPortal.Gateway.Tests.Integration;

/// <summary>Portal backup (sealed DB snapshot + key ring + CA) and staged restore applied at startup.</summary>
public sealed class ConfigBackupTests : IAsyncLifetime
{
    private const string Pass = "correct horse battery staple 2026";
    private GatewayFactory _app = null!;

    public async Task InitializeAsync()
    {
        _app = new GatewayFactory();
        _ = _app.Server;
        await _app.SetMfaPolicyAsync(MfaRequirement.None);
    }

    public Task DisposeAsync()
    {
        _app.Dispose();
        return Task.CompletedTask;
    }

    private static async Task<HttpResponseMessage> RestoreAsync(HttpClient c, byte[] file, string pass, bool apply = false, string? confirm = null)
    {
        await c.CsrfAsync();
        var content = new ByteArrayContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/settings/backup/restore" + (apply ? "?apply=true" : "")) { Content = content };
        req.Headers.Add("X-HvPortal-Passphrase", pass);
        if (confirm is not null)
        {
            req.Headers.Add("X-HvPortal-Confirm", confirm);
        }
        return await c.SendAsync(req);
    }

    [Fact]
    public async Task Backup_restore_round_trip_restores_the_state_and_revokes_sessions()
    {
        await _app.CreateUserAsync("admin-bk", BuiltInRoles.PlatformAdmin);
        var admin = _app.CreateBrowser();
        await admin.LoginAsync("admin-bk");
        await admin.ReauthAsync();
        await _app.CreateHostAsync("bk-host", ("vm1", Contracts.HyperV.VmPowerState.Off));

        // Only platform administrators; strong passphrase.
        await _app.CreateUserAsync("infra-bk", BuiltInRoles.InfrastructureAdmin);
        var infra = _app.CreateBrowser();
        await infra.LoginAsync("infra-bk");
        await infra.ReauthAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await infra.PostJsonAsync("/api/v1/settings/backup/export", new { passphrase = Pass })).StatusCode);
        Assert.Equal("weak_passphrase", await (await admin.PostJsonAsync("/api/v1/settings/backup/export", new { passphrase = "short" })).CodeAsync());

        var export = await admin.PostJsonAsync("/api/v1/settings/backup/export", new { passphrase = Pass });
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.EndsWith(".hvpbak", export.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        var backup = await export.Content.ReadAsByteArrayAsync();
        Assert.DoesNotContain("admin-bk", System.Text.Encoding.UTF8.GetString(backup)); // sealed, not plain SQLite

        // Change after the backup.
        await _app.CreateUserAsync("created-after-backup");

        Assert.Equal("invalid_backup", await (await RestoreAsync(admin, backup, "wrong passphrase for this file")).CodeAsync());
        var preview = await (await RestoreAsync(admin, backup, Pass)).JsonAsync();
        Assert.False(preview.GetProperty("staged").GetBoolean());
        Assert.Equal(1, preview.GetProperty("manifest").GetProperty("hosts").GetInt32());
        var paths = _app.Services.GetRequiredService<GatewayPaths>();
        Assert.False(Directory.Exists(Path.Combine(paths.Root, "restore-pending")));
        Assert.Equal("confirmation_required", await (await RestoreAsync(admin, backup, Pass, apply: true)).CodeAsync());
        var staged = await (await RestoreAsync(admin, backup, Pass, apply: true, confirm: "RIPRISTINA")).JsonAsync();
        Assert.True(staged.GetProperty("staged").GetBoolean());
        Assert.True(File.Exists(Path.Combine(paths.Root, "restore-pending", "hvportal.db")));
        Assert.NotEqual(JsonNull, (await (await admin.GetAsync("/api/v1/settings/backup/restore")).JsonAsync()).GetProperty("pending").ValueKind);

        // "Restart": no open connection, apply, then the post-start step.
        SqliteConnection.ClearAllPools();
        ConfigBackup.ApplyPendingRestore(paths, NullLogger.Instance);
        Assert.False(Directory.Exists(Path.Combine(paths.Root, "restore-pending")));
        Assert.Single(Directory.GetDirectories(paths.Database, "pre-restore-*"));
        await _app.WithDbAsync(db => ConfigBackup.AfterRestoreAsync(paths, _app.Services.GetRequiredService<AuditLog>(), db, TimeProvider.System));

        Assert.False(await _app.WithDbAsync(db => db.Users.AnyAsync(u => u.UserName == "created-after-backup")));
        Assert.True(await _app.WithDbAsync(db => db.Users.AnyAsync(u => u.UserName == "admin-bk")));
        Assert.False(await _app.WithDbAsync(db => db.Sessions.AnyAsync(s => s.RevokedAt == null)));
        Assert.True(await _app.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "settings.backup.restore.applied")));
        // The audit chain of the restored database is still valid.
        Assert.True((await _app.Services.GetRequiredService<AuditLog>().VerifyAsync(CancellationToken.None)).Valid);
        // Old session cookie no longer works.
        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    private const System.Text.Json.JsonValueKind JsonNull = System.Text.Json.JsonValueKind.Null;
}
