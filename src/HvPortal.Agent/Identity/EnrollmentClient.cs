using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using HvPortal.Contracts.Agent;

namespace HvPortal.Agent.Identity;

/// <summary>
/// One-time enrollment:
///  1. download the gateway CA certificate (public data) and compare its SHA-256 with the fingerprint
///     the administrator copied from the portal (out-of-band pin);
///  2. only then open a strictly validated TLS connection and send the one-time token + CSR.
/// The token is never sent over a connection that has not been validated against the pinned CA.
/// </summary>
public sealed class EnrollmentClient
{
    private readonly AgentIdentityStore _store;

    public EnrollmentClient(AgentIdentityStore store)
    {
        _store = store;
    }

    public async Task<AgentState> EnrollAsync(Uri gatewayUrl, string token, string caFingerprint, CancellationToken ct)
    {
        if (gatewayUrl.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("The gateway URL must use https.");
        }

        X509Certificate2 ca;
        using (var bootstrap = new HttpClient(new SocketsHttpHandler
               {
                   // Only public data (the CA certificate) is fetched here; its content is verified by the pin below.
                   SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
               }) { Timeout = TimeSpan.FromSeconds(30) })
        {
            var pem = await bootstrap.GetStringAsync(new Uri(gatewayUrl, "/agent/v1/ca"), ct);
            ca = X509Certificate2.CreateFromPem(pem);
        }

        if (!GatewayTrust.FingerprintMatches(ca, caFingerprint))
        {
            throw new InvalidOperationException(
                $"The gateway CA fingerprint {GatewayTrust.Sha256Fingerprint(ca)} does not match the expected value. Enrollment aborted.");
        }

        var (key, csr) = AgentIdentityStore.CreateKeyAndCsr(Environment.MachineName);
        using (key)
        {
            using var http = new HttpClient(new SocketsHttpHandler
            {
                SslOptions = { RemoteCertificateValidationCallback = GatewayTrust.CreateValidator(ca) },
            }) { Timeout = TimeSpan.FromSeconds(60) };

            var request = new EnrollmentRequest
            {
                Token = token,
                CsrPem = csr,
                MachineName = Environment.MachineName,
                AgentVersion = typeof(EnrollmentClient).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            };
            using var response = await http.PostAsJsonAsync(new Uri(gatewayUrl, AgentProtocol.EnrollPath), request, AgentProtocol.Json, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException($"Enrollment rejected ({(int)response.StatusCode}): {Truncate(body, 300)}");
            }
            var result = await response.Content.ReadFromJsonAsync<EnrollmentResponse>(AgentProtocol.Json, ct)
                         ?? throw new InvalidOperationException("Empty enrollment response.");

            _store.SaveIdentity(AgentIdentityStore.CombineToPfx(result.CertificatePem, key));
            var state = new AgentState
            {
                GatewayUrl = gatewayUrl,
                HostId = result.HostId,
                // Keep the CA that was verified against the pin, not whatever the response claims.
                CaCertificatePem = ca.ExportCertificatePem(),
                EnrolledAtUtc = DateTimeOffset.UtcNow,
            };
            _store.SaveState(state);
            return state;
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
