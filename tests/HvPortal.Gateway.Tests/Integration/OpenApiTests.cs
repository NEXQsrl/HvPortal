using System.Net;
using System.Text.Json;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Security;
using HvPortal.Gateway.Tests.Infrastructure;

namespace HvPortal.Gateway.Tests.Integration;

public sealed class OpenApiTests : IAsyncLifetime
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
    public async Task OpenApi_document_describes_the_api_and_requires_authentication()
    {
        var anonymous = _app.CreateBrowser();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/openapi/v1.json")).StatusCode);

        await _app.CreateUserAsync("doc-reader", BuiltInRoles.ReadOnly);
        var c = _app.CreateBrowser();
        await c.LoginAsync("doc-reader");
        var r = await c.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var json = await r.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var paths = doc.RootElement.GetProperty("paths");
        foreach (var p in new[] { "/api/v1/auth/login", "/api/v1/vms/{id}/power", "/api/v1/jobs/{id}", "/api/v1/role-assignments", "/api/v1/audit/verify" })
        {
            Assert.True(paths.TryGetProperty(p, out _), $"missing {p}");
        }
        // Agent endpoints are not part of the public API description.
        Assert.False(paths.TryGetProperty("/agent/v1/enroll", out _));

        // Regenerate the committed copy with: $env:HVP_WRITE_OPENAPI = "<repo>\docs\openapi\v1.json"; dotnet test --filter OpenApi
        if (Environment.GetEnvironmentVariable("HVP_WRITE_OPENAPI") is { Length: > 0 } target)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target, JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
