using System.Security.Claims;
using HvPortal.Gateway.Audit;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace HvPortal.Gateway.Security;

public static class Restrictions
{
    public const string PasswordChange = "password_change";
    public const string MfaEnrollment = "mfa_enrollment";
}

/// <summary>
/// Server-side sessions. The cookie only carries (user id, session id); every request re-validates the session
/// row (revoked? idle/absolute timeout? security stamp changed? user disabled?) and rebuilds the principal, so
/// revocation is immediate.
/// </summary>
public sealed class SessionService
{
    private readonly PortalDbContext _db;
    private readonly TimeProvider _time;
    private readonly SettingsStore _settings;

    public SessionService(PortalDbContext db, TimeProvider time, SettingsStore settings)
    {
        _db = db;
        _time = time;
        _settings = settings;
    }

    public async Task<UserSession> SignInAsync(User user, string authMethods, bool mfaSatisfied, HttpContext http, CancellationToken ct)
    {
        var settings = await _settings.GetSecurityAsync(ct);
        var now = _time.GetUtcNow();
        var ua = http.Request.Headers.UserAgent.ToString();
        var session = new UserSession
        {
            UserId = user.Id,
            SecurityStamp = user.SecurityStamp,
            CreatedAt = now,
            LastSeenAt = now,
            ExpiresAt = now.AddHours(settings.SessionAbsoluteHours),
            AuthMethods = authMethods,
            MfaSatisfied = mfaSatisfied,
            ReauthenticatedAt = now,
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
            UserAgent = ua.Length > 256 ? ua[..256] : ua,
        };
        _db.Sessions.Add(session);
        user.LastLoginAt = now;
        await _db.SaveChangesAsync(ct);

        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, BuildPrincipal(user, session, null),
            new AuthenticationProperties { IsPersistent = false, AllowRefresh = false, ExpiresUtc = session.ExpiresAt });
        return session;
    }

    public async Task<int> RevokeAllAsync(Guid userId, string reason, Guid? exceptSessionId, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        return await _db.Sessions
            .Where(s => s.UserId == userId && s.RevokedAt == null && (exceptSessionId == null || s.Id != exceptSessionId))
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.RevokedAt, now).SetProperty(s => s.RevokedReason, reason), ct);
    }

    public async Task RevokeAsync(Guid sessionId, string reason, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        await _db.Sessions.Where(s => s.Id == sessionId && s.RevokedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.RevokedAt, now).SetProperty(s => s.RevokedReason, reason), ct);
    }

    public static ClaimsPrincipal BuildPrincipal(User user, UserSession session, string? restriction)
    {
        var claims = new List<Claim>
        {
            new(PortalClaims.UserId, user.Id.ToString("D")),
            new(PortalClaims.SessionId, session.Id.ToString("D")),
            new(PortalClaims.UserName, user.UserName),
            new(PortalClaims.AuthMethods, session.AuthMethods),
            // Used by antiforgery and SignalR user routing.
            new(ClaimTypes.NameIdentifier, user.Id.ToString("D")),
        };
        if (restriction is not null)
        {
            claims.Add(new Claim(PortalClaims.Restriction, restriction));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme, PortalClaims.UserName, null));
    }

    /// <summary>Cookie OnValidatePrincipal handler.</summary>
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var http = context.HttpContext;
        var principal = context.Principal;
        if (principal is null
            || !Guid.TryParse(principal.FindFirstValue(PortalClaims.UserId), out var userId)
            || !Guid.TryParse(principal.FindFirstValue(PortalClaims.SessionId), out var sessionId))
        {
            context.RejectPrincipal();
            return;
        }

        var sp = http.RequestServices;
        var db = sp.GetRequiredService<PortalDbContext>();
        var time = sp.GetRequiredService<TimeProvider>();
        var settings = await sp.GetRequiredService<SettingsStore>().GetSecurityAsync(http.RequestAborted);
        var now = time.GetUtcNow();

        var session = await db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId, http.RequestAborted);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, http.RequestAborted);
        var valid = session is not null && user is not null
                    && session.RevokedAt is null
                    && session.ExpiresAt > now
                    && now - session.LastSeenAt <= TimeSpan.FromMinutes(settings.SessionIdleMinutes)
                    && user.IsEnabled
                    && Tokens.FixedTimeEqualsHex(user.SecurityStamp, session.SecurityStamp);
        if (!valid)
        {
            context.RejectPrincipal();
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }

        if (now - session!.LastSeenAt > TimeSpan.FromSeconds(60))
        {
            await db.Sessions.Where(s => s.Id == sessionId).ExecuteUpdateAsync(u => u.SetProperty(s => s.LastSeenAt, now), http.RequestAborted);
        }

        string? restriction = null;
        if (user!.MustChangePassword)
        {
            restriction = Restrictions.PasswordChange;
        }
        else if (await IsMfaRequiredAsync(user, settings, sp.GetRequiredService<AccessEvaluator>(), http.RequestAborted)
                 && (!user.TotpEnabled || !session.MfaSatisfied))
        {
            restriction = Restrictions.MfaEnrollment;
        }

        context.ReplacePrincipal(BuildPrincipal(user, session, restriction));
        context.ShouldRenew = false;
    }

    public static async Task<bool> IsMfaRequiredAsync(User user, SecuritySettings settings, AccessEvaluator access, CancellationToken ct) =>
        settings.MfaRequiredFor switch
        {
            MfaRequirement.All => true,
            MfaRequirement.Privileged => user.IsBreakGlass || await access.IsPrivilegedAsync(user.Id, ct),
            _ => false,
        };
}

public static class PrincipalExtensions
{
    public static Guid UserId(this ClaimsPrincipal p) =>
        Guid.TryParse(p.FindFirstValue(PortalClaims.UserId), out var g) ? g : throw new UnauthorizedAccessException();

    public static Guid SessionId(this ClaimsPrincipal p) =>
        Guid.TryParse(p.FindFirstValue(PortalClaims.SessionId), out var g) ? g : throw new UnauthorizedAccessException();
}
