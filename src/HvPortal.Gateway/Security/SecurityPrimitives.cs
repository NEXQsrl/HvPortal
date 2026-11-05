using System.Security.Cryptography;
using System.Text;
using HvPortal.Gateway.Crypto;

namespace HvPortal.Gateway.Security;

public static class Tokens
{
    /// <summary>URL-safe random token (default 256 bits).</summary>
    public static string New(int bytes = 32) => Base64UrlEncode(RandomNumberGenerator.GetBytes(bytes));

    /// <summary>Tokens are high-entropy, so an unsalted SHA-256 is an adequate lookup hash.</summary>
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim())));

    public static bool FixedTimeEqualsHex(string a, string b) =>
        a.Length == b.Length && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    public static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// One-time recovery codes: 12 characters from a 32-symbol alphabet without ambiguous characters (60 bits),
/// displayed as xxxx-xxxx-xxxx. Stored as HMAC-SHA256 with a key derived from the key ring, so a stolen
/// database alone does not allow offline guessing.
/// </summary>
public sealed class RecoveryCodeService
{
    private const string Alphabet = "abcdefghjkmnpqrstuvwxyz23456789"; // 31 symbols: no i, l, o, 0, 1
    public const int CodeCount = 10;
    private readonly byte[] _key;

    public RecoveryCodeService(KeyRing keys)
    {
        _key = keys.DeriveKey("recovery-codes");
    }

    public IReadOnlyList<string> Generate()
    {
        var codes = new List<string>(CodeCount);
        for (var i = 0; i < CodeCount; i++)
        {
            var chars = new char[12];
            for (var j = 0; j < chars.Length; j++)
            {
                chars[j] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
            }
            var s = new string(chars);
            codes.Add($"{s[..4]}-{s[4..8]}-{s[8..]}");
        }
        return codes;
    }

    public string Hash(string code)
    {
        var normalized = new string(code.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(normalized)));
    }
}
