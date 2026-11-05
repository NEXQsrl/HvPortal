using System.Text.Json;
using System.Text.Json.Serialization;
using HvPortal.Gateway.Data;
using Microsoft.EntityFrameworkCore;

namespace HvPortal.Gateway.Security;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MfaRequirement
{
    /// <summary>MFA optional for everyone.</summary>
    None,
    /// <summary>Default: required for users holding any privileged permission (users/roles/settings/hosts management).</summary>
    Privileged,
    /// <summary>Required for every user.</summary>
    All,
}

public sealed record SecuritySettings
{
    public MfaRequirement MfaRequiredFor { get; init; } = MfaRequirement.Privileged;
    public int SessionIdleMinutes { get; init; } = 30;
    public int SessionAbsoluteHours { get; init; } = 12;
    public int PasswordMinLength { get; init; } = PasswordPolicy.AbsoluteMinimum;
    /// <summary>Local sign-in policy once Entra ID is configured (M2): "allowed" or "break-glass-only".</summary>
    public string LocalSignIn { get; init; } = "allowed";

    public SecuritySettings Normalize() => this with
    {
        SessionIdleMinutes = Math.Clamp(SessionIdleMinutes, 5, 240),
        SessionAbsoluteHours = Math.Clamp(SessionAbsoluteHours, 1, 72),
        PasswordMinLength = Math.Clamp(PasswordMinLength, PasswordPolicy.AbsoluteMinimum, 128),
        LocalSignIn = LocalSignIn is "break-glass-only" ? LocalSignIn : "allowed",
    };
}

public sealed class SettingsStore
{
    private const string SecurityKey = "security";
    private readonly PortalDbContext _db;
    private readonly TimeProvider _time;

    public SettingsStore(PortalDbContext db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    public async Task<SecuritySettings> GetSecurityAsync(CancellationToken ct = default)
    {
        var row = await _db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == SecurityKey, ct);
        return row is null ? new SecuritySettings() : (JsonSerializer.Deserialize<SecuritySettings>(row.Value) ?? new()).Normalize();
    }

    public async Task SaveSecurityAsync(SecuritySettings settings, CancellationToken ct = default)
    {
        await SetAsync(SecurityKey, JsonSerializer.Serialize(settings.Normalize()), ct);
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct = default) =>
        (await _db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct))?.Value;

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        var row = await _db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null)
        {
            _db.Settings.Add(new Setting { Key = key, Value = value, UpdatedAt = _time.GetUtcNow() });
        }
        else
        {
            row.Value = value;
            row.UpdatedAt = _time.GetUtcNow();
        }
        await _db.SaveChangesAsync(ct);
    }
}
