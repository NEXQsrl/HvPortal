using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HvPortal.Shared;

namespace HvPortal.Gateway.Crypto;

/// <summary>
/// Application key ring used to encrypt secrets stored in the database (TOTP seeds, future provider credentials),
/// the internal CA private key, and to derive HMAC keys (audit chain, recovery codes).
///
/// Storage: keys/master.json, one entry per key, kept outside the database. Key material is wrapped with
///  - Windows: DPAPI (LocalMachine scope); the file ACL is restricted to SYSTEM, Administrators and the service SID;
///  - Linux (container): AES-256-GCM with a key-encryption key (KEK) from HVP_KEY_ENCRYPTION_KEY_FILE (Docker secret,
///    recommended) or HVP_KEY_ENCRYPTION_KEY; without either, a KEK is generated in keys/kek.bin (mode 0600, same
///    volume: protection is then only the file permissions) and a warning is logged. See <see cref="KeyWrap"/>.
/// Disaster recovery: DPAPI ties the file to this machine, therefore `HvPortal.Gateway.exe admin export-keys`
/// produces a passphrase-encrypted export (PBKDF2-SHA256 + AES-256-GCM) that can be imported on a new server.
/// Rotation: `admin rotate-keys` adds a new active key; old keys stay available for decryption.
/// </summary>
public sealed class KeyRing
{
    private const byte FormatVersion = 1;

    private readonly Dictionary<string, byte[]> _keys;
    private readonly string _path;

    public string ActiveKeyId { get; private set; }

    private KeyRing(string path, Dictionary<string, byte[]> keys, string active)
    {
        _path = path;
        _keys = keys;
        ActiveKeyId = active;
    }

    public static KeyRing LoadOrCreate(string keysDirectory)
    {
        SecureFileSystem.EnsurePrivateDirectory(keysDirectory);
        var path = Path.Combine(keysDirectory, "master.json");
        if (!File.Exists(path))
        {
            var id = NewKeyId();
            var ring = new KeyRing(path, new Dictionary<string, byte[]> { [id] = RandomNumberGenerator.GetBytes(32) }, id);
            ring.Save();
            return ring;
        }
        var doc = JsonSerializer.Deserialize<KeyFile>(File.ReadAllText(path)) ?? throw new InvalidDataException("Invalid key file.");
        var keys = doc.Keys.ToDictionary(k => k.Id, k => KeyWrap.Unwrap(doc.Protection, k.Id, Convert.FromBase64String(k.Wrapped), keysDirectory));
        return new KeyRing(path, keys, doc.Active);
    }

    /// <summary>For tests only: an in-memory key ring.</summary>
    internal static KeyRing CreateEphemeral() =>
        new(string.Empty, new Dictionary<string, byte[]> { ["test"] = RandomNumberGenerator.GetBytes(32) }, "test");

    public string Rotate()
    {
        var id = NewKeyId();
        _keys[id] = RandomNumberGenerator.GetBytes(32);
        ActiveKeyId = id;
        Save();
        return id;
    }

    /// <summary>AES-256-GCM. Output: [ver][idLen][id][nonce12][tag16][ciphertext]. <paramref name="purpose"/> is bound as AAD.</summary>
    public byte[] Encrypt(ReadOnlySpan<byte> plaintext, string purpose)
    {
        var id = Encoding.ASCII.GetBytes(ActiveKeyId);
        var output = new byte[2 + id.Length + 12 + 16 + plaintext.Length];
        output[0] = FormatVersion;
        output[1] = (byte)id.Length;
        id.CopyTo(output, 2);
        var nonce = output.AsSpan(2 + id.Length, 12);
        var tag = output.AsSpan(2 + id.Length + 12, 16);
        var cipher = output.AsSpan(2 + id.Length + 28);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(SubKey(_keys[ActiveKeyId], "enc:" + purpose), 16);
        aes.Encrypt(nonce, plaintext, cipher, tag, Encoding.UTF8.GetBytes(purpose));
        return output;
    }

    public byte[] Decrypt(ReadOnlySpan<byte> data, string purpose)
    {
        if (data.Length < 2 + 12 + 16 || data[0] != FormatVersion)
        {
            throw new CryptographicException("Unsupported ciphertext format.");
        }
        var idLen = data[1];
        var id = Encoding.ASCII.GetString(data.Slice(2, idLen));
        if (!_keys.TryGetValue(id, out var key))
        {
            throw new CryptographicException($"Key '{id}' is not in the key ring (was the key export imported?).");
        }
        var nonce = data.Slice(2 + idLen, 12);
        var tag = data.Slice(2 + idLen + 12, 16);
        var cipher = data[(2 + idLen + 28)..];
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(SubKey(key, "enc:" + purpose), 16);
        aes.Decrypt(nonce, cipher, tag, plain, Encoding.UTF8.GetBytes(purpose));
        return plain;
    }

