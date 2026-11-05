using System.Text.Json;
using HvPortal.Gateway.Crypto;

namespace HvPortal.Gateway.Security.Entra;

/// <summary>
/// Microsoft Entra ID sign-in configuration (Settings > Microsoft Entra ID), stored in the Settings table.
/// The client secret is encrypted with the gateway key ring (purpose "entra-client-secret") and never returned.
/// </summary>
public sealed record EntraConfig
{
    public bool Enabled { get; init; }
    public string? TenantId { get; init; }
    public string? ClientId { get; init; }
    /// <summary>Cloud instance: global by default (sovereign clouds allowed, see Authorities).</summary>
    public string Authority { get; init; } = EntraConfigStore.GlobalAuthority;
    public string? ClientSecretEncrypted { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }

    public bool IsComplete => Guid.TryParse(TenantId, out _) && Guid.TryParse(ClientId, out _) && ClientSecretEncrypted is { Length: > 0 };
    public bool IsActive => Enabled && IsComplete;
}

public sealed class EntraConfigStore(SettingsStore settings, KeyRing keys, TimeProvider time)
{
    public const string SettingKey = "entra";
    public const string SecretPurpose = "entra-client-secret";
    public const string GlobalAuthority = "https://login.microsoftonline.com";

    /// <summary>Microsoft identity platform instances (global, US Government, China). Nothing else is accepted.</summary>
    public static readonly string[] Authorities = [GlobalAuthority, "https://login.microsoftonline.us", "https://login.partner.microsoftonline.cn"];

    public async Task<EntraConfig> GetAsync(CancellationToken ct = default) =>
        await settings.GetAsync(SettingKey, ct) is { } json ? JsonSerializer.Deserialize<EntraConfig>(json) ?? new EntraConfig() : new EntraConfig();

    public async Task<EntraConfig> SaveAsync(EntraConfig config, string? newSecret, CancellationToken ct = default)
    {
        var encrypted = newSecret switch
        {
            null => config.ClientSecretEncrypted,
            "" => null,
            _ => Convert.ToBase64String(keys.Encrypt(System.Text.Encoding.UTF8.GetBytes(newSecret), SecretPurpose)),
        };
        var saved = config with { ClientSecretEncrypted = encrypted, UpdatedAt = time.GetUtcNow() };
        await settings.SetAsync(SettingKey, JsonSerializer.Serialize(saved), ct);
        return saved;
    }

    public string? ClientSecret(EntraConfig config) =>
        config.ClientSecretEncrypted is { Length: > 0 } e ? System.Text.Encoding.UTF8.GetString(keys.Decrypt(Convert.FromBase64String(e), SecretPurpose)) : null;
}
