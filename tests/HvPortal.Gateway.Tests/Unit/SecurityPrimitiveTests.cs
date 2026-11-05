using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using HvPortal.Gateway.Crypto;
using HvPortal.Gateway.Pki;
using HvPortal.Gateway.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace HvPortal.Gateway.Tests.Unit;

public class PasswordHasherTests
{
    private readonly PasswordHasher _hasher = new(10_000);

    [Fact]
    public void Verifies_correct_password_and_rejects_wrong_one()
    {
        var hash = _hasher.Hash("orbit lantern violet harbor");
        Assert.StartsWith("pbkdf2-sha256$10000$", hash);
        Assert.True(_hasher.Verify("orbit lantern violet harbor", hash));
        Assert.False(_hasher.Verify("orbit lantern violet harbour", hash));
    }

    [Fact]
    public void Long_passwords_are_not_truncated()
    {
        var a = new string('x', 200) + "A";
        var b = new string('x', 200) + "B";
        Assert.False(_hasher.Verify(b, _hasher.Hash(a)));
    }

    [Fact]
    public void Unicode_is_normalized_NFKC()
    {
        // "e" + combining acute vs precomposed "é"
        var hash = _hasher.Hash("café lantern violet harbor");
        Assert.True(_hasher.Verify("café lantern violet harbor", hash));
    }

    [Fact]
    public void Weaker_parameters_need_rehash()
    {
        var old = new PasswordHasher(10_000).Hash("orbit lantern violet harbor");
        Assert.True(new PasswordHasher(600_000).NeedsRehash(old));
        Assert.False(new PasswordHasher(10_000).NeedsRehash(old));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("pbkdf2-sha256$x$y$z")]
    public void Malformed_hashes_never_verify(string? encoded) => Assert.False(_hasher.Verify("anything at all here", encoded));
}

public class PasswordPolicyTests
{
    private readonly PasswordPolicy _policy = new();

    [Theory]
    [InlineData("short pass", "password_too_short")]
    [InlineData("passwordpassword", "password_common")]
    [InlineData("password12345678", "password_common")]
    [InlineData("123456789012345", "password_common")]
    [InlineData("aaaaaaaaaaaaaaaaaaaa", "password_repetitive")]
    [InlineData("abcabcabcabcabcabc", "password_repetitive")]
    [InlineData("abcdefghijklmnopq", "password_sequence")]
    [InlineData("abcdefghijklmnop", "password_common")]
    [InlineData("mnopqrstuvwxyz{|", "password_sequence")]
    public void Rejects_weak_passwords(string password, string code)
    {
        var r = _policy.Check(password, "someone");
        Assert.False(r.IsValid);
        Assert.Equal(code, r.Code);
    }

    [Fact]
    public void Rejects_password_containing_user_name() =>
        Assert.Equal("password_contains_username", _policy.Check("my name is mario.rossi ok", "mario.rossi").Code);

    [Fact]
    public void Accepts_long_passphrases_up_to_256_and_rejects_longer()
    {
        var p64 = string.Join(' ', Enumerable.Repeat("harbor-lantern", 5)) + " violet orbit q";
        Assert.True(p64.Length >= 64);
        Assert.True(_policy.Check(p64 + " unique tail 7", "u").IsValid);
        var tooLong = new string('a', 100) + new string('b', 100) + new string('c', 100);
        Assert.Equal("password_too_long", _policy.Check(tooLong, "u").Code);
    }

    [Fact]
    public void Minimum_cannot_be_configured_below_15() => Assert.Equal(15, new PasswordPolicy(8).MinimumLength);

    [Fact]
    public void Local_blocklist_with_sha1_entries_is_honored()
    {
        var file = Path.GetTempFileName();
        try
        {
            var banned = "galaxy harbor lantern 2024";
            File.WriteAllText(file, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(banned))) + Environment.NewLine);
            var policy = new PasswordPolicy(15, file);
            Assert.Equal("password_common", policy.Check(banned, "u").Code);
        }
        finally
        {
            File.Delete(file);
        }
    }
}

public class TotpTests
{
    // RFC 6238 Appendix B (SHA-1 seed), last 6 digits of the 8-digit reference values.
    private static readonly byte[] Seed = "12345678901234567890"u8.ToArray();

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    [InlineData(20000000000L, "353130")]
    public void Matches_rfc6238_vectors(long unixTime, string expected) =>
        Assert.Equal(expected, Totp.Compute(Seed, unixTime / 30));

