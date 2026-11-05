using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HvPortal.Gateway.Crypto;
using HvPortal.Shared;

namespace HvPortal.Gateway.Pki;

/// <summary>
/// Private CA used only for the agent channel (gateway server certificate + agent client certificates).
/// Built with the .NET CertificateRequest API (no external tooling). The CA key is stored encrypted with the
/// key ring (pki/ca.bin). Agents pin the CA by SHA-256 fingerprint at enrollment.
/// </summary>
public sealed class InternalCertificateAuthority : IDisposable
{
    private const string CaPurpose = "pki:ca";
    private const string ServerPurpose = "pki:server";
    private static readonly Oid ServerAuth = new("1.3.6.1.5.5.7.3.1");
    private static readonly Oid ClientAuth = new("1.3.6.1.5.5.7.3.2");

    private readonly string _dir;
    private readonly KeyRing _keys;
    private readonly ILogger _logger;

    public X509Certificate2 CaCertificate { get; }
    public X509Certificate2 ServerCertificate { get; private set; }
    public string CaFingerprintSha256 => Convert.ToHexString(SHA256.HashData(CaCertificate.RawData));

    private InternalCertificateAuthority(string dir, KeyRing keys, X509Certificate2 ca, X509Certificate2 server, ILogger logger)
    {
        _dir = dir;
        _keys = keys;
        CaCertificate = ca;
        ServerCertificate = server;
        _logger = logger;
    }

    public static InternalCertificateAuthority LoadOrCreate(string pkiDir, KeyRing keys, IEnumerable<string> publicNames, ILogger logger)
    {
        SecureFileSystem.EnsurePrivateDirectory(pkiDir);
        var caPath = Path.Combine(pkiDir, "ca.bin");
        X509Certificate2 ca;
        if (File.Exists(caPath))
        {
            ca = LoadPfx(keys.Decrypt(File.ReadAllBytes(caPath), CaPurpose));
        }
        else
        {
            var caPfx = CreateCaPfx();
            SecureFileSystem.WriteAllBytesAtomic(caPath, keys.Encrypt(caPfx, CaPurpose));
            ca = LoadPfx(caPfx);
            logger.LogInformation("Created internal CA {Subject}", ca.Subject);
        }

        var names = NormalizeNames(publicNames);
        var serverPath = Path.Combine(pkiDir, "server.bin");
        X509Certificate2? server = null;
        if (File.Exists(serverPath))
        {
            server = LoadPfx(keys.Decrypt(File.ReadAllBytes(serverPath), ServerPurpose));
            if (server.NotAfter.ToUniversalTime() < DateTime.UtcNow.AddDays(30) || !CoversNames(server, names))
            {
                server.Dispose();
                server = null;
            }
        }
        if (server is null)
        {
            var serverPfx = IssueServerCertificatePfx(ca, names);
            SecureFileSystem.WriteAllBytesAtomic(serverPath, keys.Encrypt(serverPfx, ServerPurpose));
            server = LoadPfx(serverPfx);
            logger.LogInformation("Issued gateway server certificate for {Names}", string.Join(", ", names));
        }
        return new InternalCertificateAuthority(pkiDir, keys, ca, server, logger);
    }

    /// <summary>Issues an agent client certificate from a CSR. The gateway decides subject, SAN, EKU and lifetime.</summary>
    public IssuedCertificate IssueAgentCertificate(string csrPem, Guid hostId, TimeSpan lifetime)
    {
        if (csrPem.Length > 16 * 1024)
        {
            throw new CertificateRequestRejectedException("CSR too large.");
        }
        CertificateRequest csr;
        try
        {
            csr = CertificateRequest.LoadSigningRequestPem(csrPem, HashAlgorithmName.SHA256,
                CertificateRequestLoadOptions.Default, RSASignaturePadding.Pkcs1);
        }
        catch (CryptographicException ex)
        {
            throw new CertificateRequestRejectedException("Invalid CSR: " + ex.Message);
        }
        EnsureAcceptableKey(csr.PublicKey);

        var req = new CertificateRequest(new X500DistinguishedName($"CN=hvportal-host-{hostId:D}, O=HvPortal Agent"),
            csr.PublicKey, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([ClientAuth], true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddUri(new Uri($"urn:hvportal:host:{hostId:D}"));
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(csr.PublicKey, X509SubjectKeyIdentifierHashAlgorithm.Sha1, false));
        req.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(CaCertificate, true, false));

        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        using var cert = req.Create(CaCertificate, notBefore, notBefore.Add(lifetime), serial);
        return new IssuedCertificate(cert.ExportCertificatePem(), Sha256Thumbprint(cert), Convert.ToHexString(serial),
            notBefore, notBefore.Add(lifetime));
    }

