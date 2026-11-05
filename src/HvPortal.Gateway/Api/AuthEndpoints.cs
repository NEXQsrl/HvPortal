using HvPortal.Gateway.Audit;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Bootstrap;
using HvPortal.Gateway.Configuration;
using HvPortal.Gateway.Crypto;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Realtime;
using HvPortal.Gateway.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace HvPortal.Gateway.Api;

public static class AuthEndpoints
{
    public sealed record SetupRequest(string SetupToken, string UserName, string? DisplayName, string Password);
    public sealed record LoginRequest(string UserName, string Password);
    public sealed record MfaLoginRequest(string Challenge, string? Code, string? RecoveryCode);
    public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
    public sealed record ReauthRequest(string Password, string? Code);
    public sealed record RecoverRequest(string Token, string UserName, string NewPassword);
    public sealed record LoginResponse(string Status, string? Challenge = null, string? Restriction = null);

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/v1").WithTags("Authentication");

        g.MapGet("/auth/state", async (PortalDbContext db, HttpContext http) => Results.Ok(new
        {
            setupRequired = !await db.Users.AnyAsync(http.RequestAborted),
            authenticated = http.User.Identity?.IsAuthenticated == true,
            providers = new[] { "local" },
            version = typeof(AuthEndpoints).Assembly.GetName().Version?.ToString(),
        })).AllowAnonymous();

        g.MapGet("/auth/csrf", (IAntiforgery af, HttpContext http) =>
        {
            var tokens = af.GetAndStoreTokens(http);
            return Results.Ok(new { token = tokens.RequestToken });
        }).AllowAnonymous();

        g.MapPost("/setup", SetupAsync).AllowAnonymous().RequireRateLimiting("auth");
        g.MapPost("/auth/login", LoginAsync).AllowAnonymous().RequireRateLimiting("auth");
        g.MapPost("/auth/login/mfa", LoginMfaAsync).AllowAnonymous().RequireRateLimiting("auth");
        g.MapPost("/auth/recover", RecoverAsync).AllowAnonymous().RequireRateLimiting("auth");

        g.MapPost("/auth/logout", async (HttpContext http, SessionService sessions) =>
        {
            await sessions.RevokeAsync(http.User.SessionId(), "logout", http.RequestAborted);
            await http.WriteAuditAsync("auth.logout");
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        }).RequireAuthorization(AuthPolicies.AnySession);

        g.MapGet("/auth/me", MeAsync).RequireAuthorization(AuthPolicies.AnySession);
        g.MapPost("/auth/password", ChangePasswordAsync).RequireAuthorization(AuthPolicies.AnySession).RequireRateLimiting("auth");
        g.MapPost("/auth/reauth", ReauthAsync).RequireAuthorization(AuthPolicies.AnySession).RequireRateLimiting("auth");

        g.MapGet("/auth/sessions", async (HttpContext http, PortalDbContext db, TimeProvider time) =>
        {
            var uid = http.User.UserId();
            var current = http.User.SessionId();
            var now = time.GetUtcNow();
            var list = await db.Sessions.AsNoTracking().Where(s => s.UserId == uid && s.RevokedAt == null && s.ExpiresAt > now)
                .OrderByDescending(s => s.LastSeenAt).Take(50).ToListAsync(http.RequestAborted);
            return Results.Ok(list.Select(s => new { s.Id, s.CreatedAt, s.LastSeenAt, s.ExpiresAt, s.IpAddress, s.UserAgent, s.AuthMethods, current = s.Id == current }));
        });

