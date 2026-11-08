using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace HvPortal.Gateway.Security;

/// <summary>
/// Confirmation codes for the step-up of Microsoft (Entra ID) accounts (ADR-038): the portal shows a random code and the
/// user types it again. It confirms that a person at this session wants the sensitive action; it does not prove the
/// identity again (the Microsoft sign-in of the session did). One code per session, single use, short-lived, a few
/// attempts. Kept in memory only: a gateway restart simply asks for a new code.
/// </summary>
public sealed class ReauthCodeService(TimeProvider time)
{
    /// <summary>No 0/O, 1/I/L: easy to read and to type again.</summary>
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    public const int Length = 6;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    public const int MaxAttempts = 5;

    private sealed record Entry(string Code, DateTimeOffset ExpiresAt, int Attempts);

    private readonly ConcurrentDictionary<Guid, Entry> _codes = new();

    /// <summary>A new code for the session (replaces the previous one).</summary>
    public (string Code, DateTimeOffset ExpiresAt) Issue(Guid sessionId)
    {
        Prune();
        var code = RandomNumberGenerator.GetString(Alphabet, Length);
        var expires = time.GetUtcNow() + Lifetime;
        _codes[sessionId] = new Entry(code, expires, 0);
        return (code, expires);
    }

    public enum Outcome { Ok, Wrong, Expired, Missing, TooManyAttempts }

    /// <summary>Checks a typed code (case and spaces ignored). A right code is consumed; a wrong one counts as an attempt.</summary>
    public Outcome Verify(Guid sessionId, string? typed)
    {
        if (!_codes.TryGetValue(sessionId, out var entry))
        {
            return Outcome.Missing;
        }
        if (time.GetUtcNow() > entry.ExpiresAt)
        {
            _codes.TryRemove(sessionId, out _);
            return Outcome.Expired;
        }
        var normalized = new string((typed ?? "").Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray()).ToUpperInvariant();
        var ok = normalized.Length == Length
                 && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(normalized), Encoding.ASCII.GetBytes(entry.Code));
        if (ok)
        {
            // Consumed only if still the same entry (two parallel requests with the same code: one wins).
            return _codes.TryRemove(new KeyValuePair<Guid, Entry>(sessionId, entry)) ? Outcome.Ok : Outcome.Missing;
        }
        var next = entry with { Attempts = entry.Attempts + 1 };
        if (next.Attempts >= MaxAttempts)
        {
            _codes.TryRemove(sessionId, out _);
            return Outcome.TooManyAttempts;
        }
        _codes.TryUpdate(sessionId, next, entry);
        return Outcome.Wrong;
    }

    private void Prune()
    {
        var now = time.GetUtcNow();
        foreach (var (key, value) in _codes)
        {
            if (now > value.ExpiresAt)
            {
                _codes.TryRemove(key, out _);
            }
        }
    }
}
