using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace HvPortal.Gateway.Security;

/// <summary>
/// Password rules aligned with NIST SP 800-63B: length over composition, no periodic expiry, blocklist check.
///  - minimum 15 characters (configurable upwards), maximum 256 (long passphrases supported, never truncated);
///  - length is measured in Unicode text elements, after NFKC normalization;
///  - rejected: entries of the embedded common-password list (also after stripping trailing digits/symbols),
///    an optional local blocklist (plain text or SHA-1 hex per line, e.g. an offline breach corpus),
///    the user name, a single repeated character / short repeated pattern, simple sequences.
/// </summary>
public sealed class PasswordPolicy
{
    public const int AbsoluteMinimum = 15;
    public const int Maximum = 256;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (HashSet<string> Plain, HashSet<string> Sha1)> LocalLists = new();
    private static readonly Lazy<HashSet<string>> Embedded = new(LoadEmbedded);
    private readonly HashSet<string> _common;
    private readonly HashSet<string> _localPlain;
    private readonly HashSet<string> _localSha1;

    public int MinimumLength { get; }

    public PasswordPolicy(int minimumLength = AbsoluteMinimum, string? localBlocklistPath = null)
    {
        MinimumLength = Math.Clamp(minimumLength, AbsoluteMinimum, 128);
        _common = Embedded.Value;
        (_localPlain, _localSha1) = localBlocklistPath is not null && File.Exists(localBlocklistPath)
            ? LocalLists.GetOrAdd(Path.GetFullPath(localBlocklistPath), LoadLocal)
            : (new HashSet<string>(), new HashSet<string>());
    }

    private static (HashSet<string>, HashSet<string>) LoadLocal(string localBlocklistPath)
    {
        var plain = new HashSet<string>(StringComparer.Ordinal);
        var sha1 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        {
            foreach (var line in File.ReadLines(localBlocklistPath).Take(5_000_000))
            {
                var t = line.Trim();
                if (t.Length == 40 && t.All(Uri.IsHexDigit))
                {
                    sha1.Add(t);
                }
                else if (t.Length > 0)
                {
                    plain.Add(t.ToLowerInvariant());
                }
            }
        }
        return (plain, sha1);
    }

    public PasswordCheck Check(string? password, string? userName)
    {
        if (string.IsNullOrEmpty(password))
        {
            return PasswordCheck.Fail("password_required", "A password is required.");
        }
        var normalized = password.Normalize(NormalizationForm.FormKC);
        var length = new StringInfo(normalized).LengthInTextElements;
        if (length < MinimumLength)
        {
            return PasswordCheck.Fail("password_too_short", $"Use at least {MinimumLength} characters. Long passphrases are encouraged.");
        }
        if (length > Maximum)
        {
            return PasswordCheck.Fail("password_too_long", $"Use at most {Maximum} characters.");
        }
        var lower = normalized.ToLowerInvariant();
        if (IsCommon(lower))
        {
            return PasswordCheck.Fail("password_common", "This password is too common or appears in a list of known passwords.");
        }
        if (!string.IsNullOrWhiteSpace(userName) && userName.Length >= 3 && lower.Contains(userName.ToLowerInvariant(), StringComparison.Ordinal))
        {
            return PasswordCheck.Fail("password_contains_username", "The password must not contain the user name.");
        }
        if (IsRepetitive(lower))
        {
            return PasswordCheck.Fail("password_repetitive", "The password is a repeated character or pattern.");
        }
        if (IsSequence(lower))
        {
            return PasswordCheck.Fail("password_sequence", "The password is a simple sequence.");
        }
        return PasswordCheck.Ok;
    }

    private bool IsCommon(string lower)
    {
        if (_common.Contains(lower) || _localPlain.Contains(lower))
        {
            return true;
        }
        var stripped = lower.TrimEnd("0123456789!@#$%^&*()-_=+.?".ToCharArray());
        if (stripped.Length >= 4 && (_common.Contains(stripped) || _localPlain.Contains(stripped)))
        {
            return true;
        }
        // Common word repeated ("passwordpassword") or combined with a common suffix.
        foreach (var w in _common)
        {
            if (w.Length >= 4 && lower.Length % w.Length == 0 && lower == string.Concat(Enumerable.Repeat(w, lower.Length / w.Length)))
            {
                return true;
            }
        }
        if (_localSha1.Count > 0)
        {
            var sha1 = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(lower)));
            if (_localSha1.Contains(sha1))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsRepetitive(string s)
    {
        for (var period = 1; period <= 4; period++)
        {
            if (s.Length % period != 0 && period > 1)
            {
                continue;
            }
            var unit = s[..period];
            var ok = true;
            for (var i = 0; i < s.Length; i++)
            {
                if (s[i] != unit[i % period])
                {
                    ok = false;
                    break;
                }
            }
            if (ok)
            {
                return true;
            }
        }
        return s.Distinct().Count() <= 2;
    }

    private static bool IsSequence(string s)
    {
        static bool Monotonic(string x, int step)
        {
            for (var i = 1; i < x.Length; i++)
            {
                if (x[i] - x[i - 1] != step)
                {
                    return false;
                }
            }
            return true;
        }
        if (Monotonic(s, 1) || Monotonic(s, -1))
        {
            return true;
        }
        const string rows = "qwertyuiopasdfghjklzxcvbnm1234567890";
        return rows.Contains(s, StringComparison.Ordinal) || "1234567890".Repeat(4).Contains(s, StringComparison.Ordinal);
    }

    private static HashSet<string> LoadEmbedded()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("common-passwords.txt", StringComparison.Ordinal));
        if (name is null)
        {
            return set;
        }
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            var t = line.Trim();
            if (t.Length > 0 && !t.StartsWith('#'))
            {
                set.Add(t.ToLowerInvariant());
            }
        }
        return set;
    }
}

internal static class StringRepeatExtensions
{
    public static string Repeat(this string s, int count) => string.Concat(Enumerable.Repeat(s, count));
}

public sealed record PasswordCheck(bool IsValid, string? Code, string? Message)
{
    public static readonly PasswordCheck Ok = new(true, null, null);
    public static PasswordCheck Fail(string code, string message) => new(false, code, message);
}