    [Fact]
    public void Accepts_one_step_of_drift_but_not_two()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var step = Totp.TimeStep(now);
        Assert.NotNull(Totp.Verify(Seed, Totp.Compute(Seed, step - 1), now, 0));
        Assert.NotNull(Totp.Verify(Seed, Totp.Compute(Seed, step + 1), now, 0));
        Assert.Null(Totp.Verify(Seed, Totp.Compute(Seed, step - 2), now, 0));
        Assert.Null(Totp.Verify(Seed, Totp.Compute(Seed, step + 2), now, 0));
    }

    [Fact]
    public void Replay_of_used_or_older_step_is_rejected()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var step = Totp.TimeStep(now);
        var code = Totp.Compute(Seed, step);
        var used = Totp.Verify(Seed, code, now, 0);
        Assert.Equal(step, used);
        Assert.Null(Totp.Verify(Seed, code, now, used!.Value));
        Assert.Null(Totp.Verify(Seed, Totp.Compute(Seed, step - 1), now, used.Value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12a456")]
    [InlineData("' OR 1=1")]
    public void Malformed_codes_are_rejected(string? code) =>
        Assert.Null(Totp.Verify(Seed, code, DateTimeOffset.UtcNow, 0));

    [Fact]
    public void Base32_encoding_matches_rfc4648() => Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", Totp.Base32Encode(Seed));
}

public class KeyRingTests
{
    [Fact]
    public void Encrypts_and_binds_purpose()
    {
        var ring = KeyRing.CreateEphemeral();
        var c = ring.Encrypt("secret"u8, "totp");
        Assert.Equal("secret", Encoding.UTF8.GetString(ring.Decrypt(c, "totp")));
        Assert.ThrowsAny<CryptographicException>(() => ring.Decrypt(c, "other"));
        c[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => ring.Decrypt(c, "totp"));
    }

    [Fact]
    public void Export_requires_the_passphrase()
    {
        var ring = KeyRing.CreateEphemeral();
        var blob = ring.Export("a sufficiently long passphrase", new Dictionary<string, byte[]> { ["pki/ca.bin"] = [1, 2, 3] });
        var (keys, active, files) = KeyRing.ReadExport(blob, "a sufficiently long passphrase");
        Assert.Equal(ring.ActiveKeyId, active);
        Assert.Single(keys);
        Assert.Equal([1, 2, 3], files["pki/ca.bin"]);
        Assert.ThrowsAny<CryptographicException>(() => KeyRing.ReadExport(blob, "a wrong but long passphrase!!"));
        Assert.Throws<ArgumentException>(() => ring.Export("short", new Dictionary<string, byte[]>()));
    }

    [Fact]
    public void Recovery_codes_are_hashed_with_a_keyed_hmac()
    {
        var a = new RecoveryCodeService(KeyRing.CreateEphemeral());
        var b = new RecoveryCodeService(KeyRing.CreateEphemeral());
        var codes = a.Generate();
        Assert.Equal(10, codes.Count);
        Assert.Equal(codes.Count, codes.Distinct().Count());
        Assert.Equal(a.Hash(codes[0]), a.Hash(codes[0].ToUpperInvariant().Replace("-", " ")));
        Assert.NotEqual(a.Hash(codes[0]), b.Hash(codes[0]));
    }
}

public sealed class InternalCaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hvp-ca-" + Guid.NewGuid().ToString("N"));
    private readonly InternalCertificateAuthority _ca;

    public InternalCaTests()
    {
        _ca = InternalCertificateAuthority.LoadOrCreate(_dir, KeyRing.CreateEphemeral(), ["gw.test.local"], NullLogger.Instance);
    }

    [Fact]
    public void Issues_client_certificate_that_validates_only_against_this_ca()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var csr = new CertificateRequest("CN=attacker-chosen", key, HashAlgorithmName.SHA256).CreateSigningRequestPem();
        var hostId = Guid.NewGuid();
        var issued = _ca.IssueAgentCertificate(csr, hostId, TimeSpan.FromDays(90));
        using var cert = X509Certificate2.CreateFromPem(issued.CertificatePem);
        Assert.Contains(hostId.ToString("D"), cert.Subject);          // subject set by the gateway, not the CSR
        Assert.DoesNotContain("attacker-chosen", cert.Subject);
        Assert.True(_ca.ValidateClientCertificate(cert));

        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var selfSigned = new CertificateRequest("CN=rogue", otherKey, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        Assert.False(_ca.ValidateClientCertificate(selfSigned));
    }

    [Fact]
    public void Server_certificate_is_not_accepted_as_client_certificate() =>
        Assert.False(_ca.ValidateClientCertificate(_ca.ServerCertificate));

    [Fact]
    public void Rejects_weak_or_invalid_csr()
    {
        using var rsa = RSA.Create(2048);
        var weak = new CertificateRequest("CN=x", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSigningRequestPem();
        Assert.Throws<CertificateRequestRejectedException>(() => _ca.IssueAgentCertificate(weak, Guid.NewGuid(), TimeSpan.FromDays(1)));
        Assert.Throws<CertificateRequestRejectedException>(() => _ca.IssueAgentCertificate("-----BEGIN CERTIFICATE REQUEST-----\nAAAA\n-----END CERTIFICATE REQUEST-----", Guid.NewGuid(), TimeSpan.FromDays(1)));
    }

    [Fact]
    public void Reload_keeps_the_same_ca()
    {
        var fp = _ca.CaFingerprintSha256;
        // A second load from the same directory with a different key ring must fail rather than silently create a new CA.
        Assert.ThrowsAny<CryptographicException>(() =>
            InternalCertificateAuthority.LoadOrCreate(_dir, KeyRing.CreateEphemeral(), ["gw.test.local"], NullLogger.Instance));
        Assert.Equal(64, fp.Length);
    }

    public void Dispose()
    {
        _ca.Dispose();
        Directory.Delete(_dir, true);
    }
}
