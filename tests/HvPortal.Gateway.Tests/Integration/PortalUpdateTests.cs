using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Security;
using HvPortal.Gateway.Tests.Infrastructure;
using HvPortal.Gateway.Updates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HvPortal.Gateway.Tests.Integration;

/// <summary>
/// Updates of the portal from the GitHub releases (ADR-042). GitHub and the updater task are test doubles: what the
/// SYSTEM updater script does on a real server is in the real-host test plan (V82).
/// </summary>
public sealed class PortalUpdateTests : IAsyncLifetime
{
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

    private async Task<HttpClient> UserAsync(string name, string role)
    {
        await _app.CreateUserAsync(name, role);
        var c = _app.CreateBrowser();
        await c.LoginAsync(name);
        return c;
    }

    private string UpdateFolder() => _app.Services.CreateScope().ServiceProvider.GetRequiredService<PortalUpdateService>().UpdateFolder;

    [Fact]
    public async Task Only_settings_administrators_check_and_github_is_contacted_only_when_asked()
    {
        var infra = await UserAsync("upd-infra", BuiltInRoles.InfrastructureAdmin);
        Assert.Equal(HttpStatusCode.Forbidden, (await infra.GetAsync("/api/v1/system/updates")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await infra.PostJsonAsync("/api/v1/system/updates/check")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await infra.PostJsonAsync("/api/v1/system/updates/install", new { version = "9.9.9" })).StatusCode);
        Assert.Equal(3, await _app.WithDbAsync(db => db.AuditEntries.CountAsync(a => a.Action.StartsWith("system.updates.") && a.Outcome == AuditOutcome.Denied)));

        var admin = await UserAsync("upd-admin", BuiltInRoles.PlatformAdmin);
        var status = await (await admin.GetAsync("/api/v1/system/updates")).JsonAsync();
        Assert.Equal(0, _app.Releases.Checks);
        Assert.False(status.GetProperty("autoCheck").GetBoolean());
        Assert.False(status.GetProperty("updateAvailable").GetBoolean());

        _app.Releases.Publish("99.0.0");
        var checkedNow = await (await admin.PostJsonAsync("/api/v1/system/updates/check")).JsonAsync();
        Assert.Equal(1, _app.Releases.Checks);
        Assert.True(checkedNow.GetProperty("updateAvailable").GetBoolean());
        Assert.Equal("99.0.0", checkedNow.GetProperty("latest").GetProperty("version").GetString());

        // A release older than the running portal is not an update.
        _app.Releases.Publish("0.0.1");
        Assert.False((await (await admin.PostJsonAsync("/api/v1/system/updates/check")).JsonAsync()).GetProperty("updateAvailable").GetBoolean());

        _app.Releases.CheckError = new HttpRequestException("no route to api.github.com");
        var failed = await (await admin.PostJsonAsync("/api/v1/system/updates/check")).JsonAsync();
        Assert.Contains("no route", failed.GetProperty("lastError").GetString(), StringComparison.Ordinal);
        Assert.Equal(1, await _app.WithDbAsync(db => db.AuditEntries.CountAsync(a => a.Action == "system.updates.check" && a.Outcome == AuditOutcome.Failure)));

        await admin.CsrfAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/v1/system/updates/settings", new { autoCheck = true }, GatewayFactory.Json)).StatusCode);
        Assert.True((await (await admin.GetAsync("/api/v1/system/updates")).JsonAsync()).GetProperty("autoCheck").GetBoolean());
    }

    [Fact]
    public async Task An_update_is_staged_only_when_its_checksum_matches_and_then_handed_to_the_updater()
    {
        var admin = await UserAsync("inst-admin", BuiltInRoles.PlatformAdmin);
        await admin.ReauthAsync();
        _app.Releases.Publish("99.0.0", tampered: true);
        await admin.PostJsonAsync("/api/v1/system/updates/check");
        Assert.Equal("checksum_mismatch", await (await admin.PostJsonAsync("/api/v1/system/updates/install", new { version = "99.0.0" })).CodeAsync());
        Assert.Equal(0, _app.Releases.Starts);
        Assert.False(File.Exists(Path.Combine(UpdateFolder(), "HvPortal.Gateway.Setup.exe")));

        _app.Releases.Publish("99.0.0", checksum: false);
        await admin.PostJsonAsync("/api/v1/system/updates/check");
        Assert.Equal("no_checksum", await (await admin.PostJsonAsync("/api/v1/system/updates/install", new { version = "99.0.0" })).CodeAsync());

        _app.Releases.Publish("99.0.0");
        await admin.PostJsonAsync("/api/v1/system/updates/check");
        Assert.Equal("not_latest", await (await admin.PostJsonAsync("/api/v1/system/updates/install", new { version = "98.0.0" })).CodeAsync());
        _app.Releases.BlockerValue = "container";
        Assert.Equal("container", await (await admin.PostJsonAsync("/api/v1/system/updates/install", new { version = "99.0.0" })).CodeAsync());
        _app.Releases.BlockerValue = null;

        var accepted = await admin.PostJsonAsync("/api/v1/system/updates/install", new { version = "99.0.0" });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Equal(1, _app.Releases.Starts);
        Assert.Equal(_app.Releases.SetupBytes, await File.ReadAllBytesAsync(Path.Combine(UpdateFolder(), "HvPortal.Gateway.Setup.exe")));
        using var request = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(UpdateFolder(), "request.json")));
        Assert.Equal("99.0.0", request.RootElement.GetProperty("version").GetString());
        Assert.Equal(64, request.RootElement.GetProperty("sha256").GetString()!.Length);
        Assert.Equal(1, await _app.WithDbAsync(db => db.AuditEntries.CountAsync(a => a.Action == "system.updates.install" && a.Outcome == AuditOutcome.Success)));
    }

    [Fact]
    public void Only_github_addresses_are_accepted_for_releases()
    {
        Assert.True(GitHubReleaseSource.IsGitHubUrl("https://github.com/NEXQsrl/HvPortal/releases/download/v1/HvPortal.Gateway.Setup.exe"));
        Assert.False(GitHubReleaseSource.IsGitHubUrl("http://github.com/x"));
        Assert.False(GitHubReleaseSource.IsGitHubUrl("https://github.com.evil.example/x"));
        Assert.False(GitHubReleaseSource.IsGitHubUrl("https://user:pw@github.com/x"));
        Assert.False(GitHubReleaseSource.IsGitHubUrl("https://example.com/HvPortal.Gateway.Setup.exe"));
    }
}