    /// <summary>Derives a stable purpose-specific key from a given key id (HKDF-SHA256).</summary>
    public byte[] DeriveKey(string purpose, string? keyId = null) => SubKey(_keys[keyId ?? ActiveKeyId], "mac:" + purpose);

    public bool HasKey(string keyId) => _keys.ContainsKey(keyId);

    private static byte[] SubKey(byte[] master, string info) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, salt: null, info: Encoding.UTF8.GetBytes(info));

    private void Save()
    {
        if (string.IsNullOrEmpty(_path))
        {
            return;
        }
        var keysDirectory = Path.GetDirectoryName(_path)!;
        var protection = KeyWrap.CurrentProtection;
        var doc = new KeyFile
        {
            Active = ActiveKeyId,
            Protection = protection,
            Keys = _keys.Select(k => new KeyEntry
            {
                Id = k.Key,
                Wrapped = Convert.ToBase64String(KeyWrap.Wrap(protection, k.Key, k.Value, keysDirectory)),
            }).ToList(),
        };
        SecureFileSystem.WriteAllTextAtomic(_path, JsonSerializer.Serialize(doc, KeyFileJson));
    }

    // ------------------------------------------------------------- export / import (disaster recovery)

    public byte[] Export(string passphrase, IReadOnlyDictionary<string, byte[]> extraFiles)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new ExportPayload
        {
            Active = ActiveKeyId,
            Keys = _keys.ToDictionary(k => k.Key, k => Convert.ToBase64String(k.Value)),
            Files = extraFiles.ToDictionary(f => f.Key, f => Convert.ToBase64String(f.Value)),
        });
        return PassphraseBox.Seal(payload, passphrase);
    }

    public static (Dictionary<string, byte[]> Keys, string Active, Dictionary<string, byte[]> Files) ReadExport(byte[] blob, string passphrase)
    {
        var payload = JsonSerializer.Deserialize<ExportPayload>(PassphraseBox.Open(blob, passphrase))
                      ?? throw new InvalidDataException("Invalid export.");
        return (payload.Keys.ToDictionary(k => k.Key, k => Convert.FromBase64String(k.Value)), payload.Active,
            payload.Files.ToDictionary(f => f.Key, f => Convert.FromBase64String(f.Value)));
    }

    public static void WriteImported(string keysDirectory, Dictionary<string, byte[]> keys, string active)
    {
        SecureFileSystem.EnsurePrivateDirectory(keysDirectory);
        var ring = new KeyRing(Path.Combine(keysDirectory, "master.json"), keys, active);
        ring.Save();
    }

    private static string NewKeyId() => "k" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + Convert.ToHexString(RandomNumberGenerator.GetBytes(2)).ToLowerInvariant();

    private static readonly JsonSerializerOptions KeyFileJson = new() { WriteIndented = true };

    private sealed class KeyFile
    {
        public string Active { get; set; } = "";
        /// <summary>"dpapi" (Windows; also files written before this field existed) or "kek-aesgcm".</summary>
        public string Protection { get; set; } = KeyWrap.Dpapi;
        public List<KeyEntry> Keys { get; set; } = [];
    }

    private sealed class KeyEntry
    {
        public string Id { get; set; } = "";
        public string Wrapped { get; set; } = "";
    }

    private sealed class ExportPayload
    {
        public string Active { get; set; } = "";
        public Dictionary<string, string> Keys { get; set; } = [];
        public Dictionary<string, string> Files { get; set; } = [];
    }
}

/// <summary>How key material is protected at rest (per platform).</summary>
public static class KeyWrap
{
    public const string Dpapi = "dpapi";
    public const string Kek = "kek-aesgcm";
    private static readonly byte[] DpapiEntropy = "HvPortal.Gateway.KeyRing.v1"u8.ToArray();
    private static readonly object Gate = new();
    private static byte[]? _kek;

    public static string CurrentProtection => OperatingSystem.IsWindows() ? Dpapi : Kek;

    /// <summary>True when the KEK had to be generated next to the keys (no secret provided): logged as a warning at startup.</summary>
    public static bool KekGeneratedLocally { get; private set; }

    public static byte[] Wrap(string protection, string keyId, byte[] key, string keysDirectory)
    {
        if (protection == Dpapi)
        {
            return OperatingSystem.IsWindows()
                ? ProtectedData.Protect(key, DpapiEntropy, DataProtectionScope.LocalMachine)
                : throw new PlatformNotSupportedException("DPAPI is available only on Windows.");
        }
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[key.Length];
        using (var aes = new AesGcm(GetKek(keysDirectory), 16))
        {
            aes.Encrypt(nonce, key, cipher, tag, Aad(keyId));
        }
        return [.. nonce, .. tag, .. cipher];
    }

