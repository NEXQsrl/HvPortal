using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Security;
using HvPortal.Gateway.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HvPortal.Gateway.Tests.Integration;

public sealed class DownloadTests : IAsyncLifetime
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

    [Fact]
    public async Task Agent_package_requires_hosts_manage()
    {
        await _app.CreateUserAsync("reader-dl", BuiltInRoles.ReadOnly);
        var c = _app.CreateBrowser();
        await c.LoginAsync("reader-dl");
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/v1/downloads/agent")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/v1/downloads/agent-setup")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/v1/downloads")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/v1/hosts/connection-info")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _app.CreateBrowser().GetAsync("/api/v1/downloads/agent")).StatusCode);
    }

    [Fact]
    public async Task Missing_package_is_reported_not_faked()
    {
        await _app.CreateUserAsync("infra-dl1", BuiltInRoles.InfrastructureAdmin);
        var c = _app.CreateBrowser();
        await c.LoginAsync("infra-dl1");
        var list = await (await c.GetAsync("/api/v1/downloads")).JsonAsync();
        Assert.Equal(System.Text.Json.JsonValueKind.Null, list.GetProperty("agent").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/v1/downloads/agent")).StatusCode);
    }

    [Fact]
    public async Task Package_is_served_with_its_real_hash_and_signature_state_and_download_is_audited()
    {
        Directory.CreateDirectory(_app.PackagesDirectory);
        var bytes = RandomNumberGenerator.GetBytes(4096);
        await File.WriteAllBytesAsync(Path.Combine(_app.PackagesDirectory, "HvPortal.Agent-win-x64.zip"), bytes);
        await File.WriteAllTextAsync(Path.Combine(_app.PackagesDirectory, "HvPortal.Agent-win-x64.json"),
            """{ "version": "9.9.9", "builtAtUtc": "2026-01-01T00:00:00Z", "signed": false, "signer": null }""");

        await _app.CreateUserAsync("infra-dl2", BuiltInRoles.InfrastructureAdmin);
        var c = _app.CreateBrowser();
        await c.LoginAsync("infra-dl2");

        var info = (await (await c.GetAsync("/api/v1/downloads")).JsonAsync()).GetProperty("agent");
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), info.GetProperty("sha256").GetString());
        Assert.Equal("9.9.9", info.GetProperty("version").GetString());
        Assert.False(info.GetProperty("signed").GetBoolean());
        Assert.False(info.GetProperty("selfSigned").GetBoolean());
        Assert.Equal(4096, info.GetProperty("sizeBytes").GetInt64());

        // No setup file on this gateway: the guide offers only the PowerShell method.
        Assert.Equal(JsonValueKind.Null, (await (await c.GetAsync("/api/v1/downloads")).JsonAsync()).GetProperty("agentSetup").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/v1/downloads/agent-setup")).StatusCode);
        // With the graphical setup next to the package: size and hash computed by the gateway, download audited.
        var setup = RandomNumberGenerator.GetBytes(2048);
        await File.WriteAllBytesAsync(Path.Combine(_app.PackagesDirectory, "HvPortal.Agent.Setup.exe"), setup);
        var setupInfo = (await (await c.GetAsync("/api/v1/downloads")).JsonAsync()).GetProperty("agentSetup");
        Assert.Equal(Convert.ToHexString(SHA256.HashData(setup)), setupInfo.GetProperty("sha256").GetString());
        Assert.Equal(2048, setupInfo.GetProperty("sizeBytes").GetInt64());
        var setupDownload = await c.GetAsync("/api/v1/downloads/agent-setup");
        Assert.Equal(HttpStatusCode.OK, setupDownload.StatusCode);
        Assert.Equal(setup, await setupDownload.Content.ReadAsByteArrayAsync());
        Assert.True(await _app.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "downloads.agent_setup" && a.Outcome == AuditOutcome.Success)));

        // Self-signed build (New-CodeSigningCertificate.ps1): reported as such, never as a plain trusted signature.
        var manifest = Path.Combine(_app.PackagesDirectory, "HvPortal.Agent-win-x64.json");
        await File.WriteAllTextAsync(manifest,
            """{ "version": "9.9.9", "signed": true, "signer": "CN=NEXQ S.r.l., O=NEXQ S.r.l., C=IT", "selfSigned": true }""");
        info = (await (await c.GetAsync("/api/v1/downloads")).JsonAsync()).GetProperty("agent");
        Assert.True(info.GetProperty("signed").GetBoolean());
        Assert.True(info.GetProperty("selfSigned").GetBoolean());
        Assert.Equal("CN=NEXQ S.r.l., O=NEXQ S.r.l., C=IT", info.GetProperty("signer").GetString());
        // An inconsistent manifest (self-signed but not signed) never turns into a signature.
        await File.WriteAllTextAsync(manifest, """{ "version": "9.9.9", "signed": false, "selfSigned": true }""");
        info = (await (await c.GetAsync("/api/v1/downloads")).JsonAsync()).GetProperty("agent");
        Assert.False(info.GetProperty("signed").GetBoolean());
        Assert.False(info.GetProperty("selfSigned").GetBoolean());

        var r = await c.GetAsync("/api/v1/downloads/agent");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("application/zip", r.Content.Headers.ContentType?.MediaType);
        Assert.Equal("HvPortal.Agent-win-x64.zip", r.Content.Headers.ContentDisposition?.FileNameStar ?? r.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Equal(bytes, await r.Content.ReadAsByteArrayAsync());
        Assert.True(await _app.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "downloads.agent" && a.Outcome == AuditOutcome.Success)));
    }

    [Fact]
    public async Task Connection_info_never_offers_loopback_names()
    {
        await _app.CreateUserAsync("infra-dl3", BuiltInRoles.InfrastructureAdmin);
        var c = _app.CreateBrowser();
        await c.LoginAsync("infra-dl3");
        var info = await (await c.GetAsync("/api/v1/hosts/connection-info")).JsonAsync();
        var names = info.GetProperty("gatewayNames").EnumerateArray().Select(n => n.GetString()).ToList();
        Assert.NotEmpty(names);
        Assert.DoesNotContain("localhost", names);
        Assert.DoesNotContain("127.0.0.1", names);
        Assert.DoesNotContain("localhost", info.GetProperty("gatewayAgentUrl").GetString());
        Assert.Equal(64, info.GetProperty("caFingerprintSha256").GetString()!.Length);
    }
}
