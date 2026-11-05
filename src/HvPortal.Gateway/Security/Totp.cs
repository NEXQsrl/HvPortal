using System.Security.Cryptography;
using System.Text;

namespace HvPortal.Gateway.Security;

/// <summary>RFC 6238 TOTP (HMAC-SHA1, 6 digits, 30 s) — the parameters supported by all common authenticator apps.</summary>
public static class Totp
{
    public const int Digits = 6;
    public const int PeriodSeconds = 30;
    /// <summary>Accepted clock drift in time steps on each side (±30 s).</summary>
    public const int AllowedDriftSteps = 1;

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(20);

    public static long TimeStep(DateTimeOffset at) => at.ToUnixTimeSeconds() / PeriodSeconds;

    public static string Compute(byte[] secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(secret, counter, hash);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Verifies a code. Returns the matched time step, or null. Steps &lt;= <paramref name="lastUsedStep"/> are rejected
    /// so that a code (or an older one in the window) cannot be replayed.
    /// </summary>
    public static long? Verify(byte[] secret, string? code, DateTimeOffset now, long lastUsedStep)
    {
        if (code is null)
        {
            return null;
        }
        var digits = new string(code.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length != Digits || code.Count(c => !char.IsWhiteSpace(c) && c != '-') != Digits)
        {
            return null;
        }
        var current = TimeStep(now);
        long? matched = null;
        var expected = Encoding.ASCII.GetBytes(digits);
        for (var d = -AllowedDriftSteps; d <= AllowedDriftSteps; d++)
        {
            var step = current + d;
            var candidate = Encoding.ASCII.GetBytes(Compute(secret, step));
            // Constant-time compare; evaluate all steps to avoid timing differences.
            if (CryptographicOperations.FixedTimeEquals(candidate, expected) && step > lastUsedStep)
            {
                matched = step;
            }
        }
        return matched;
    }

    public static string Base32Encode(byte[] data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var sb = new StringBuilder((data.Length + 4) / 5 * 8);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0)
        {
            sb.Append(alphabet[(buffer << (5 - bits)) & 31]);
        }
        return sb.ToString();
    }

    public static string OtpAuthUri(string issuer, string account, byte[] secret) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}" +
        $"?secret={Base32Encode(secret)}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={PeriodSeconds}";
}