        g.MapDelete("/auth/sessions/{id:guid}", async (Guid id, HttpContext http, PortalDbContext db, SessionService sessions) =>
        {
            var uid = http.User.UserId();
            if (!await db.Sessions.AnyAsync(s => s.Id == id && s.UserId == uid, http.RequestAborted))
            {
                return ApiSupport.NotFound();
            }
            await sessions.RevokeAsync(id, "revoked by user", http.RequestAborted);
            await http.WriteAuditAsync("auth.session.revoked", resourceType: "session", resourceId: id.ToString("D"));
            return Results.NoContent();
        });
    }

    private static async Task<IResult> SetupAsync(SetupRequest req, HttpContext http, PortalDbContext db, PasswordHasher hasher,
        SettingsStore settings, SessionService sessions, TimeProvider time, GatewayPaths paths, AuditLog audit)
    {
        if (await db.Users.AnyAsync(http.RequestAborted))
        {
            return ApiSupport.Conflict("setup_completed");
        }
        var now = time.GetUtcNow();
        var hash = Tokens.Hash(req.SetupToken ?? "");
        var token = await db.OneTimeTokens.FirstOrDefaultAsync(t => t.Purpose == BootstrapService.SetupPurpose && t.TokenHash == hash, http.RequestAborted);
        if (token is null || token.ConsumedAt is not null || token.ExpiresAt <= now)
        {
            await audit.WriteAsync(http.Audit("setup.complete", AuditOutcome.Denied, details: new { reason = "invalid setup token" }));
            return ApiSupport.BadRequest("invalid_setup_token", "The setup token is invalid or expired. Restart the service to issue a new one.");
        }
        if (req.UserName is null || !ApiSupport.UserNameRegex().IsMatch(req.UserName))
        {
            return ApiSupport.BadRequest("invalid_user_name", "3-64 characters: letters, digits, '.', '_', '-', '@'.");
        }
        if (req.DisplayName is { Length: > 0 } dn && !ApiSupport.DisplayNameRegex().IsMatch(dn))
        {
            return ApiSupport.BadRequest("invalid_display_name");
        }
        var policy = new PasswordPolicy((await settings.GetSecurityAsync(http.RequestAborted)).PasswordMinLength);
        var check = policy.Check(req.Password, req.UserName);
        if (!check.IsValid)
        {
            return ApiSupport.BadRequest(check.Code!, check.Message);
        }

        var platformAdmin = await db.Roles.FirstAsync(r => r.BuiltInKey == BuiltInRoles.PlatformAdmin, http.RequestAborted);
        await using var tx = await db.Database.BeginTransactionAsync(http.RequestAborted);
        var consumed = await db.OneTimeTokens.Where(t => t.Id == token.Id && t.ConsumedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(t => t.ConsumedAt, now), http.RequestAborted);
        if (consumed != 1)
        {
            return ApiSupport.BadRequest("invalid_setup_token");
        }
        var user = new User
        {
            UserName = req.UserName,
            NormalizedUserName = ApiSupport.NormalizeUserName(req.UserName),
            DisplayName = req.DisplayName,
            PasswordHash = hasher.Hash(req.Password),
            PasswordChangedAt = now,
            CreatedAt = now,
        };
        db.Users.Add(user);
        db.RoleAssignments.Add(new RoleAssignment
        {
            PrincipalId = user.Id.ToString("D"),
            RoleId = platformAdmin.Id,
            ScopeType = ScopeType.Global,
            ScopeId = "",
            CreatedAt = now,
        });
        await db.SaveChangesAsync(http.RequestAborted);
        await tx.CommitAsync(http.RequestAborted);

        if (File.Exists(paths.SetupTokenFile))
        {
            File.Delete(paths.SetupTokenFile);
        }
        await audit.WriteAsync(http.Audit("setup.complete", actorOverride: user.Id, actorNameOverride: user.UserName,
            details: new { role = BuiltInRoles.PlatformAdmin }));
        // The session is restricted to MFA enrollment by policy (administrators must use MFA by default).
        await sessions.SignInAsync(user, "setup", mfaSatisfied: false, http, http.RequestAborted);
        return Results.Ok(new LoginResponse("ok"));
    }

    private static async Task<IResult> LoginAsync(LoginRequest req, HttpContext http, PortalDbContext db, PasswordHasher hasher,
        LoginThrottleService throttle, SessionService sessions, SettingsStore settings, TimeProvider time, AuditLog audit)
    {
        var ip = http.ClientIp();
        var account = (req.UserName ?? "").Trim();
        if (account.Length is 0 or > 128 || req.Password is null || req.Password.Length > PasswordPolicy.Maximum * 4)
        {
            return ApiSupport.Error(StatusCodes.Status401Unauthorized, "invalid_credentials");
        }
        if (await throttle.GetRemainingDelayAsync(account, ip, http.RequestAborted) is { } delay)
        {
            http.Response.Headers.RetryAfter = ((int)Math.Ceiling(delay.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return ApiSupport.Error(StatusCodes.Status429TooManyRequests, "throttled", $"Too many attempts. Retry in {Math.Ceiling(delay.TotalSeconds)} s.");
        }

        var normalized = ApiSupport.NormalizeUserName(account);
        var user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedUserName == normalized && u.AuthSource == "local", http.RequestAborted);
        var ok = user is not null && user.IsEnabled && hasher.Verify(req.Password, user.PasswordHash);
        if (user is null || !user.IsEnabled)
        {
            hasher.SimulateVerify(req.Password);
        }
        if (!ok)
        {
            var failures = await throttle.RecordFailureAsync(account, ip, http.RequestAborted);
            // The attempted name is recorded only when it matches an existing account (it could be a mistyped password).
            await audit.WriteAsync(http.Audit("auth.login", AuditOutcome.Failure, "user", user?.Id.ToString("D"),
                new { reason = "invalid credentials" }, actorOverride: user?.Id, actorNameOverride: user?.UserName));
            if (LoginThrottleService.IsAlertThreshold(failures))
            {
                await audit.WriteAsync(http.Audit("auth.suspicious", AuditOutcome.Failure, "user", user?.Id.ToString("D"),
                    new { failures, scope = "account" }, actorOverride: user?.Id, actorNameOverride: user?.UserName));
            }
            return ApiSupport.Error(StatusCodes.Status401Unauthorized, "invalid_credentials");
        }

        var security = await settings.GetSecurityAsync(http.RequestAborted);
        if (security.LocalSignIn == "break-glass-only" && !user!.IsBreakGlass)
        {
            await audit.WriteAsync(http.Audit("auth.login", AuditOutcome.Denied, "user", user.Id.ToString("D"),
                new { reason = "local sign-in restricted to break-glass accounts" }, user.Id, user.UserName));
            return ApiSupport.Forbidden("local_signin_disabled", "Local sign-in is disabled by policy. Use the organization identity provider.");
        }

        await throttle.RecordSuccessAsync(account, ip, http.RequestAborted);
        if (hasher.NeedsRehash(user!.PasswordHash!))
        {
            user.PasswordHash = hasher.Hash(req.Password);
            await db.SaveChangesAsync(http.RequestAborted);
        }

        if (user.TotpEnabled)
        {
            var challenge = Tokens.New();
            db.MfaChallenges.Add(new MfaChallenge
            {
                TokenHash = Tokens.Hash(challenge),
                UserId = user.Id,
                ExpiresAt = time.GetUtcNow().AddMinutes(5),
                IpAddress = ip,
            });
            await db.SaveChangesAsync(http.RequestAborted);
            await audit.WriteAsync(http.Audit("auth.login.password_verified", resourceType: "user", resourceId: user.Id.ToString("D"),
                actorOverride: user.Id, actorNameOverride: user.UserName));
            return Results.Ok(new LoginResponse("mfa_required", challenge));
        }

        await sessions.SignInAsync(user, "pwd", mfaSatisfied: false, http, http.RequestAborted);
        await audit.WriteAsync(http.Audit("auth.login", resourceType: "user", resourceId: user.Id.ToString("D"),
            details: new { mfa = false, breakGlass = user.IsBreakGlass }, actorOverride: user.Id, actorNameOverride: user.UserName));
        return Results.Ok(new LoginResponse("ok", Restriction: user.MustChangePassword ? Restrictions.PasswordChange : null));
    }

    private static async Task<IResult> LoginMfaAsync(MfaLoginRequest req, HttpContext http, PortalDbContext db, KeyRing keys,
        RecoveryCodeService recovery, LoginThrottleService throttle, SessionService sessions, TimeProvider time, AuditLog audit)
    {
        var now = time.GetUtcNow();
        var ip = http.ClientIp();
        var hash = Tokens.Hash(req.Challenge ?? "");
        var challenge = await db.MfaChallenges.FirstOrDefaultAsync(c => c.TokenHash == hash, http.RequestAborted);
        if (challenge is null || challenge.ConsumedAt is not null || challenge.ExpiresAt <= now || challenge.Attempts >= 5)
        {
            return ApiSupport.Error(StatusCodes.Status401Unauthorized, "mfa_challenge_invalid", "Sign in again.");
        }
        var user = await db.Users.FirstAsync(u => u.Id == challenge.UserId, http.RequestAborted);
        if (await throttle.GetRemainingDelayAsync(user.UserName, ip, http.RequestAborted) is { } delay)
        {
            return ApiSupport.Error(StatusCodes.Status429TooManyRequests, "throttled", $"Retry in {Math.Ceiling(delay.TotalSeconds)} s.");
        }

        string? method = null;
        if (!string.IsNullOrWhiteSpace(req.Code) && user.TotpEnabled && user.TotpSecretEncrypted is not null)
        {
            var secret = keys.Decrypt(user.TotpSecretEncrypted, "totp");
            if (Totp.Verify(secret, req.Code, now, user.TotpLastUsedStep) is { } step)
            {
                user.TotpLastUsedStep = step;
                try
                {
                    await db.SaveChangesAsync(http.RequestAborted); // concurrency token: a parallel use of the same code loses
                    method = "pwd+totp";
                }
                catch (DbUpdateConcurrencyException)
                {
                    method = null;
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(req.RecoveryCode))
        {
            var codeHash = recovery.Hash(req.RecoveryCode);
            var used = await db.RecoveryCodes.Where(r => r.UserId == user.Id && r.CodeHash == codeHash && r.UsedAt == null)
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.UsedAt, now), http.RequestAborted);
            if (used == 1)
            {
                method = "pwd+recovery";
            }
        }

        if (method is null)
        {
            await db.MfaChallenges.Where(c => c.Id == challenge.Id).ExecuteUpdateAsync(u => u.SetProperty(c => c.Attempts, c => c.Attempts + 1), http.RequestAborted);
            await throttle.RecordFailureAsync(user.UserName, ip, http.RequestAborted);
            await audit.WriteAsync(http.Audit("auth.mfa", AuditOutcome.Failure, "user", user.Id.ToString("D"),
                new { reason = "invalid or reused code" }, user.Id, user.UserName));
            return ApiSupport.Error(StatusCodes.Status401Unauthorized, "invalid_mfa_code");
        }

        var consumed = await db.MfaChallenges.Where(c => c.Id == challenge.Id && c.ConsumedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.ConsumedAt, now), http.RequestAborted);
        if (consumed != 1)
        {
            return ApiSupport.Error(StatusCodes.Status401Unauthorized, "mfa_challenge_invalid");
        }
        await db.Entry(user).ReloadAsync(http.RequestAborted);
        await sessions.SignInAsync(user, method, mfaSatisfied: true, http, http.RequestAborted);
        var remaining = await db.RecoveryCodes.CountAsync(r => r.UserId == user.Id && r.UsedAt == null, http.RequestAborted);
        await audit.WriteAsync(http.Audit(method == "pwd+recovery" ? "auth.login.recovery_code" : "auth.login", resourceType: "user",
            resourceId: user.Id.ToString("D"), details: new { mfa = true, recoveryCodesRemaining = remaining, breakGlass = user.IsBreakGlass },
            actorOverride: user.Id, actorNameOverride: user.UserName));
        return Results.Ok(new { status = "ok", recoveryCodesRemaining = remaining });
    }

    private static async Task<IResult> MeAsync(HttpContext http, PortalDbContext db, AccessEvaluator access, SettingsStore settings, TimeProvider time)
    {
        var uid = http.User.UserId();
        var sid = http.User.SessionId();
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == uid, http.RequestAborted);
        var session = await db.Sessions.AsNoTracking().FirstAsync(s => s.Id == sid, http.RequestAborted);
        var security = await settings.GetSecurityAsync(http.RequestAborted);
        return Results.Ok(new
        {
            id = user.Id,
            userName = user.UserName,
            displayName = user.DisplayName,
            restriction = http.User.FindFirst(PortalClaims.Restriction)?.Value,
            mfaEnabled = user.TotpEnabled,
            mfaRequired = await SessionService.IsMfaRequiredAsync(user, security, access, http.RequestAborted),
            recoveryCodesRemaining = await db.RecoveryCodes.CountAsync(r => r.UserId == uid && r.UsedAt == null, http.RequestAborted),
            isBreakGlass = user.IsBreakGlass,
            authMethods = session.AuthMethods,
            sessionExpiresAt = session.ExpiresAt,
            reauthenticatedAt = session.ReauthenticatedAt,
            idleTimeoutMinutes = security.SessionIdleMinutes,
            passwordMinLength = security.PasswordMinLength,
            // UI hint only. Every API call is authorized server-side.
            globalPermissions = await access.GlobalPermissionsAsync(uid, http.RequestAborted),
        });
    }

    private static async Task<IResult> ChangePasswordAsync(ChangePasswordRequest req, HttpContext http, PortalDbContext db, PasswordHasher hasher,
        SettingsStore settings, SessionService sessions, LoginThrottleService throttle, TimeProvider time, AuditLog audit)
    {
        var uid = http.User.UserId();
        var user = await db.Users.FirstAsync(u => u.Id == uid, http.RequestAborted);
        if (!hasher.Verify(req.CurrentPassword ?? "", user.PasswordHash))
        {
            await throttle.RecordFailureAsync(user.UserName, http.ClientIp(), http.RequestAborted);
            await audit.WriteAsync(http.Audit("auth.password.change", AuditOutcome.Failure, "user", uid.ToString("D"), new { reason = "wrong current password" }));
            return ApiSupport.BadRequest("invalid_current_password");
        }
        var policy = new PasswordPolicy((await settings.GetSecurityAsync(http.RequestAborted)).PasswordMinLength);
        var check = policy.Check(req.NewPassword, user.UserName);
        if (!check.IsValid)
        {
            return ApiSupport.BadRequest(check.Code!, check.Message);
        }
        if (hasher.Verify(req.NewPassword, user.PasswordHash))
        {
            return ApiSupport.BadRequest("password_unchanged", "Choose a password different from the current one.");
        }
        var session = await db.Sessions.AsNoTracking().FirstAsync(s => s.Id == http.User.SessionId(), http.RequestAborted);
        user.PasswordHash = hasher.Hash(req.NewPassword);
        user.PasswordChangedAt = time.GetUtcNow();
        user.MustChangePassword = false;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        await db.SaveChangesAsync(http.RequestAborted);
        var revoked = await sessions.RevokeAllAsync(uid, "password changed", null, http.RequestAborted);
        await sessions.SignInAsync(user, session.AuthMethods, session.MfaSatisfied, http, http.RequestAborted);
        await audit.WriteAsync(http.Audit("auth.password.change", resourceType: "user", resourceId: uid.ToString("D"), details: new { sessionsRevoked = revoked }));
        return Results.Ok(new { status = "ok" });
    }

    private static async Task<IResult> ReauthAsync(ReauthRequest req, HttpContext http, PortalDbContext db, PasswordHasher hasher, KeyRing keys,
        LoginThrottleService throttle, TimeProvider time, AuditLog audit)
    {
        var uid = http.User.UserId();
        var user = await db.Users.FirstAsync(u => u.Id == uid, http.RequestAborted);
        var ip = http.ClientIp();
        if (await throttle.GetRemainingDelayAsync(user.UserName, ip, http.RequestAborted) is { } delay)
        {
            return ApiSupport.Error(StatusCodes.Status429TooManyRequests, "throttled", $"Retry in {Math.Ceiling(delay.TotalSeconds)} s.");
        }
        var now = time.GetUtcNow();
        var ok = hasher.Verify(req.Password ?? "", user.PasswordHash);
        if (ok && user.TotpEnabled)
        {
            var secret = keys.Decrypt(user.TotpSecretEncrypted!, "totp");
            if (Totp.Verify(secret, req.Code, now, user.TotpLastUsedStep) is { } step)
            {
                user.TotpLastUsedStep = step;
                try
                {
                    await db.SaveChangesAsync(http.RequestAborted);
                }
                catch (DbUpdateConcurrencyException)
                {
                    ok = false;
                }
            }
            else
            {
                ok = false;
            }
        }
        if (!ok)
        {
            await throttle.RecordFailureAsync(user.UserName, ip, http.RequestAborted);
            await audit.WriteAsync(http.Audit("auth.reauthenticate", AuditOutcome.Failure, "user", uid.ToString("D")));
            return ApiSupport.Error(StatusCodes.Status401Unauthorized, user.TotpEnabled ? "invalid_password_or_code" : "invalid_password");
        }
        var sid = http.User.SessionId();
        await db.Sessions.Where(s => s.Id == sid).ExecuteUpdateAsync(u => u.SetProperty(s => s.ReauthenticatedAt, now), http.RequestAborted);
        await audit.WriteAsync(http.Audit("auth.reauthenticate", resourceType: "user", resourceId: uid.ToString("D")));
        return Results.Ok(new { status = "ok", validUntil = now.AddMinutes(5) });
    }

    /// <summary>
    /// Account recovery with a one-time token produced on the gateway server by a local administrator:
    /// HvPortal.Gateway.exe admin recover-account --user &lt;name&gt; [--reset-mfa]. See docs/security-model.md.
    /// </summary>
    private static async Task<IResult> RecoverAsync(RecoverRequest req, HttpContext http, PortalDbContext db, PasswordHasher hasher,
        SettingsStore settings, SessionService sessions, TimeProvider time, AuditLog audit)
    {
        var now = time.GetUtcNow();
        var hash = Tokens.Hash(req.Token ?? "");
        var token = await db.OneTimeTokens.FirstOrDefaultAsync(t => t.TokenHash == hash && t.Purpose.StartsWith("account-recovery"), http.RequestAborted);
        var normalized = ApiSupport.NormalizeUserName(req.UserName ?? "");
        var user = token?.UserId is { } id ? await db.Users.FirstOrDefaultAsync(u => u.Id == id, http.RequestAborted) : null;
        if (token is null || token.ConsumedAt is not null || token.ExpiresAt <= now || user is null || user.NormalizedUserName != normalized)
        {
            await audit.WriteAsync(http.Audit("auth.recover", AuditOutcome.Denied, details: new { reason = "invalid recovery token" }));
            return ApiSupport.BadRequest("invalid_recovery_token");
        }
        var policy = new PasswordPolicy((await settings.GetSecurityAsync(http.RequestAborted)).PasswordMinLength);
        var check = policy.Check(req.NewPassword, user.UserName);
        if (!check.IsValid)
        {
            return ApiSupport.BadRequest(check.Code!, check.Message);
        }
        var consumed = await db.OneTimeTokens.Where(t => t.Id == token.Id && t.ConsumedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(t => t.ConsumedAt, now), http.RequestAborted);
        if (consumed != 1)
        {
            return ApiSupport.BadRequest("invalid_recovery_token");
        }
        var resetMfa = token.Purpose == "account-recovery+mfa";
        user.PasswordHash = hasher.Hash(req.NewPassword);
        user.PasswordChangedAt = now;
        user.MustChangePassword = false;
        user.IsEnabled = true;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        if (resetMfa)
        {
            user.TotpEnabled = false;
            user.TotpSecretEncrypted = null;
            user.PendingTotpSecretEncrypted = null;
            await db.RecoveryCodes.Where(r => r.UserId == user.Id).ExecuteDeleteAsync(http.RequestAborted);
        }
        await db.SaveChangesAsync(http.RequestAborted);
        await sessions.RevokeAllAsync(user.Id, "account recovery", null, http.RequestAborted);
        await audit.WriteAsync(http.Audit("auth.recover", resourceType: "user", resourceId: user.Id.ToString("D"),
            details: new { resetMfa }, actorOverride: user.Id, actorNameOverride: user.UserName));
        return Results.Ok(new { status = "ok" });
    }
}
