using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Crypto;
using HvPortal.Gateway.Data;

namespace HvPortal.Gateway.VmBackup;

/// <summary>
/// Rules of VM backups (ADR-031) shared by the endpoints, the job runner and the scheduler: where a restore point goes,
/// how the repository credentials are protected and handed to the agent, when a job runs next.
/// </summary>
public static partial class BackupRules
{
    public const int MaxRetentionPoints = 366;
    public static readonly string[] NotifyValues = ["never", "failure", "warning", "always"];
    public static readonly string[] ScheduleKinds = ["daily", "hourly", "manual"];

    [GeneratedRegex(@"^[\p{L}\p{N}][\p{L}\p{N} ._()-]{0,63}$", RegexOptions.CultureInvariant)]
    public static partial Regex NameRegex();

    [GeneratedRegex(@"^([01][0-9]|2[0-3]):[0-5][0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex TimeRegex();

    private static string Purpose(Guid repositoryId) => "backup-repository-password:" + repositoryId.ToString("D");

    public static byte[] Protect(KeyRing keys, Guid repositoryId, string password) => keys.Encrypt(Encoding.UTF8.GetBytes(password), Purpose(repositoryId));

    /// <exception cref="System.Security.Cryptography.CryptographicException">Wrong key ring or tampered value.</exception>
    public static string Unprotect(KeyRing keys, Guid repositoryId, byte[] encrypted) => Encoding.UTF8.GetString(keys.Decrypt(encrypted, Purpose(repositoryId)));

    /// <summary>"&lt;safe VM name&gt;_&lt;first 8 hex of the Hyper-V id&gt;": stable per VM, readable in the repository.</summary>
    public static string VmKey(string vmName, Guid hyperVId)
    {
        var sb = new StringBuilder();
        foreach (var c in vmName)
        {
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
            if (sb.Length >= 60)
            {
                break;
            }
        }
        var name = sb.ToString().Trim('.', '_');
        if (name.Length == 0 || !char.IsAsciiLetterOrDigit(name[0]))
        {
            name = "vm" + name;
        }
        return name + "_" + hyperVId.ToString("N")[..8];
    }

    public static string PointFolder(string vmName, Guid hyperVId, Guid pointId) => VmKey(vmName, hyperVId) + @"\" + pointId.ToString("D");

    /// <summary>
    /// The repository as the agent needs it for one operation: path, credentials (decrypted now, never stored elsewhere)
    /// and the staging folder of the host doing the work.
    /// </summary>
    public static BackupTarget Target(BackupRepository repo, ManagedHost host, KeyRing keys) => new()
    {
        Path = repo.Path,
        UserName = repo.PasswordEncrypted is null ? null : repo.UserName,
        Password = repo.PasswordEncrypted is null ? null : Unprotect(keys, repo.Id, repo.PasswordEncrypted),
        StagingPath = host.BackupStagingPath,
    };

    /// <summary>Null when the host can use the repository, otherwise why not (error code: message).</summary>
    public static (string Code, string Message)? CanUse(BackupRepository repo, ManagedHost host)
    {
        if (repo.HostId is { } owner && owner != host.Id)
        {
            return ("repository_other_host", "This repository is a local folder of another host: choose a share, or a repository of this host.");
        }
        if (repo.PasswordEncrypted is not null && host.BackupStagingPath is null)
        {
            return ("staging_required", $"The repository uses credentials: set a backup staging folder for {host.DisplayName} (Host > Settings), where the copies are prepared.");
        }
        return null;
    }

    public static IReadOnlyList<string> Times(BackupJob job)
    {
        try
        {
            return (JsonSerializer.Deserialize<List<string>>(job.TimesJson) ?? []).Where(t => TimeRegex().IsMatch(t)).Distinct().Order(StringComparer.Ordinal).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static bool IsValidTime(string? t) => t is not null && TimeRegex().IsMatch(t);

    public static TimeZoneInfo Zone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    public static bool IsKnownZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64)
        {
            return false;
        }
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    /// <summary>The next start after <paramref name="after"/> (null = manual job, or no valid day/time).</summary>
    public static DateTimeOffset? NextRun(BackupJob job, DateTimeOffset after)
    {
        switch (job.ScheduleKind)
        {
            case "hourly":
                var hours = Math.Clamp(job.IntervalHours, 1, 24);
                var from = job.LastRunAt ?? after;
                var next = from.AddHours(hours);
                return next > after ? next : after.AddMinutes(1);
            case "daily":
                var zone = Zone(job.TimeZoneId);
                var times = Times(job);
                if (times.Count == 0 || (job.DaysMask & 0x7F) == 0)
                {
                    return null;
                }
                var local = TimeZoneInfo.ConvertTime(after, zone);
                for (var d = 0; d <= 8; d++)
                {
                    var day = local.Date.AddDays(d);
                    if ((job.DaysMask & (1 << (int)day.DayOfWeek)) == 0)
                    {
                        continue;
                    }
                    foreach (var t in times)
                    {
                        var parts = t.Split(':');
                        var wall = day.AddHours(int.Parse(parts[0], CultureInfo.InvariantCulture)).AddMinutes(int.Parse(parts[1], CultureInfo.InvariantCulture));
                        if (zone.IsInvalidTime(wall))
                        {
                            wall = wall.AddHours(1); // skipped by a daylight-saving change: run one hour later
                        }
                        var candidate = new DateTimeOffset(wall, zone.GetUtcOffset(wall)).ToUniversalTime();
                        if (candidate > after)
                        {
                            return candidate;
                        }
                    }
                }
                return null;
            default:
                return null;
        }
    }

    /// <summary>Disks of the manifest (relative paths), for "restore a disk" and "browse files".</summary>
    public static IReadOnlyList<string> Disks(BackupPoint p)
    {
        if (p.ManifestJson is null)
        {
            return [];
        }
        try
        {
            return JsonSerializer.Deserialize<BackupManifest>(p.ManifestJson, Contracts.Agent.AgentProtocol.Json)?.Disks ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The manifest without per-file hashes when it is too large for the record (the repository keeps the full one).</summary>
    public static string? StoreManifest(BackupManifest? m)
    {
        if (m is null)
        {
            return null;
        }
        var json = JsonSerializer.Serialize(m, Contracts.Agent.AgentProtocol.Json);
        if (json.Length <= 64000)
        {
            return json;
        }
        json = JsonSerializer.Serialize(m with { Files = [] }, Contracts.Agent.AgentProtocol.Json);
        return json.Length <= 64000 ? json : null;
    }
}