    /// <summary>Chain validation of an agent client certificate against this CA only (no Windows trust store).</summary>
    public bool ValidateClientCertificate(X509Certificate2 cert)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(CaCertificate);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck; // revocation is enforced against the database
        chain.ChainPolicy.ApplicationPolicy.Add(ClientAuth);
        return chain.Build(cert);
    }

    public static string Sha256Thumbprint(X509Certificate2 cert) => Convert.ToHexString(SHA256.HashData(cert.RawData));

    /// <summary>DNS names and IP addresses in the agent-facing server certificate (agents must use one of them).</summary>
    public IReadOnlyList<string> ServerCertificateNames()
    {
        var ext = ServerCertificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        return ext is null ? [] : [.. ext.EnumerateDnsNames(), .. ext.EnumerateIPAddresses().Select(i => i.ToString())];
    }

    public void Dispose()
    {
        CaCertificate.Dispose();
        ServerCertificate.Dispose();
    }

    private static void EnsureAcceptableKey(PublicKey key)
    {
        using var ec = key.GetECDsaPublicKey();
        if (ec is not null)
        {
            if (ec.KeySize < 256)
            {
                throw new CertificateRequestRejectedException("ECDSA keys must be at least P-256.");
            }
            return;
        }
        using var rsa = key.GetRSAPublicKey();
        if (rsa is not null && rsa.KeySize >= 3072)
        {
            return;
        }
        throw new CertificateRequestRejectedException("Only ECDSA (P-256+) or RSA (3072+) keys are accepted.");
    }

    private static byte[] CreateCaPfx()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(4));
        var req = new CertificateRequest($"CN=HvPortal Internal CA {id}, O=HvPortal", key, HashAlgorithmName.SHA384);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        var now = DateTimeOffset.UtcNow.AddMinutes(-5);
        using var cert = req.CreateSelfSigned(now, now.AddYears(10));
        return cert.Export(X509ContentType.Pkcs12);
    }

    private static byte[] IssueServerCertificatePfx(X509Certificate2 ca, IReadOnlyList<string> names)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest($"CN={names[0]}, O=HvPortal Gateway", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([ServerAuth], true));
        var san = new SubjectAlternativeNameBuilder();
        foreach (var n in names)
        {
            if (IPAddress.TryParse(n, out var ip))
            {
                san.AddIpAddress(ip);
            }
            else
            {
                san.AddDnsName(n);
            }
        }
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        req.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, true, false));
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        var now = DateTimeOffset.UtcNow.AddMinutes(-5);
        using var pub = req.Create(ca, now, now.AddDays(397), serial);
        using var withKey = pub.CopyWithPrivateKey(key);
        return withKey.Export(X509ContentType.Pkcs12);
    }

    private static IReadOnlyList<string> NormalizeNames(IEnumerable<string> configured)
    {
        var names = new List<string>();
        void Add(string? n)
        {
            if (!string.IsNullOrWhiteSpace(n) && !names.Contains(n.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                names.Add(n.Trim());
            }
        }
        foreach (var n in configured)
        {
            Add(n);
        }
        Add(Environment.MachineName);
        try
        {
            Add(Dns.GetHostEntry(Environment.MachineName).HostName);
        }
        catch (System.Net.Sockets.SocketException)
        {
        }
        Add("localhost");
        Add("127.0.0.1");
        Add("::1");
        return names;
    }

    private static bool CoversNames(X509Certificate2 cert, IReadOnlyList<string> names)
    {
        var ext = cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        if (ext is null)
        {
            return false;
        }
        var dns = ext.EnumerateDnsNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ips = ext.EnumerateIPAddresses().Select(i => i.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return names.All(n => IPAddress.TryParse(n, out var ip) ? ips.Contains(ip.ToString()) : dns.Contains(n));
    }

    /// <summary>Re-import through PKCS#12 so the private key is persisted: SChannel cannot use ephemeral keys.</summary>
    private static X509Certificate2 LoadPfx(byte[] pfx)
    {
        try
        {
            return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.DefaultKeySet);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }
    }
}

public sealed record IssuedCertificate(string CertificatePem, string ThumbprintSha256, string SerialNumber, DateTimeOffset NotBefore, DateTimeOffset NotAfter);

public sealed class CertificateRequestRejectedException(string message) : Exception(message);
