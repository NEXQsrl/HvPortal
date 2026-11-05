using System.Text.RegularExpressions;
using HvPortal.Gateway.Audit;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Configuration;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HvPortal.Gateway.Api;

public static partial class ApiSupport
{
    public static IResult Error(int status, string code, string? detail = null) =>
        Results.Problem(statusCode: status, title: code, detail: detail, extensions: new Dictionary<string, object?> { ["code"] = code });

    public static IResult NotFound() => Error(StatusCodes.Status404NotFound, "not_found");

    public static IResult Forbidden(string code = "forbidden", string? detail = null) => Error(StatusCodes.Status403Forbidden, code, detail);

    public static IResult BadRequest(string code, string? detail = null) => Error(StatusCodes.Status400BadRequest, code, detail);

    public static IResult Conflict(string code, string? detail = null) => Error(StatusCodes.Status409Conflict, code, detail);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._@-]{2,63}$", RegexOptions.CultureInvariant)]
    public static partial Regex UserNameRegex();

    [GeneratedRegex(@"^[\p{L}\p{N}][\p{L}\p{N} ._()-]{0,63}$", RegexOptions.CultureInvariant)]
    public static partial Regex DisplayNameRegex();

    [GeneratedRegex(@"^[A-Za-z0-9._:-]{8,128}$", RegexOptions.CultureInvariant)]
    public static partial Regex IdempotencyKeyRegex();

    public static string NormalizeUserName(string userName) => userName.Trim().ToUpperInvariant();

    public static string ClientIp(this HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>Checks a permission; on denial writes a "Denied" audit entry and returns 403.</summary>
    public static async Task<IResult?> GuardAsync(this HttpContext http, string permission, ResourceRef resource, string action)
    {
        var access = http.RequestServices.GetRequiredService<AccessEvaluator>();
        if (await access.HasAsync(http.User.UserId(), permission, resource, http.RequestAborted))
        {
            return null;
        }
        await http.RequestServices.GetRequiredService<AuditLog>().WriteAsync(
            http.Audit(action, AuditOutcome.Denied, resource.Type.ToString().ToLowerInvariant(), resource.Id, new { permission }));
        return Forbidden("forbidden", $"Missing permission '{permission}' on {resource}.");
    }

    /// <summary>Critical actions require the password (and TOTP when enabled) to have been re-entered recently.</summary>
    public static async Task<IResult?> RequireRecentReauthAsync(this HttpContext http)
    {
        var db = http.RequestServices.GetRequiredService<PortalDbContext>();
        var time = http.RequestServices.GetRequiredService<TimeProvider>();
        var window = TimeSpan.FromMinutes(http.RequestServices.GetRequiredService<IOptions<GatewayOptions>>().Value.Security.ReauthenticationWindowMinutes);
        var sid = http.User.SessionId();
        var at = await db.Sessions.AsNoTracking().Where(s => s.Id == sid).Select(s => s.ReauthenticatedAt).FirstOrDefaultAsync(http.RequestAborted);
        return at is { } t && time.GetUtcNow() - t <= window
            ? null
            : Forbidden("reauthentication_required", "Confirm your password to continue.");
    }

    public static Task WriteAuditAsync(this HttpContext http, string action, AuditOutcome outcome = AuditOutcome.Success,
        string? resourceType = null, string? resourceId = null, object? details = null) =>
        http.RequestServices.GetRequiredService<AuditLog>().WriteAsync(http.Audit(action, outcome, resourceType, resourceId, details));

    /// <summary>Strong random temporary password (shown once, must be changed at first sign-in).</summary>
    public static string TemporaryPassword()
    {
        const string alphabet = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var chars = new char[24];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = alphabet[System.Security.Cryptography.RandomNumberGenerator.GetInt32(alphabet.Length)];
        }
        return $"{new string(chars, 0, 6)}-{new string(chars, 6, 6)}-{new string(chars, 12, 6)}-{new string(chars, 18, 6)}";
    }
}
