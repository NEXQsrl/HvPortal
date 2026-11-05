using System.Security.Cryptography;
using HvPortal.Gateway.Crypto;

namespace HvPortal.Gateway.Tests.Unit;

/// <summary>Key-ring wrapping used in Linux containers (KEK + AES-256-GCM); exercised on any OS.</summary>
public sealed class KeyWrapTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hvp-kek-" + Guid.NewGuid().ToString("N"));

    public KeyWrapTests()
    {
        Directory.CreateDirectory(_dir);
        KeyWrap.ResetForTests();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HVP_KEY_ENCRYPTION_KEY", null);
        KeyWrap.ResetForTests();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Kek_from_environment_wraps_and_unwraps_and_rejects_another_kek()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        Environment.SetEnvironmentVariable("HVP_KEY_ENCRYPTION_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        var wrapped = KeyWrap.Wrap(KeyWrap.Kek, "k1", key, _dir);
        Assert.Equal(key, KeyWrap.Unwrap(KeyWrap.Kek, "k1", wrapped, _dir));
        Assert.False(KeyWrap.KekGeneratedLocally);
        Assert.False(File.Exists(Path.Combine(_dir, "kek.bin")));
        // Key id is bound: a wrapped key cannot be swapped to another id.
        Assert.ThrowsAny<CryptographicException>(() => KeyWrap.Unwrap(KeyWrap.Kek, "k2", wrapped, _dir));

        KeyWrap.ResetForTests();
        Environment.SetEnvironmentVariable("HVP_KEY_ENCRYPTION_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        Assert.ThrowsAny<CryptographicException>(() => KeyWrap.Unwrap(KeyWrap.Kek, "k1", wrapped, _dir));
    }

    [Fact]
    public void Without_a_kek_one_is_generated_next_to_the_keys_and_flagged()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        Assert.Equal(key, KeyWrap.RoundTripKekForTests(key, _dir));
        Assert.True(KeyWrap.KekGeneratedLocally);
        Assert.Equal(32, File.ReadAllBytes(Path.Combine(_dir, "kek.bin")).Length);
    }

    [Fact]
    public void Invalid_kek_values_are_refused()
    {
        Environment.SetEnvironmentVariable("HVP_KEY_ENCRYPTION_KEY", "not-base64!");
        Assert.Throws<InvalidOperationException>(() => KeyWrap.Wrap(KeyWrap.Kek, "k", new byte[32], _dir));
        KeyWrap.ResetForTests();
        Environment.SetEnvironmentVariable("HVP_KEY_ENCRYPTION_KEY", Convert.ToBase64String(new byte[16]));
        Assert.Throws<InvalidOperationException>(() => KeyWrap.Wrap(KeyWrap.Kek, "k", new byte[32], _dir));
    }
}
