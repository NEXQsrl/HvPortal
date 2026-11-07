using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using HvPortal.Gateway.Api;
using HvPortal.Gateway.Audit;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Security;
using Microsoft.EntityFrameworkCore;

namespace HvPortal.Gateway.VmBackup;

/// <summary>
/// API tokens for monitoring systems (ADR-032). A token is "hvp_" + 40 random characters, shown once; the database keeps
/// only its SHA-256. Tokens are read-only and scoped (backup.read): they open /api/v1/public/* only, never the rest of
/// the API (no session, no cookie, no antiforgery bypass for anything else). Creation needs settings.manage and a recent
/// re-authentication; every creation and revocation is audited, uses update the "last used" time.
/// </summary>
public static class ApiTokenEndpoints
{
    public const string Prefix = "hvp_";
    public const string BackupRead = "backup.read";
    public static readonly string[] Scopes = [BackupRead];

    public sealed record CreateBody(string? Name, int? ExpiresInDays, string[]? Scopes);

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/api-tokens").WithTags("API tokens");
        g.MapGet("/", ListAsync);
        g.MapPost("/", CreateAsync);
        g.MapPost("/{id:guid}/revoke", RevokeAsync);

        // Public read-only API: authenticated by the bearer token only.
        var pub = app.MapGroup("/api/v1/public").WithTags("Public API").AllowAnonymous().RequireRateLimiting("api-token");
        pub.MapGet("/backup/status", StatusAsync);
        pub.MapGet("/backup/runs", PublicRunsAsync);
    }

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static string NewToken()
    {
        const string alphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var chars = new char[40];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }
        return Prefix + new string(chars);
    }

    private static async Task<IResult> ListAsync(HttpContext http, PortalDbContext db)
    {
        if (await http.GuardAsync(Permissions.SettingsManage, ResourceRef.Global, "api_tokens.list") is { } denied)
        {
            return denied;
        }
        var users = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.UserName, http.RequestAborted);
        var tokens = await db.ApiTokens.AsNoTracking().OrderByDescending(t => t.CreatedAt).ToListAsync(http.RequestAborted);
        return Results.Ok(tokens.Select(t => new
        {
            t.Id, t.Name, t.Prefix, scopes = t.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries), createdBy = users.GetValueOrDefault(t.CreatedByUserId),
            t.CreatedAt, t.ExpiresAt, t.LastUsedAt, t.LastUsedIp, t.RevokedAt,
        }));
    }

    private static async Task<IResult> CreateAsync(CreateBody body, HttpContext http, PortalDbContext db, TimeProvider time)
    {
        if (await http.GuardAsync(Permissions.SettingsManage, ResourceRef.Global, "api_tokens.create") is { } denied)
        {
            return denied;
        }
        if (await http.RequireRecentReauthAsync() is { } reauth)
        {
            return reauth;
        }
        var name = body.Name?.Trim() ?? "";
        if (!BackupRules.NameRegex().IsMatch(name))
        {
            return ApiSupport.BadRequest("invalid_token_name");
        }
        var scopes = (body.Scopes is { Length: > 0 } s ? s : [BackupRead]).Distinct().ToList();
        if (scopes.Any(x => !Scopes.Contains(x)))
        {
            return ApiSupport.BadRequest("invalid_scope", "Available scope: backup.read.");
        }
        if (body.ExpiresInDays is < 1 or > 3650)
        {
            return ApiSupport.BadRequest("invalid_expiry", "1 to 3650 days, or no expiry.");
        }
        if (await db.ApiTokens.CountAsync(t => t.RevokedAt == null, http.RequestAborted) >= 100)
        {
            return ApiSupport.Conflict("too_many_tokens");
        }
        var token = NewToken();
        var now = time.GetUtcNow();
        var row = new ApiToken
        {
            Name = name, TokenHash = Hash(token), Prefix = token[..8], Scopes = string.Join(' ', scopes), CreatedByUserId = http.User.UserId(), CreatedAt = now,
            ExpiresAt = body.ExpiresInDays is { } d ? now.AddDays(d) : null,
        };
        db.ApiTokens.Add(row);
        await db.SaveChangesAsync(http.RequestAborted);
        await http.WriteAuditAsync("api_tokens.create", resourceType: "api_token", resourceId: row.Id.ToString("D"), details: new { row.Name, row.Prefix, scopes, row.ExpiresAt });
        // The only time the token is shown.
        return Results.Ok(new { row.Id, row.Name, token, row.ExpiresAt, scopes });
    }

    private static async Task<IResult> RevokeAsync(Guid id, HttpContext http, PortalDbContext db, TimeProvider time)
    {
        if (await http.GuardAsync(Permissions.SettingsManage, ResourceRef.Global, "api_tokens.revoke") is { } denied)
        {
            return denied;
        }
        var row = await db.ApiTokens.FirstOrDefaultAsync(t => t.Id == id, http.RequestAborted);
        if (row is null)
        {
            return ApiSupport.NotFound();
        }
        row.RevokedAt ??= time.GetUtcNow();
        await db.SaveChangesAsync(http.RequestAborted);
        await http.WriteAuditAsync("api_tokens.revoke", resourceType: "api_token", resourceId: id.ToString("D"), details: new { row.Name, row.Prefix });
        return Results.NoContent();
    }

    /// <summary>The token of the request when it is valid for the scope; failures are audited (at most one entry per token prefix and minute).</summary>
    internal static async Task<ApiToken?> AuthenticateAsync(HttpContext http, PortalDbContext db, TimeProvider time, string scope)
    {
        var header = http.Request.Headers.Authorization.ToString();
        const string bearer = "Bearer ";
        if (!header.StartsWith(bearer, StringComparison.OrdinalIgnoreCase) || header.Length > 200)
        {
            return null;
        }
        var token = header[bearer.Length..].Trim();
        if (!token.StartsWith(Prefix, StringComparison.Ordinal) || token.Length != Prefix.Length + 40)
        {
            return null;
        }
        var hash = Hash(token);
        var now = time.GetUtcNow();
        var row = await db.ApiTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, http.RequestAborted);
        // A token works only while the user who created it is enabled (a disabled or deleted user's tokens stop).
        var creatorEnabled = row is not null && await db.Users.AsNoTracking().AnyAsync(u => u.Id == row.CreatedByUserId && u.IsEnabled, http.RequestAborted);
        if (row is null || row.RevokedAt is not null || row.ExpiresAt <= now || !row.Scopes.Split(' ').Contains(scope) || !creatorEnabled)
        {
            var prefix = token[..Math.Min(8, token.Length)];
            if (http.RequestServices.GetService<ApiTokenAuditThrottle>()?.ShouldAudit(prefix, now) ?? true)
            {
                await http.WriteAuditAsync("api_tokens.use", AuditOutcome.Denied, "api_token", row?.Id.ToString("D"), new
                {
                    prefix,
                    reason = row is null ? "unknown" : row.RevokedAt is not null ? "revoked" : row.ExpiresAt <= now ? "expired" : !creatorEnabled ? "creator_disabled" : "scope",
                });
            }
            return null;
        }
        // "Last used" with a minute of resolution keeps the database quiet under frequent polling.
        if (row.LastUsedAt is null || now - row.LastUsedAt > TimeSpan.FromMinutes(1))
        {
            row.LastUsedAt = now;
            row.LastUsedIp = http.ClientIp();
            await db.SaveChangesAsync(http.RequestAborted);
        }
        return row;
    }

    private static IResult Unauthorized(HttpContext http)
    {
        http.Response.Headers.WWWAuthenticate = "Bearer realm=\"HvPortal\"";
        return ApiSupport.Error(StatusCodes.Status401Unauthorized, "invalid_token", "A valid API token with the backup.read scope is required (Authorization: Bearer hvp_...).");
    }

    /// <summary>
    /// Backup status for monitoring: overall status (ok / warning / critical), each job with its last run, each protected
    /// VM with its newest restore point. Names only, no paths, credentials or host details.
    /// </summary>
    private static async Task<IResult> StatusAsync(HttpContext http, PortalDbContext db, TimeProvider time)
    {
        if (await AuthenticateAsync(http, db, time, BackupRead) is null)
        {
            return Unauthorized(http);
        }
        var now = time.GetUtcNow();
        var jobs = await db.BackupJobs.AsNoTracking().OrderBy(j => j.Name).ToListAsync(http.RequestAborted);
        var runs = (await db.BackupRuns.AsNoTracking().Where(r => r.BackupJobId != null).OrderByDescending(r => r.StartedAt).Take(5000).ToListAsync(http.RequestAborted))
            .GroupBy(r => r.BackupJobId).Select(g => g.First()).ToList();
        var points = await db.BackupPoints.AsNoTracking().Where(p => p.Status != BackupPointStatus.Deleted && p.Status != BackupPointStatus.Deleting).ToListAsync(http.RequestAborted);
        var latest = points.GroupBy(p => p.VmId).Select(g => g.OrderByDescending(p => p.CreatedAt).First()).ToList();
        var lastGood = points.Where(p => p.Status == BackupPointStatus.Ok).GroupBy(p => p.VmId).ToDictionary(g => g.Key, g => g.Max(p => p.CreatedAt));
        var jobItems = jobs.Select(j =>
        {
            var last = runs.FirstOrDefault(r => r.BackupJobId == j.Id);
            return new
            {
                j.Name, j.Enabled, schedule = j.ScheduleKind, j.NextRunAt,
                lastRun = last is null ? null : new { status = last.Status.ToString().ToLowerInvariant(), last.StartedAt, last.CompletedAt, last.Total, last.Succeeded, last.Warnings, last.Failed },
            };
        }).ToList();
        var vmItems = latest.OrderBy(p => p.VmName).Select(p => new
        {
            vm = p.VmName, status = p.Status.ToString().ToLowerInvariant(), lastPointAt = p.CreatedAt,
            lastGoodPointAt = lastGood.TryGetValue(p.VmId, out var g) ? g : (DateTimeOffset?)null,
            hoursSinceGood = lastGood.TryGetValue(p.VmId, out var g2) ? Math.Round((now - g2).TotalHours, 1) : (double?)null,
            verified = p.VerifyOk, restoreTest = p.TestPassed,
        }).ToList();
        var failedJobs = jobItems.Count(j => j.Enabled && j.lastRun?.status == "failed");
        var warnJobs = jobItems.Count(j => j.Enabled && j.lastRun?.status == "warning");
        var staleVms = vmItems.Count(v => v.hoursSinceGood is null or > 48);
        var overall = failedJobs > 0 ? "critical" : warnJobs > 0 || staleVms > 0 ? "warning" : "ok";
        return Results.Ok(new
        {
            status = overall, generatedAt = now, failedJobs, warningJobs = warnJobs, vmsWithoutRecentBackup = staleVms,
            jobs = jobItems, vms = vmItems,
        });
    }

    private static async Task<IResult> PublicRunsAsync(DateTimeOffset? since, HttpContext http, PortalDbContext db, TimeProvider time)
    {
        if (await AuthenticateAsync(http, db, time, BackupRead) is null)
        {
            return Unauthorized(http);
        }
        var from = since ?? time.GetUtcNow().AddDays(-1);
        var names = await db.BackupJobs.AsNoTracking().ToDictionaryAsync(j => j.Id, j => j.Name, http.RequestAborted);
        var runs = await db.BackupRuns.AsNoTracking().Where(r => r.StartedAt >= from).OrderByDescending(r => r.StartedAt).Take(500).ToListAsync(http.RequestAborted);
        return Results.Ok(runs.Select(r => new
        {
            job = r.BackupJobId is { } j ? names.GetValueOrDefault(j) : null, r.Trigger, status = r.Status.ToString().ToLowerInvariant(), r.StartedAt, r.CompletedAt,
            r.Total, r.Succeeded, r.Warnings, r.Failed,
        }));
    }
}

/// <summary>At most one audit entry per token prefix and minute: a polling client with a wrong token does not flood the log.</summary>
public sealed class ApiTokenAuditThrottle
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _last = new(StringComparer.Ordinal);

    public bool ShouldAudit(string prefix, DateTimeOffset now)
    {
        if (_last.Count > 10_000)
        {
            _last.Clear();
        }
        if (_last.TryGetValue(prefix, out var last) && now - last < TimeSpan.FromMinutes(1))
        {
            return false;
        }
        _last[prefix] = now;
        return true;
    }
}
