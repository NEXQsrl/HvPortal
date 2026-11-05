using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HvPortal.Agent.Identity;

/// <summary>
/// Validates the gateway's TLS certificate against the pinned internal CA only (custom root trust).
/// The Windows trust store is deliberately not used for the agent channel.
/// </summary>
public static class GatewayTrust
{
    public static RemoteCertificateValidationCallback CreateValidator(X509Certificate2 caCertificate) =>
        (_, certificate, _, sslPolicyErrors) => Validate(caCertificate, certificate, sslPolicyErrors);

    internal static bool Validate(X509Certificate2 ca, X509Certificate? presented, SslPolicyErrors errors)
    {
        if (presented is null)
        {
            return false;
        }
        // Name mismatch and "no certificate" are never acceptable; chain errors are re-evaluated below.
        if ((errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
        {
            return false;
        }
        using var leaf = X509CertificateLoader.LoadCertificate(presented.GetRawCertData());
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1")); // serverAuth
        return chain.Build(leaf);
    }

    public static string Sha256Fingerprint(X509Certificate2 cert) =>
        Convert.ToHexString(SHA256.HashData(cert.RawData));

    public static bool FingerprintMatches(X509Certificate2 cert, string expectedHex)
    {
        var normalized = new string(expectedHex.Where(Uri.IsHexDigit).ToArray());
        if (normalized.Length != 64)
        {
            return false;
        }
        var expected = Convert.FromHexString(normalized);
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(cert.RawData), expected);
    }
}
