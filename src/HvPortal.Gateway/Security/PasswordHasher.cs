using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace HvPortal.Gateway.Security;

/// <summary>
/// PBKDF2-HMAC-SHA256 (System.Security.Cryptography, FIPS-approved) with a per-hash 16-byte salt.
/// Format: "pbkdf2-sha256$&lt;iterations&gt;$&lt;salt b64&gt;$&lt;hash b64&gt;" so parameters can be raised later:
/// <see cref="NeedsRehash"/> triggers a transparent upgrade at the next successful login.
/// The password is normalized to Unicode NFKC (NIST SP 800-63B §5.1.1.2) and never truncated.
/// </summary>
public sealed class PasswordHasher
{
    private const string Prefix = "pbkdf2-sha256";
    private readonly int _iterations;

    public PasswordHasher(int iterations)
    {
        _iterations = Math.Max(iterations, 10_000);
    }

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Derive(password, salt, _iterations);
        return string.Join('$', Prefix, _iterations.ToString(CultureInfo.InvariantCulture), Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    public bool Verify(string password, string? encoded)
    {
        if (encoded is null || !TryParse(encoded, out var iterations, out var salt, out var expected))
        {
            // Burn comparable time so that unknown users and users without passwords are not distinguishable.
            Derive(password, new byte[16], _iterations);
            return false;
        }
        var actual = Derive(password, salt, iterations);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public bool NeedsRehash(string encoded) =>
        !TryParse(encoded, out var iterations, out _, out _) || iterations < _iterations;

    /// <summary>Equalizes timing for login attempts on unknown accounts.</summary>
    public void SimulateVerify(string password) => Derive(password, new byte[16], _iterations);

    private static byte[] Derive(string password, byte[] salt, int iterations)
    {
        var normalized = password.Normalize(NormalizationForm.FormKC);
        var bytes = Encoding.UTF8.GetBytes(normalized);
        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(bytes, salt, iterations, HashAlgorithmName.SHA256, 32);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool TryParse(string encoded, out int iterations, out byte[] salt, out byte[] hash)
    {
        iterations = 0;
        salt = hash = [];
        var parts = encoded.Split('$');
        if (parts.Length != 4 || parts[0] != Prefix || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out iterations))
        {
            return false;
        }
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            hash = Convert.FromBase64String(parts[3]);
            return salt.Length >= 16 && hash.Length == 32 && iterations > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
