using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HvPortal.Gateway.Crypto;
using HvPortal.Gateway.Data;
using Microsoft.EntityFrameworkCore;

namespace HvPortal.Gateway.Audit;

public sealed record AuditRecord
{
    public required string Action { get; init; }
    public AuditOutcome Outcome { get; init; } = AuditOutcome.Success;
    public Guid? ActorUserId { get; init; }
    public string? ActorName { get; init; }
    public string AuthMethod { get; init; } = "anonymous";
    public string? SourceIp { get; init; }
    public string? ResourceType { get; init; }
    public string? ResourceId { get; init; }
    public Guid CorrelationId { get; init; }
    /// <summary>Small, non-sensitive details. Never put passwords, tokens, secrets or codes here.</summary>
    public object? Details { get; init; }
}

/// <summary>
/// Security audit log. Each entry carries HMAC-SHA256(key, canonical(entry) || previousHash): any modification,
/// deletion or reordering of stored rows is detected by <see cref="VerifyAsync"/>.
/// Limits (documented): an attacker holding both database write access and the key ring can rebuild a valid
/// chain. Real tamper evidence requires exporting entries to an external system (export endpoint / SIEM)
/// — the in-product log is NOT described as immutable.
/// Entries are written in a separate DbContext so they persist even when the caller's operation fails.
/// </summary>
public sealed class AuditLog
{
    private static readonly SemaphoreSlim ChainLock = new(1, 1);
    private readonly IServiceScopeFactory _scopes;
    private readonly KeyRing _keys;
    private readonly TimeProvider _time;
    private readonly ILogger<AuditLog> _logger;

    public AuditLog(IServiceScopeFactory scopes, KeyRing keys, TimeProvider time, ILogger<AuditLog> logger)
    {
        _scopes = scopes;
        _keys = keys;
        _time = time;
        _logger = logger;
    }

    public async Task WriteAsync(AuditRecord record, CancellationToken ct = default)
    {
        await ChainLock.WaitAsync(CancellationToken.None);
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
            var previous = await db.AuditEntries.AsNoTracking().OrderByDescending(e => e.Id).Select(e => e.Hash).FirstOrDefaultAsync(CancellationToken.None)
                           ?? "GENESIS";
            var entry = new AuditEntry
            {
                // Millisecond precision: the stored value must round-trip exactly for hash verification.
                TimestampUtc = TruncateToMs(_time.GetUtcNow()),
                ActorUserId = record.ActorUserId,
                ActorName = Truncate(record.ActorName, 128),
                AuthMethod = record.AuthMethod,
                SourceIp = record.SourceIp,
                Action = record.Action,
                ResourceType = record.ResourceType,
                ResourceId = Truncate(record.ResourceId, 128),
                Outcome = record.Outcome,
                CorrelationId = record.CorrelationId == Guid.Empty ? Guid.NewGuid() : record.CorrelationId,
                DetailsJson = record.Details is null ? null : Truncate(JsonSerializer.Serialize(record.Details), 4000),
                KeyId = _keys.ActiveKeyId,
                PreviousHash = previous,
                Hash = "",
            };
            entry.Hash = ComputeHash(entry, _keys.DeriveKey("audit-chain", entry.KeyId));
            db.AuditEntries.Add(entry);
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Never silently lose audit: surface in diagnostics (event log) and rethrow to the caller.
            _logger.LogCritical(ex, "Failed to write audit entry {Action}", record.Action);
            throw;
        }
        finally
        {
            ChainLock.Release();
        }
    }

    public async Task<AuditVerification> VerifyAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
        var previous = "GENESIS";
        long count = 0;
        await foreach (var e in db.AuditEntries.AsNoTracking().OrderBy(e => e.Id).AsAsyncEnumerable().WithCancellation(ct))
        {
            count++;
            if (!_keys.HasKey(e.KeyId))
            {
                return new AuditVerification(false, count, e.Id, $"Key '{e.KeyId}' is not available.");
            }
            if (e.PreviousHash != previous)
            {
                return new AuditVerification(false, count, e.Id, "Chain link broken (entry removed, inserted or reordered).");
            }
            if (!Tokensafe(ComputeHash(e, _keys.DeriveKey("audit-chain", e.KeyId)), e.Hash))
            {
                return new AuditVerification(false, count, e.Id, "Entry content does not match its hash (modified).");
            }
            previous = e.Hash;
        }
        return new AuditVerification(true, count, null, null);
    }

    internal static string ComputeHash(AuditEntry e, byte[] key)
    {
        var canonical = string.Join('\n',
            e.TimestampUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            e.ActorUserId?.ToString("D") ?? "",
            e.ActorName ?? "",
            e.AuthMethod,
            e.SourceIp ?? "",
            e.Action,
            e.ResourceType ?? "",
            e.ResourceId ?? "",
            e.Outcome.ToString(),
            e.CorrelationId.ToString("D"),
            e.DetailsJson ?? "",
            e.KeyId,
            e.PreviousHash);
        return Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(canonical)));
    }

    private static bool Tokensafe(string a, string b) =>
        a.Length == b.Length && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];

    private static DateTimeOffset TruncateToMs(DateTimeOffset t) =>
        new(t.UtcTicks - (t.UtcTicks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);
}

public sealed record AuditVerification(bool Valid, long EntriesChecked, long? FirstInvalidId, string? Problem);

public static class AuditHttpExtensions
{
    public static AuditRecord Audit(this HttpContext http, string action, AuditOutcome outcome = AuditOutcome.Success,
        string? resourceType = null, string? resourceId = null, object? details = null, Guid? actorOverride = null, string? actorNameOverride = null)
    {
        var user = http.User;
        var id = user.FindFirstValue(PortalClaims.UserId);
        return new AuditRecord
        {
            Action = action,
            Outcome = outcome,
            ActorUserId = actorOverride ?? (Guid.TryParse(id, out var g) ? g : null),
            ActorName = actorNameOverride ?? user.FindFirstValue(PortalClaims.UserName),
            AuthMethod = user.Identity?.IsAuthenticated == true ? "local:" + (user.FindFirstValue(PortalClaims.AuthMethods) ?? "?") : "anonymous",
            SourceIp = http.Connection.RemoteIpAddress?.ToString(),
            ResourceType = resourceType,
            ResourceId = resourceId,
            CorrelationId = http.GetCorrelationId(),
            Details = details,
        };
    }

    public static Guid GetCorrelationId(this HttpContext http)
    {
        if (http.Items.TryGetValue("hvp.correlation", out var v) && v is Guid g)
        {
            return g;
        }
        var id = Guid.NewGuid();
        http.Items["hvp.correlation"] = id;
        return id;
    }
}

public static class PortalClaims
{
    public const string UserId = "hvp:uid";
    public const string SessionId = "hvp:sid";
    public const string UserName = "hvp:name";
    public const string AuthMethods = "hvp:amr";
    public const string Restriction = "hvp:restriction";
}
