using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HvPortal.Gateway.Api;
using HvPortal.Gateway.Audit;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace HvPortal.Gateway.Security.Entra;

/// <summary>
/// Microsoft Entra ID sign-in (ADR-019): OpenID Connect authorization code flow with PKCE, confidential client, single
/// tenant. Users are pre-provisioned in the portal (Users > New user > Microsoft) by user principal name and bound to
/// their Entra object id at the first sign-in; nobody gets in just because the tenant knows them. The state, nonce
/// and PKCE verifier live in a short-lived data-protected cookie. Also used to re-authenticate Entra users before
/// sensitive actions (prompt=login, max_age=0). Outbound HTTPS to Microsoft happens only when Entra is enabled.
/// </summary>
public static class EntraEndpoints
{
    private const string StateCookie = "hvp.entra";
    private const string CallbackPath = "/api/v1/auth/entra/callback";
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);

    public sealed record EntraSettingsRequest(bool Enabled, string? TenantId, string? ClientId, string? Authority, string? ClientSecret);

    private sealed record State(string Value, string Nonce, string Verifier, string ReturnUrl, string Mode, Guid? SessionId);

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/v1").WithTags("Authentication");
        g.MapGet("/auth/entra/start", StartAsync).AllowAnonymous().RequireRateLimiting("auth");
        g.MapGet("/auth/entra/callback", CallbackAsync).AllowAnonymous().RequireRateLimiting("auth");

        var s = app.MapGroup("/api/v1/settings/entra").WithTags("Settings");
        s.MapGet("/", GetSettingsAsync);
        s.MapPut("/", UpdateSettingsAsync);
        s.MapPost("/test", TestAsync);
    }

    public static string RedirectUri(HttpContext http) => $"https://{http.Request.Host}{CallbackPath}";

    private static IDataProtector Protector(HttpContext http) =>
        http.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector("HvPortal.Entra.State.v1");

    // ------------------------------------------------------------------ settings

    private static object View(EntraConfig c, HttpContext http) => new
    {
        c.Enabled,
        c.TenantId,
        c.ClientId,
        c.Authority,
        hasClientSecret = c.ClientSecretEncrypted is { Length: > 0 },
        complete = c.IsComplete,
        c.UpdatedAt,
        redirectUri = RedirectUri(http),
        authorities = EntraConfigStore.Authorities,
    };

    private static async Task<IResult> GetSettingsAsync(HttpContext http, EntraConfigStore store)
    {
        if (await http.GuardAsync(Permissions.SettingsManage, ResourceRef.Global, "settings.entra.read") is { } denied)
        {
            return denied;
        }
        return Results.Ok(View(await store.GetAsync(http.RequestAborted), http));
    }

    private static async Task<IResult> UpdateSettingsAsync(EntraSettingsRequest req, HttpContext http, EntraConfigStore store)
    {
        if ((await http.GuardAsync(Permissions.SettingsManage, ResourceRef.Global, "settings.entra.update") ?? await http.RequireRecentReauthAsync()) is { } denied)
        {
            return denied;
        }
        var tenant = req.TenantId?.Trim();
        var client = req.ClientId?.Trim();
        if ((tenant is { Length: > 0 } && !Guid.TryParse(tenant, out _)) || (client is { Length: > 0 } && !Guid.TryParse(client, out _)))
        {
            return ApiSupport.BadRequest("invalid_entra_settings", "Tenant ID and application (client) ID are GUIDs (Entra admin center > App registrations > Overview).");
        }
        var authority = (req.Authority ?? EntraConfigStore.GlobalAuthority).TrimEnd('/');
        if (!EntraConfigStore.Authorities.Contains(authority, StringComparer.OrdinalIgnoreCase))
        {
            return ApiSupport.BadRequest("invalid_entra_settings", "Unknown Microsoft identity platform instance.");
        }
        if (req.ClientSecret is { Length: > 512 })
        {
            return ApiSupport.BadRequest("invalid_entra_settings", "Client secret too long.");
        }
        var before = await store.GetAsync(http.RequestAborted);
        var next = before with
        {
            Enabled = req.Enabled,
            TenantId = tenant is { Length: > 0 } ? Guid.Parse(tenant).ToString("D") : null,
            ClientId = client is { Length: > 0 } ? Guid.Parse(client).ToString("D") : null,
            Authority = authority.ToLowerInvariant(),
        };
        var secretChange = req.ClientSecret is null ? null : req.ClientSecret.Trim();
        var preview = next with { ClientSecretEncrypted = secretChange switch { null => before.ClientSecretEncrypted, "" => null, _ => "x" } };
        if (next.Enabled && !preview.IsComplete)
        {
            return ApiSupport.BadRequest("invalid_entra_settings", "Tenant ID, application ID and client secret are required to enable Entra ID sign-in.");
        }
        var saved = await store.SaveAsync(next, secretChange, http.RequestAborted);
        await http.WriteAuditAsync("settings.entra.update", details: new
        {
            before = new { before.Enabled, before.TenantId, before.ClientId, before.Authority },
            after = new { saved.Enabled, saved.TenantId, saved.ClientId, saved.Authority },
            secret = secretChange switch { null => "unchanged", "" => "removed", _ => "replaced" },
        });
        return Results.Ok(View(saved, http));
    }

    private static async Task<IResult> TestAsync(HttpContext http, EntraConfigStore store, IEntraClient client)
    {
        if (await http.GuardAsync(Permissions.SettingsManage, ResourceRef.Global, "settings.entra.test") is { } denied)
        {
            return denied;
        }
        var cfg = await store.GetAsync(http.RequestAborted);
        if (!Guid.TryParse(cfg.TenantId, out _))
        {
            return ApiSupport.BadRequest("entra_not_configured");
        }
        try
        {
            var meta = await client.GetConfigurationAsync(cfg.Authority, cfg.TenantId!, http.RequestAborted);
            return Results.Ok(new { ok = true, issuer = meta.Issuer, keys = meta.SigningKeys.Count, authorizationEndpoint = meta.AuthorizationEndpoint });
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException or TaskCanceledException)
        {
            return ApiSupport.Error(StatusCodes.Status502BadGateway, "entra_unreachable", "Cannot read the tenant metadata from Microsoft: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ sign-in

    private static string LocalReturnUrl(string? url) =>
        url is { Length: > 0 and < 500 } && url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal) && !url.Contains('\\', StringComparison.Ordinal)
            ? url : "/";

    private static IResult LoginError(string code) => Results.Redirect("/login?error=" + Uri.EscapeDataString("entra_" + code));

    private static async Task<IResult> StartAsync(string? returnUrl, string? mode, HttpContext http, EntraConfigStore store, IEntraClient client, PortalDbContext db)
    {
        var cfg = await store.GetAsync(http.RequestAborted);
        if (!cfg.IsActive)
        {
            return LoginError("not_configured");
        }
        Guid? sessionId = null;
        if (mode == "reauth")
        {
            // Re-authentication of the signed-in Entra user (popup opened by the portal itself, same-site: the cookie is sent).
            if (http.User.Identity?.IsAuthenticated != true)
            {
                return LoginError("session_expired");
            }
            sessionId = http.User.SessionId();
        }
        Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration meta;
        try
        {
            meta = await client.GetConfigurationAsync(cfg.Authority, cfg.TenantId!, http.RequestAborted);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException or TaskCanceledException)
        {
            return LoginError("unreachable");
        }
        var state = new State(Tokens.New(), Tokens.New(), Tokens.New() + Tokens.New(), LocalReturnUrl(returnUrl), mode == "reauth" ? "reauth" : "login", sessionId);
        var protectedState = Protector(http).ToTimeLimitedDataProtector().Protect(JsonSerializer.Serialize(state), StateLifetime);
        http.Response.Cookies.Append(StateCookie, protectedState, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            // Lax: the cookie must come back on the top-level redirect from login.microsoftonline.com.
            SameSite = SameSiteMode.Lax,
            Path = "/api/v1/auth/entra",
            MaxAge = StateLifetime,
        });
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(state.Verifier)));
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = cfg.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = RedirectUri(http),
            ["response_mode"] = "query",
            ["scope"] = "openid profile email",
            ["state"] = state.Value,
            ["nonce"] = state.Nonce,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["prompt"] = mode == "reauth" ? "login" : "select_account",
        };
        if (mode == "reauth")
        {
            query["max_age"] = "0";
        }
        return Results.Redirect(Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(meta.AuthorizationEndpoint, query));
    }

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task<IResult> CallbackAsync(string? code, string? state, string? error, HttpContext http, EntraConfigStore store, IEntraClient client,
        PortalDbContext db, SessionService sessions, AuditLog audit, TimeProvider time)
    {
        var raw = http.Request.Cookies[StateCookie];
        http.Response.Cookies.Delete(StateCookie, new CookieOptions { Path = "/api/v1/auth/entra", Secure = true, SameSite = SameSiteMode.Lax });
        State? saved = null;
        try
        {
            saved = raw is null ? null : JsonSerializer.Deserialize<State>(Protector(http).ToTimeLimitedDataProtector().Unprotect(raw));
        }
        catch (CryptographicException)
        {
        }
        if (saved is null || state is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(saved.Value), Encoding.UTF8.GetBytes(state)))
        {
            await audit.WriteAsync(http.Audit("auth.login", AuditOutcome.Failure, details: new { method = "entra", reason = "invalid or expired state" }));
            return LoginError("state");
        }
        if (error is not null || code is null)
        {
            // The user cancelled or Entra refused (e.g. user not assigned to the app). error is a short OAuth code.
            await audit.WriteAsync(http.Audit("auth.login", AuditOutcome.Failure, details: new { method = "entra", reason = "idp error", error = error?[..Math.Min(error.Length, 64)] }));
            return saved.Mode == "reauth" ? Results.Redirect("/auth/reauth-done?status=cancelled") : LoginError("denied");
        }
        var cfg = await store.GetAsync(http.RequestAborted);
        if (!cfg.IsActive)
        {
            return LoginError("not_configured");
        }
        EntraIdentity identity;
        try
        {
            var meta = await client.GetConfigurationAsync(cfg.Authority, cfg.TenantId!, http.RequestAborted);
            var idToken = await client.RedeemCodeAsync(meta.TokenEndpoint, cfg.ClientId!, store.ClientSecret(cfg)!, code, RedirectUri(http), saved.Verifier, http.RequestAborted);
            identity = await EntraTokenValidator.ValidateAsync(idToken, meta, cfg.TenantId!, cfg.ClientId!, saved.Nonce, time);
        }
        catch (EntraSignInException ex)
        {
            await audit.WriteAsync(http.Audit("auth.login", AuditOutcome.Failure, details: new { method = "entra", reason = ex.Code, detail = ex.Message }));
            return LoginError(ex.Code);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException or TaskCanceledException or JsonException)
        {
            await audit.WriteAsync(http.Audit("auth.login", AuditOutcome.Failure, details: new { method = "entra", reason = "unreachable" }));
            return LoginError("unreachable");
        }

        // Pre-provisioned users only: bound by object id, or by UPN at the first sign-in.
        var user = await db.Users.FirstOrDefaultAsync(u => u.AuthSource == "entra" && u.ExternalTenantId == identity.TenantId && u.ExternalObjectId == identity.ObjectId, http.RequestAborted);
        if (user is null && identity.UserName is { Length: > 0 } upn)
        {
            var normalized = ApiSupport.NormalizeUserName(upn);
            user = await db.Users.FirstOrDefaultAsync(u => u.AuthSource == "entra" && u.NormalizedUserName == normalized && u.ExternalObjectId == null, http.RequestAborted);
            if (user is not null)
            {
                user.ExternalObjectId = identity.ObjectId;
                user.ExternalTenantId = identity.TenantId;
                await db.SaveChangesAsync(http.RequestAborted);
                await audit.WriteAsync(http.Audit("users.entra.bound", resourceType: "user", resourceId: user.Id.ToString("D"),
                    details: new { user.UserName, objectId = identity.ObjectId }, actorOverride: user.Id, actorNameOverride: user.UserName));
            }
        }
        if (user is null)
        {
            await audit.WriteAsync(http.Audit("auth.login", AuditOutcome.Denied, details: new { method = "entra", reason = "not provisioned", userName = identity.UserName }));
            return LoginError("not_provisioned");
        }
        if (!user.IsEnabled)
        {
            await audit.WriteAsync(http.Audit("auth.login", AuditOutcome.Denied, "user", user.Id.ToString("D"), new { method = "entra", reason = "disabled" }, user.Id, user.UserName));
            return LoginError("disabled");
        }

        if (saved.Mode == "reauth")
        {
            var now = time.GetUtcNow();
            var updated = await db.Sessions.Where(s => s.Id == saved.SessionId && s.UserId == user.Id && s.RevokedAt == null && s.ExpiresAt > now)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.ReauthenticatedAt, now), http.RequestAborted);
            await audit.WriteAsync(http.Audit("auth.reauthenticate", updated == 1 ? AuditOutcome.Success : AuditOutcome.Denied, "user", user.Id.ToString("D"),
                new { method = "entra" }, user.Id, user.UserName));
            return Results.Redirect("/auth/reauth-done?status=" + (updated == 1 ? "ok" : "mismatch"));
        }

        user.LastLoginAt = time.GetUtcNow();
        await db.SaveChangesAsync(http.RequestAborted);
        // MFA is enforced by Entra ID (Conditional Access); the portal does not add a local authenticator for these accounts.
        await sessions.SignInAsync(user, "entra", mfaSatisfied: true, http, http.RequestAborted);
        await audit.WriteAsync(http.Audit("auth.login", resourceType: "user", resourceId: user.Id.ToString("D"),
            details: new { method = "entra" }, actorOverride: user.Id, actorNameOverride: user.UserName));
        return Results.Redirect(saved.ReturnUrl);
    }
}