    public static byte[] Unwrap(string protection, string keyId, byte[] wrapped, string keysDirectory)
    {
        if (protection == Dpapi)
        {
            return OperatingSystem.IsWindows()
                ? ProtectedData.Unprotect(wrapped, DpapiEntropy, DataProtectionScope.LocalMachine)
                : throw new InvalidOperationException(
                    "This key ring was created on Windows (DPAPI) and cannot be opened here. Restore a portal backup (Settings > Backup) or a key export instead.");
        }
        if (wrapped.Length < 28)
        {
            throw new CryptographicException("Invalid wrapped key.");
        }
        var plain = new byte[wrapped.Length - 28];
        using var aes = new AesGcm(GetKek(keysDirectory), 16);
        aes.Decrypt(wrapped.AsSpan(0, 12), wrapped.AsSpan(28), wrapped.AsSpan(12, 16), plain, Aad(keyId));
        return plain;
    }

    private static byte[] Aad(string keyId) => Encoding.UTF8.GetBytes("HvPortal.KeyRing.KEK.v1:" + keyId);

    /// <summary>KEK source: HVP_KEY_ENCRYPTION_KEY_FILE (Docker secret), then HVP_KEY_ENCRYPTION_KEY, then generated keys/kek.bin.</summary>
    private static byte[] GetKek(string keysDirectory)
    {
        lock (Gate)
        {
            if (_kek is not null)
            {
                return _kek;
            }
            var file = Environment.GetEnvironmentVariable("HVP_KEY_ENCRYPTION_KEY_FILE");
            var inline = Environment.GetEnvironmentVariable("HVP_KEY_ENCRYPTION_KEY");
            if (!string.IsNullOrWhiteSpace(file))
            {
                _kek = ParseKek(File.ReadAllText(file), "HVP_KEY_ENCRYPTION_KEY_FILE");
            }
            else if (!string.IsNullOrWhiteSpace(inline))
            {
                _kek = ParseKek(inline, "HVP_KEY_ENCRYPTION_KEY");
            }
            else
            {
                var local = Path.Combine(keysDirectory, "kek.bin");
                if (!File.Exists(local))
                {
                    SecureFileSystem.WriteAllBytesAtomic(local, RandomNumberGenerator.GetBytes(32));
                }
                _kek = File.ReadAllBytes(local);
                KekGeneratedLocally = true;
            }
            return _kek;
        }
    }

    /// <summary>32 random bytes in base64, e.g. the output of: openssl rand -base64 32.</summary>
    private static byte[] ParseKek(string text, string source)
    {
        byte[] k;
        try
        {
            k = Convert.FromBase64String(text.Trim());
        }
        catch (FormatException)
        {
            throw new InvalidOperationException(source + ": expected 32 random bytes in base64 (openssl rand -base64 32).");
        }
        return k.Length == 32 ? k : throw new InvalidOperationException(source + ": expected 32 bytes, got " + k.Length + ".");
    }

    /// <summary>For tests: forget the cached KEK.</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _kek = null;
            KekGeneratedLocally = false;
        }
    }

    /// <summary>For tests: wrap/unwrap with the KEK path on any OS.</summary>
    internal static byte[] RoundTripKekForTests(byte[] key, string keysDirectory) =>
        Unwrap(Kek, "t", Wrap(Kek, "t", key, keysDirectory), keysDirectory);
}

/// <summary>Passphrase-based encryption for exports: PBKDF2-HMAC-SHA256 (600k) + AES-256-GCM.</summary>
public static class PassphraseBox
{
    private static readonly byte[] Magic = "HVPX1"u8.ToArray();
    private const int Iterations = 600_000;

    public static byte[] Seal(ReadOnlySpan<byte> plaintext, string passphrase)
    {
        if (passphrase.Length < 20)
        {
            throw new ArgumentException("The export passphrase must be at least 20 characters.");
        }
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, Iterations, HashAlgorithmName.SHA256, 32);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16))
        {
            aes.Encrypt(nonce, plaintext, cipher, tag, Magic);
        }
        return [.. Magic, .. salt, .. nonce, .. tag, .. cipher];
    }

    public static byte[] Open(ReadOnlySpan<byte> blob, string passphrase)
    {
        if (blob.Length < Magic.Length + 44 || !blob[..Magic.Length].SequenceEqual(Magic))
        {
            throw new CryptographicException("Not an HvPortal key export.");
        }
        var o = Magic.Length;
        var salt = blob.Slice(o, 16);
        var nonce = blob.Slice(o + 16, 12);
        var tag = blob.Slice(o + 28, 16);
        var cipher = blob[(o + 44)..];
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, Iterations, HashAlgorithmName.SHA256, 32);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, cipher, tag, plain, Magic);
        return plain;
    }
}
