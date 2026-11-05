using System.Security.Cryptography;
using HvPortal.Gateway.Security.Entra;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace HvPortal.Gateway.Tests.Infrastructure;

/// <summary>
/// TEST DOUBLE for Microsoft Entra ID: tenant metadata with a local RSA signing key, and a token endpoint that returns
/// an id_token for the identity the test chooses. Token validation in the gateway is the real one.
/// </summary>
public sealed class FakeEntraClient : IEntraClient, IDisposable
{
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly RsaSecurityKey _key;

    public FakeEntraClient()
    {
        _key = new RsaSecurityKey(_rsa) { KeyId = "test-key" };
    }

    /// <summary>Claims of the next id_token (tid, oid, preferred_username, name, nonce ...).</summary>
    public Dictionary<string, object> NextClaims { get; } = new();
    public string? NextAudience { get; set; }
    /// <summary>Sign with another key (invalid signature).</summary>
    public bool SignWithForeignKey { get; set; }
    public int Redemptions { get; private set; }

    public Task<OpenIdConnectConfiguration> GetConfigurationAsync(string authority, string tenantId, CancellationToken ct)
    {
        var c = new OpenIdConnectConfiguration
        {
            Issuer = "https://login.microsoftonline.com/{tenantid}/v2.0",
            AuthorizationEndpoint = $"{authority}/{tenantId}/oauth2/v2.0/authorize",
            TokenEndpoint = $"{authority}/{tenantId}/oauth2/v2.0/token",
        };
        c.SigningKeys.Add(_key);
        return Task.FromResult(c);
    }

    public Task<string> RedeemCodeAsync(string tokenEndpoint, string clientId, string clientSecret, string code, string redirectUri, string codeVerifier, CancellationToken ct)
    {
        Redemptions++;
        var tid = NextClaims.TryGetValue("tid", out var t) ? t.ToString()! : Guid.Empty.ToString();
        using var foreign = RSA.Create(2048);
        var key = SignWithForeignKey ? new RsaSecurityKey(foreign) { KeyId = "test-key" } : _key;
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = $"https://login.microsoftonline.com/{tid}/v2.0",
            Audience = NextAudience ?? clientId,
            Claims = new Dictionary<string, object>(NextClaims),
            IssuedAt = DateTime.UtcNow,
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            Expires = DateTime.UtcNow.AddMinutes(30),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
        });
        return Task.FromResult(token);
    }

    public void Dispose() => _rsa.Dispose();
}
