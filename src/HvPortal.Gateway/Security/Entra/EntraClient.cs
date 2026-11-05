using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace HvPortal.Gateway.Security.Entra;

/// <summary>Network side of the Entra ID sign-in (replaced by a test double in tests).</summary>
public interface IEntraClient
{
    /// <summary>OpenID Connect metadata and signing keys of the tenant (cached, refreshed by the configuration manager).</summary>
    Task<OpenIdConnectConfiguration> GetConfigurationAsync(string authority, string tenantId, CancellationToken ct);

    /// <summary>Authorization code redemption (confidential client + PKCE). Returns the id_token.</summary>
    Task<string> RedeemCodeAsync(string tokenEndpoint, string clientId, string clientSecret, string code, string redirectUri, string codeVerifier, CancellationToken ct);
}

public sealed class EntraClient : IEntraClient, IDisposable
{
    private readonly HttpClient _http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) }) { Timeout = TimeSpan.FromSeconds(20) };
    private readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> _managers = new(StringComparer.OrdinalIgnoreCase);

    public Task<OpenIdConnectConfiguration> GetConfigurationAsync(string authority, string tenantId, CancellationToken ct)
    {
        var address = $"{authority.TrimEnd('/')}/{tenantId}/v2.0/.well-known/openid-configuration";
        var manager = _managers.GetOrAdd(address, a => new ConfigurationManager<OpenIdConnectConfiguration>(a, new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever(_http) { RequireHttps = true }));
        return manager.GetConfigurationAsync(ct);
    }

    public async Task<string> RedeemCodeAsync(string tokenEndpoint, string clientId, string clientSecret, string code, string redirectUri, string codeVerifier, CancellationToken ct)
    {
        using var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier,
            ["scope"] = "openid profile email",
        });
        using var response = await _http.PostAsync(tokenEndpoint, body, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!response.IsSuccessStatusCode || !doc.RootElement.TryGetProperty("id_token", out var idToken) || idToken.GetString() is not { Length: > 0 } token)
        {
            // Error codes only (AADSTSxxxx); never log the code, the secret or tokens.
            var error = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : $"http_{(int)response.StatusCode}";
            var codes = doc.RootElement.TryGetProperty("error_codes", out var c) ? string.Join(",", c.EnumerateArray().Select(x => x.ToString())) : null;
            throw new EntraSignInException("token_redemption_failed", $"{error} {codes}".Trim());
        }
        return token;
    }

    public void Dispose() => _http.Dispose();
}

public sealed class EntraSignInException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Validated identity from an Entra ID id_token.</summary>
public sealed record EntraIdentity(string TenantId, string ObjectId, string? UserName, string? DisplayName);

public static class EntraTokenValidator
{
    /// <summary>
    /// Signature (tenant signing keys, RS256), issuer (the configured tenant), audience (the app), lifetime (2 min skew),
    /// nonce (bound to the browser through the protected state cookie) and tenant id.
    /// </summary>
    public static async Task<EntraIdentity> ValidateAsync(string idToken, OpenIdConnectConfiguration config, string tenantId, string clientId, string nonce, TimeProvider time)
    {
        var parameters = new TokenValidationParameters
        {
            ValidIssuer = config.Issuer.Replace("{tenantid}", tenantId, StringComparison.OrdinalIgnoreCase),
            ValidAudience = clientId,
            IssuerSigningKeys = config.SigningKeys,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
            LifetimeValidator = (notBefore, expires, _, p) =>
            {
                var now = time.GetUtcNow().UtcDateTime;
                return (notBefore is null || notBefore.Value - p.ClockSkew <= now) && expires is { } exp && now <= exp + p.ClockSkew;
            },
        };
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(idToken, parameters);
        if (!result.IsValid)
        {
            throw new EntraSignInException("invalid_token", result.Exception?.GetType().Name ?? "invalid");
        }
        string? Claim(string type) => result.Claims.TryGetValue(type, out var v) ? v?.ToString() : null;
        if (!string.Equals(Claim("nonce"), nonce, StringComparison.Ordinal))
        {
            throw new EntraSignInException("invalid_token", "nonce mismatch");
        }
        if (!string.Equals(Claim("tid"), tenantId, StringComparison.OrdinalIgnoreCase))
        {
            throw new EntraSignInException("wrong_tenant", "token from another tenant");
        }
        if (Claim("oid") is not { Length: > 0 } oid)
        {
            throw new EntraSignInException("invalid_token", "missing oid");
        }
        return new EntraIdentity(tenantId, oid, Claim("preferred_username") ?? Claim("upn") ?? Claim("email"), Claim("name"));
    }
}
