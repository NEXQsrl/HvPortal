using HvPortal.Gateway.Data;
using Microsoft.EntityFrameworkCore;

namespace HvPortal.Gateway.Security;

/// <summary>
/// Progressive delays instead of hard account lockout (a lockout would let anyone deny service to a known user name).
///  - key "ai:&lt;account&gt;|&lt;ip&gt;": after 3 failures, delay = 2^(n-3) s, capped at 15 min;
///  - key "ip:&lt;ip&gt;": after 20 failures from one address (any account), delay = 2^(n-20) s, capped at 15 min;
///  - key "a:&lt;account&gt;": never blocks; thresholds (10, 50, 200...) raise "auth.suspicious" audit events.
/// Unknown accounts are throttled exactly like known ones. Counters older than 24 h reset.
/// Process-wide fixed-window rate limiting (ASP.NET Core RateLimiter) applies in addition.
/// </summary>
public sealed class LoginThrottleService
{
    private static readonly TimeSpan Cap = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ResetAfter = TimeSpan.FromHours(24);
    private readonly PortalDbContext _db;
    private readonly TimeProvider _time;

    public LoginThrottleService(PortalDbContext db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    public async Task<TimeSpan?> GetRemainingDelayAsync(string account, string ip, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var keys = new[] { AccountIp(account, ip), Ip(ip) };
        var rows = await _db.LoginThrottles.AsNoTracking().Where(t => keys.Contains(t.Key)).ToListAsync(ct);
        var until = rows.Where(r => r.BlockedUntil > now).Select(r => r.BlockedUntil!.Value).DefaultIfEmpty(now).Max();
        return until > now ? until - now : null;
    }

    /// <summary>Returns the account-wide failure count (for suspicious-activity alerting).</summary>
    public async Task<int> RecordFailureAsync(string account, string ip, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        await BumpAsync(AccountIp(account, ip), 3, now, ct);
        await BumpAsync(Ip(ip), 20, now, ct);
        var total = await BumpAsync(Account(account), int.MaxValue, now, ct);
        await _db.SaveChangesAsync(ct);
        return total;
    }

    public async Task RecordSuccessAsync(string account, string ip, CancellationToken ct)
    {
        var key = AccountIp(account, ip);
        await _db.LoginThrottles.Where(t => t.Key == key).ExecuteDeleteAsync(ct);
    }

    public static bool IsAlertThreshold(int failures) => failures is 10 or 50 or 200 or 1000;

    private async Task<int> BumpAsync(string key, int threshold, DateTimeOffset now, CancellationToken ct)
    {
        var row = await _db.LoginThrottles.FirstOrDefaultAsync(t => t.Key == key, ct);
        if (row is null)
        {
            row = new LoginThrottle { Key = key };
            _db.LoginThrottles.Add(row);
        }
        else if (now - row.LastFailureAt > ResetAfter)
        {
            row.Failures = 0;
            row.BlockedUntil = null;
        }
        row.Failures++;
        row.LastFailureAt = now;
        if (row.Failures >= threshold)
        {
            var exp = Math.Min(row.Failures - threshold, 20);
            var delay = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, exp), Cap.TotalSeconds));
            row.BlockedUntil = now + delay;
        }
        return row.Failures;
    }

    private static string Norm(string account) => account.Trim().ToUpperInvariant();
    private static string AccountIp(string account, string ip) => $"ai:{Norm(account)}|{ip}";
    private static string Ip(string ip) => $"ip:{ip}";
    private static string Account(string account) => $"a:{Norm(account)}";
}
