using System.Collections.Concurrent;
using HvPortal.Gateway.Security;

namespace HvPortal.Gateway.VmConsole;

/// <summary>
/// One-time console tickets: 256-bit random, valid 30 seconds, bound to user + portal session + VM, consumed on first use.
/// Kept in memory only (a gateway restart invalidates them, which is the desired behavior). Only the hash is stored.
/// The ticket travels in the first WebSocket message, never in the URL (URLs end up in logs).
/// </summary>
public sealed class ConsoleTicketStore(TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, Ticket> _tickets = new(StringComparer.Ordinal);

    public sealed record Ticket(Guid UserId, Guid SessionId, Guid VmId, DateTimeOffset ExpiresAt);

    public (string Token, DateTimeOffset ExpiresAt) Issue(Guid userId, Guid sessionId, Guid vmId)
    {
        var now = time.GetUtcNow();
        foreach (var (key, t) in _tickets)
        {
            if (t.ExpiresAt <= now)
            {
                _tickets.TryRemove(key, out _);
            }
        }
        var token = Tokens.New();
        var expires = now + Lifetime;
        _tickets[Tokens.Hash(token)] = new Ticket(userId, sessionId, vmId, expires);
        return (token, expires);
    }

    /// <summary>Consumes the ticket (whatever the outcome) and returns it only if valid for this user, session and VM.</summary>
    public Ticket? Redeem(string? token, Guid userId, Guid sessionId, Guid vmId)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 128 || !_tickets.TryRemove(Tokens.Hash(token), out var t))
        {
            return null;
        }
        return t.ExpiresAt > time.GetUtcNow() && t.UserId == userId && t.SessionId == sessionId && t.VmId == vmId ? t : null;
    }
}

/// <summary>Live console sessions: limits per user and per VM, and the list shown to administrators.</summary>
public sealed class ConsoleSessionRegistry
{
    public const int MaxPerUser = 4;
    public const int MaxPerVm = 4;
    private readonly ConcurrentDictionary<Guid, Entry> _sessions = new();

    public sealed record Entry(Guid Id, Guid UserId, string? UserName, Guid VmId, string VmName, DateTimeOffset StartedAt, CancellationTokenSource Cancel);

    public bool TryAdd(Entry e)
    {
        lock (_sessions)
        {
            if (_sessions.Values.Count(x => x.UserId == e.UserId) >= MaxPerUser || _sessions.Values.Count(x => x.VmId == e.VmId) >= MaxPerVm)
            {
                return false;
            }
            _sessions[e.Id] = e;
            return true;
        }
    }

    public void Remove(Guid id) => _sessions.TryRemove(id, out _);

    public int CountForUser(Guid userId) => _sessions.Values.Count(x => x.UserId == userId);

    public IReadOnlyList<Entry> ForVm(Guid vmId) => _sessions.Values.Where(x => x.VmId == vmId).ToList();

    public IReadOnlyList<Entry> All => _sessions.Values.ToList();

    /// <summary>Ends every console session of a user (e.g. after the account is disabled).</summary>
    public void CloseForUser(Guid userId)
    {
        foreach (var e in _sessions.Values.Where(x => x.UserId == userId))
        {
            e.Cancel.Cancel();
        }
    }
}
