using HvPortal.Gateway.Audit;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Crypto;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Realtime;
using HvPortal.Gateway.Security;
using Microsoft.EntityFrameworkCore;
using QRCoder;

namespace HvPortal.Gateway.Api;

/// <summary>
/// TOTP enrollment. The secret is shown once, during enrollment only (as text and as a QR code rendered
/// locally by QRCoder — no external QR service). It is stored encrypted (AES-GCM via the key ring), never hashed,
/// because verification needs it. It becomes active only after a valid first code. Recovery codes are shown once.
/// </summary>
public static class MfaEndpoints
{
    public sealed record ConfirmRequest(string Code);

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/mfa").WithTags("MFA");

        g.MapPost("/totp/enroll", EnrollAsync).RequireAuthorization(AuthPolicies.AnySession);
        g.MapPost("/totp/confirm", ConfirmAsync).RequireAuthorization(AuthPolicies.AnySession).RequireRateLimiting("auth");
        g.MapPost("/totp/disable", DisableAsync);
        g.MapPost("/recovery-codes", RegenerateAsync);
    }

    private static async Task<IResult> EnrollAsync(HttpContext http, PortalDbContext db, KeyRing keys, TimeProvider time)
    {
        if (await http.RequireRecentReauthAsync() is { } reauth)
        {
            return reauth;
        }
        var uid = http.User.UserId();
        var user = await db.Users.FirstAsync(u => u.Id == uid, http.RequestAborted);
        if (user.TotpEnabled)
        {
            return ApiSupport.Conflict("mfa_already_enabled", "Disable the current authenticator first.");
        }
        var secret = Totp.NewSecret();
        user.PendingTotpSecretEncrypted = keys.Encrypt(secret, "totp");
        user.PendingTotpCreatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(http.RequestAborted);

        var uri = Totp.OtpAuthUri("HvPortal", user.UserName, secret);
        using var data = QRCodeGenerator.GenerateQrCode(uri, QRCodeGenerator.ECCLevel.M);
        var svg = new SvgQRCode(data).GetGraphic(6);
        await http.WriteAuditAsync("mfa.totp.enrollment_started", resourceType: "user", resourceId: uid.ToString("D"));
        return Results.Ok(new
        {
            secret = Totp.Base32Encode(secret),
            otpauthUri = uri,
            qrSvgDataUri = "data:image/svg+xml;base64," + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(svg)),
            digits = Totp.Digits,
            period = Totp.PeriodSeconds,
        });
    }

    private static async Task<IResult> ConfirmAsync(ConfirmRequest req, HttpContext http, PortalDbContext db, KeyRing keys,
        RecoveryCodeService recovery, SessionService sessions, TimeProvider time)
    {
        var uid = http.User.UserId();
        var user = await db.Users.FirstAsync(u => u.Id == uid, http.RequestAborted);
        var now = time.GetUtcNow();
        if (user.PendingTotpSecretEncrypted is null || user.PendingTotpCreatedAt is not { } created || now - created > TimeSpan.FromMinutes(15))
        {
            return ApiSupport.BadRequest("no_pending_enrollment", "Start the enrollment again.");
        }
        var secret = keys.Decrypt(user.PendingTotpSecretEncrypted, "totp");
        if (Totp.Verify(secret, req.Code, now, 0) is not { } step)
        {
            await http.WriteAuditAsync("mfa.totp.confirm", AuditOutcome.Failure, "user", uid.ToString("D"));
            return ApiSupport.BadRequest("invalid_mfa_code", "The code is not valid. Check the device clock and try again.");
        }

        user.TotpSecretEncrypted = keys.Encrypt(secret, "totp");
        user.TotpEnabled = true;
        user.TotpEnabledAt = now;
        user.TotpLastUsedStep = step;
        user.PendingTotpSecretEncrypted = null;
        user.PendingTotpCreatedAt = null;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        var codes = await ReplaceRecoveryCodesAsync(db, recovery, uid, now, http.RequestAborted);
        await db.SaveChangesAsync(http.RequestAborted);

        // Critical change: end every other session, continue in a new MFA-satisfied session.
        var session = await db.Sessions.AsNoTracking().FirstAsync(s => s.Id == http.User.SessionId(), http.RequestAborted);
        await sessions.RevokeAllAsync(uid, "mfa enabled", null, http.RequestAborted);
        await sessions.SignInAsync(user, session.AuthMethods + "+totp", mfaSatisfied: true, http, http.RequestAborted);
        await http.WriteAuditAsync("mfa.totp.enabled", resourceType: "user", resourceId: uid.ToString("D"));
        return Results.Ok(new { recoveryCodes = codes });
    }

    private static async Task<IResult> DisableAsync(HttpContext http, PortalDbContext db, SettingsStore settings, AccessEvaluator access, SessionService sessions)
    {
        if (await http.RequireRecentReauthAsync() is { } reauth)
        {
            return reauth;
        }
        var uid = http.User.UserId();
        var user = await db.Users.FirstAsync(u => u.Id == uid, http.RequestAborted);
        if (await SessionService.IsMfaRequiredAsync(user, await settings.GetSecurityAsync(http.RequestAborted), access, http.RequestAborted))
        {
            return ApiSupport.Conflict("mfa_required_by_policy", "MFA is mandatory for this account.");
        }
        user.TotpEnabled = false;
        user.TotpSecretEncrypted = null;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        await db.RecoveryCodes.Where(r => r.UserId == uid).ExecuteDeleteAsync(http.RequestAborted);
        await db.SaveChangesAsync(http.RequestAborted);
        var session = await db.Sessions.AsNoTracking().FirstAsync(s => s.Id == http.User.SessionId(), http.RequestAborted);
        await sessions.RevokeAllAsync(uid, "mfa disabled", null, http.RequestAborted);
        await sessions.SignInAsync(user, session.AuthMethods, false, http, http.RequestAborted);
        await http.WriteAuditAsync("mfa.totp.disabled", resourceType: "user", resourceId: uid.ToString("D"));
        return Results.NoContent();
    }

    private static async Task<IResult> RegenerateAsync(HttpContext http, PortalDbContext db, RecoveryCodeService recovery, TimeProvider time)
    {
        if (await http.RequireRecentReauthAsync() is { } reauth)
        {
            return reauth;
        }
        var uid = http.User.UserId();
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == uid, http.RequestAborted);
        if (!user.TotpEnabled)
        {
            return ApiSupport.Conflict("mfa_not_enabled");
        }
        var codes = await ReplaceRecoveryCodesAsync(db, recovery, uid, time.GetUtcNow(), http.RequestAborted);
        await db.SaveChangesAsync(http.RequestAborted);
        await http.WriteAuditAsync("mfa.recovery_codes.regenerated", resourceType: "user", resourceId: uid.ToString("D"));
        return Results.Ok(new { recoveryCodes = codes });
    }

    private static async Task<IReadOnlyList<string>> ReplaceRecoveryCodesAsync(PortalDbContext db, RecoveryCodeService recovery, Guid uid,
        DateTimeOffset now, CancellationToken ct)
    {
        await db.RecoveryCodes.Where(r => r.UserId == uid).ExecuteDeleteAsync(ct);
        var codes = recovery.Generate();
        foreach (var c in codes)
        {
            db.RecoveryCodes.Add(new RecoveryCode { UserId = uid, CodeHash = recovery.Hash(c), CreatedAt = now });
        }
        return codes;
    }
}
