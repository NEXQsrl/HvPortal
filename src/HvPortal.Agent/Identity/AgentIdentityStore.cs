using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using HvPortal.Shared;

namespace HvPortal.Agent.Identity;

public sealed record AgentState
{
    public required Uri GatewayUrl { get; init; }
    /// <summary>Host id, or the management agent id when <see cref="Role"/> is Management.</summary>
    public required Guid HostId { get; init; }
    public required string CaCertificatePem { get; init; }
    public DateTimeOffset EnrolledAtUtc { get; init; }
    /// <summary>From the gateway at enrollment (protocol 6+); missing in older agent.json files = Host.</summary>
    public HvPortal.Contracts.Agent.AgentRole Role { get; init; } = HvPortal.Contracts.Agent.AgentRole.Host;
}

/// <summary>
/// Stores the agent configuration (agent.json) and identity (identity.bin) in a private directory.
/// identity.bin is a PKCS#12 blob encrypted with DPAPI (LocalMachine scope + entropy); the file ACL
/// restricts it to SYSTEM, Administrators and the agent service SID. The private key is generated on
/// this machine and never leaves it. A TPM-backed non-exportable key is a planned hardening (see docs).
/// </summary>
public sealed class AgentIdentityStore
{
    private static readonly byte[] Entropy = "HvPortal.Agent.Identity.v1"u8.ToArray();
    private readonly string _dir;

    public AgentIdentityStore(string dataDirectory)
    {
        _dir = dataDirectory;
    }

    public string DataDirectory => _dir;
    private string StatePath => Path.Combine(_dir, "agent.json");
    private string IdentityPath => Path.Combine(_dir, "identity.bin");

    public static string DefaultDataDirectory() =>
        Environment.GetEnvironmentVariable("HVP_AGENT_DATA_DIR") is { Length: > 0 } d
            ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HvPortal", "Agent");

    public void EnsureDirectory()
    {
        if (SecureFileSystem.ContainsReparsePoint(_dir))
        {
            throw new InvalidOperationException($"Refusing to use '{_dir}': the path contains a reparse point.");
        }
        SecureFileSystem.EnsurePrivateDirectory(_dir);
    }

    public bool IsEnrolled => File.Exists(StatePath) && File.Exists(IdentityPath);

    public AgentState? LoadState() =>
        File.Exists(StatePath) ? JsonSerializer.Deserialize<AgentState>(File.ReadAllText(StatePath)) : null;

    public void SaveState(AgentState state)
    {
        EnsureDirectory();
        SecureFileSystem.WriteAllTextAtomic(StatePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
    }

    public X509Certificate2? LoadCertificate()
    {
        if (!File.Exists(IdentityPath))
        {
            return null;
        }
        var pfx = ProtectedData.Unprotect(File.ReadAllBytes(IdentityPath), Entropy, DataProtectionScope.LocalMachine);
        try
        {
            // Persisted (non-ephemeral) key: SChannel client authentication does not work with ephemeral keys.
            return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.DefaultKeySet);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }
    }

    /// <summary>Stores the PKCS#12 produced from the original in-memory key (the persisted re-import is not exportable).</summary>
    public void SaveIdentity(byte[] pfx)
    {
        EnsureDirectory();
        try
        {
            var protectedBytes = ProtectedData.Protect(pfx, Entropy, DataProtectionScope.LocalMachine);
            SecureFileSystem.WriteAllBytesAtomic(IdentityPath, protectedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }
    }

    public void Delete()
    {
        foreach (var p in new[] { StatePath, IdentityPath })
        {
            if (File.Exists(p))
            {
                File.Delete(p);
            }
        }
    }

    /// <summary>Creates a new P-256 key and a PKCS#10 request. The subject is informational; the gateway sets its own.</summary>
    public static (ECDsa Key, string CsrPem) CreateKeyAndCsr(string machineName)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var safeName = new string(machineName.Where(c => char.IsLetterOrDigit(c) || c is '-' or '.').Take(63).ToArray());
        var req = new CertificateRequest($"CN={safeName}", key, HashAlgorithmName.SHA256);
        return (key, req.CreateSigningRequestPem());
    }

    /// <summary>Combines the issued certificate with the local private key into a PKCS#12 blob.</summary>
    public static byte[] CombineToPfx(string certificatePem, ECDsa key)
    {
        using var pub = X509Certificate2.CreateFromPem(certificatePem);
        using var withKey = pub.CopyWithPrivateKey(key);
        return withKey.Export(X509ContentType.Pkcs12);
    }
}
