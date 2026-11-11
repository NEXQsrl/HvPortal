using HvPortal.Gateway.Api;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Security;

namespace HvPortal.Gateway.Updates;

/// <summary>Settings > System > Updates (ADR-042): settings.manage; installing also needs a recent re-authentication.</summary>
public static class PortalUpdateEndpoints
{
    public sealed record SettingsBody(bool AutoCheck);
    public sealed record InstallBody(string? Version);

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/system/updates").WithTags("System");
        g.MapGet("/", StatusAsync);
        g.MapPost("/check", CheckAsync);
        g.MapPut("/settings", SettingsAsync);
        g.MapPost("/install", InstallAsync);
    }

    private static async Task<object> ViewAsync(PortalUpdateService updates, PortalUpdateService.State state, CancellationToken ct)
    {
        await Task.CompletedTask;
        var latest = state.Latest;
        var lastResult = updates.ReadLastResult();
        return new
        {
            // What became of the last update asked from here (0.9.4): the update page waits on this, not on its own guesses.
            install = PortalUpdateService.InstallStatusOf(state, lastResult, ProductInfo.Version),
            current = ProductInfo.Version,
            state.AutoCheck,
            state.LastCheckAt,
            state.LastError,
            latest = latest is null ? null : new
            {
                latest.Version, latest.Tag, latest.PublishedAt, latest.HtmlUrl, latest.Notes,
                gatewaySetup = latest.GatewaySetup is { } g ? new { g.Name, g.Size, hasChecksum = g.Sha256 is not null } : null,
                agentSetup = latest.AgentSetup is { } a ? new { a.Name, a.Size, hasChecksum = a.Sha256 is not null } : null,
            },
            updateAvailable = PortalUpdateService.IsNewer(latest?.Version, ProductInfo.Version),
            installBlocker = updates.InstallBlocker(),
            state.InstallRequestedAt,
            state.InstallRequestedVersion,
            lastResult,
        };
    }

    private static async Task<IResult> StatusAsync(HttpContext http, PortalUpdateService updates)
    {
        if (await http.GuardAsync(Permissions.SettingsManage, ResourceRef.Global, "system.updates.read") is { } denied)
        {
            return denied;
        }
        return Results.Ok(await ViewAsync(updates, await updates.LoadAsync(http.RequestAborted), http.RequestAborted));
    }

    /// <summary>Asks GitHub for the latest release now (the only moment the portal contacts GitHub, unless automatic checks are on).</summary>
    private static async Task<IResult> CheckAsync(HttpContext http, PortalUpdateService updates)
    {
        if (await http.GuardAsync(Permissions.SettingsManage, ResourceRef.Global, "system.updates.check") is { } denied)
        {
            return denied;
        }
        var state = await updates.CheckAsync(http.RequestAborted);
        await http.WriteAuditAsync("system.updates.check", state.LastError is null ? AuditOutcome.Success : AuditOutcome.Failure,
            details: new { latest = state.Latest?.Version, error = state.LastError });
        return Results.Ok(await ViewAsync(updates, state, http.RequestAborted));
    }

    private static async Task<IResult> SettingsAsync(SettingsBody body, HttpContext http, PortalUpdateService updates)
    {
        if (await http.GuardAsync(Permissions.SettingsManage, ResourceRef.Global, "system.updates.settings") is { } denied)
        {
            return denied;
        }
        var state = await updates.LoadAsync(http.RequestAborted);
        await updates.SaveAsync(state with { AutoCheck = body.AutoCheck }, http.RequestAborted);
        await http.WriteAuditAsync("system.updates.settings", details: new { before = state.AutoCheck, after = body.AutoCheck });
        return Results.NoContent();
    }

    private static async Task<IResult> InstallAsync(InstallBody body, HttpContext http, PortalUpdateService updates)
    {
        if ((await http.GuardAsync(Permissions.SettingsManage, ResourceRef.Global, "system.updates.install") ?? await http.RequireRecentReauthAsync()) is { } denied)
        {
            return denied;
        }
        if (body.Version is not { Length: > 0 and <= 32 } version)
        {
            return ApiSupport.BadRequest("invalid_version");
        }
        var (code, message) = await updates.InstallAsync(version, http.RequestAborted);
        if (code is not null)
        {
            await http.WriteAuditAsync("system.updates.install", AuditOutcome.Failure, details: new { version, from = ProductInfo.Version, reason = code });
            return ApiSupport.Conflict(code, message);
        }
        await http.WriteAuditAsync("system.updates.install", details: new { version, from = ProductInfo.Version });
        return Results.Accepted(value: new { version, message = "The update was verified and handed to the updater: the portal restarts in a few minutes." });
    }
}
