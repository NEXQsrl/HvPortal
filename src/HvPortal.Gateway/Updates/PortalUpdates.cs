using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using HvPortal.Gateway.Configuration;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HvPortal.Gateway.Updates;

/// <summary>A published release (GitHub) and its setup files with the SHA-256 published with them.</summary>
public sealed record ReleaseAsset(string Name, long Size, string DownloadUrl, string? Sha256);

public sealed record ReleaseInfo(string Version, string Tag, DateTimeOffset? PublishedAt, string? HtmlUrl, string? Notes, ReleaseAsset? GatewaySetup, ReleaseAsset? AgentSetup);

/// <summary>Where releases come from (GitHub; a test double in the tests).</summary>
public interface IReleaseSource
{
    Task<ReleaseInfo?> LatestAsync(CancellationToken ct);

    /// <summary>Copies the asset into <paramref name="target"/>; at most <paramref name="maxBytes"/>.</summary>
    Task DownloadAsync(ReleaseAsset asset, Stream target, long maxBytes, CancellationToken ct);
}

/// <summary>The SYSTEM task that installs a staged gateway setup (Install-Gateway.ps1 registers it).</summary>
public interface IUpdaterLauncher
{
    /// <summary>Why updates cannot be installed by the portal here (container, console start, task missing); null = they can.</summary>
    string? Blocker();
    (bool Started, string? Error) Start();
}

/// <summary>
/// Releases of HvPortal on GitHub (Gateway:Updates:Repository, default NEXQsrl/HvPortal), read through the public API
/// without credentials. Called only when an administrator asks for a check, or when automatic checks are turned on.
/// </summary>
public sealed partial class GitHubReleaseSource(IHttpClientFactory http, IOptions<GatewayOptions> options) : IReleaseSource
{
    public const string ClientName = "github-releases";

    private sealed record ApiAsset([property: JsonPropertyName("name")] string? Name, [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("browser_download_url")] string? Url, [property: JsonPropertyName("digest")] string? Digest);

    private sealed record ApiRelease([property: JsonPropertyName("tag_name")] string? Tag, [property: JsonPropertyName("published_at")] DateTimeOffset? PublishedAt,
        [property: JsonPropertyName("html_url")] string? HtmlUrl, [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("draft")] bool Draft, [property: JsonPropertyName("assets")] List<ApiAsset>? Assets);

    [GeneratedRegex(@"^[A-Za-z0-9-]{1,39}/[A-Za-z0-9._-]{1,100}$", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryRegex();

    // "<64 hex>  <file name>" lines of the release notes (written by Publish-GitHubRelease.ps1).
    [GeneratedRegex(@"^([0-9a-fA-F]{64}) +([A-Za-z0-9._-]{1,100})$", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex ChecksumLineRegex();

    public async Task<ReleaseInfo?> LatestAsync(CancellationToken ct)
    {
        var repo = options.Value.Updates.Repository;
        if (!RepositoryRegex().IsMatch(repo))
        {
            throw new InvalidOperationException("Invalid Gateway:Updates:Repository (owner/name).");
        }
        using var client = http.CreateClient(ClientName);
        using var response = await client.GetAsync($"https://api.github.com/repos/{repo}/releases/latest", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        var r = await response.Content.ReadFromJsonAsync<ApiRelease>(ct);
        if (r?.Tag is not { } tag || r.Draft || AgentVersions.Parse(tag.TrimStart('v', 'V')) is not { } version)
        {
            return null;
        }
        // Notes edited on github.com come back with CRLF; a file listed twice keeps its first line.
        var sums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in ChecksumLineRegex().Matches((r.Body ?? "").Replace("\r", "", StringComparison.Ordinal)))
        {
            sums.TryAdd(m.Groups[2].Value, m.Groups[1].Value.ToLowerInvariant());
        }
        ReleaseAsset? Asset(string name)
        {
            var a = r.Assets?.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (a?.Url is not { } url || !IsGitHubUrl(url))
            {
                return null;
            }
            // GitHub's own digest of the uploaded file, else the checksum published in the release notes.
            var digest = a.Digest is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && d.Length == 71 ? d[7..].ToLowerInvariant() : null;
            return new ReleaseAsset(name, a.Size, url, digest ?? sums.GetValueOrDefault(name));
        }
        var notes = r.Body is { Length: > 4000 } body ? body[..4000] : r.Body;
        return new ReleaseInfo(version.ToString(3), tag, r.PublishedAt, r.HtmlUrl is { } h && IsGitHubUrl(h) ? h : null, notes,
            Asset("HvPortal.Gateway.Setup.exe"), Asset("HvPortal.Agent.Setup.exe"));
    }

    public async Task DownloadAsync(ReleaseAsset asset, Stream target, long maxBytes, CancellationToken ct)
    {
        if (!IsGitHubUrl(asset.DownloadUrl))
        {
            throw new InvalidOperationException("The download address is not on GitHub.");
        }
        using var client = http.CreateClient(ClientName);
        using var response = await client.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        // Redirects go to GitHub's file storage, always over HTTPS (HttpClient never follows HTTPS -> HTTP).
        if (response.RequestMessage?.RequestUri is not { Scheme: "https" })
        {
            throw new InvalidOperationException("The download was not served over HTTPS.");
        }
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new InvalidOperationException("The download is larger than the published size.");
            }
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    internal static bool IsGitHubUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(u.UserInfo)
        && (u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || u.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Starts "\HvPortal\HvPortal Gateway Update" with schtasks (the task's ACL lets only the gateway service account start it).</summary>
public sealed class ScheduledTaskUpdaterLauncher(ILogger<ScheduledTaskUpdaterLauncher> logger) : IUpdaterLauncher
{
    public const string TaskPath = @"\HvPortal\HvPortal Gateway Update";

    public string? Blocker() =>
        !OperatingSystem.IsWindows() ? "container"
        : !ProductInfo.RunningAsService ? "not_a_service"
        : Run("/Query", "/TN", TaskPath).Code == 0 ? null : "updater_missing";

    public (bool Started, string? Error) Start()
    {
        var (code, output) = Run("/Run", "/TN", TaskPath);
        return code == 0 ? (true, null) : (false, $"schtasks {code}: {output}");
    }

    private (int Code, string Output) Run(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"))
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            if (!p.WaitForExit(30_000))
            {
                p.Kill(true);
                return (-1, "schtasks.exe did not answer within 30 seconds.");
            }
            return (p.ExitCode, output.Trim());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogWarning(ex, "schtasks failed");
            return (-1, ex.Message);
        }
    }
}

/// <summary>
/// Updates of the portal from the GitHub releases (ADR-042). Nothing contacts GitHub unless an administrator asks for a
/// check or turns automatic checks on. Installing: the gateway setup of the release is downloaded, its SHA-256 compared
/// with the checksum published with the release, staged in &lt;data&gt;\update and handed to the SYSTEM updater task, which
/// installs it only when it is signed by the same publisher as the installed gateway.
/// </summary>
public sealed class PortalUpdateService(PortalDbContext db, IReleaseSource releases, IUpdaterLauncher launcher, GatewayPaths paths, TimeProvider time,
    ILogger<PortalUpdateService> logger)
{
    public const string SettingKey = "portal-updates";
    /// <summary>The gateway setup is about 140 MB: anything far larger is refused.</summary>
    public const long MaxSetupBytes = 600L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed record State
    {
        public bool AutoCheck { get; init; }
        public DateTimeOffset? LastCheckAt { get; init; }
        public string? LastError { get; init; }
        public ReleaseInfo? Latest { get; init; }
        public DateTimeOffset? InstallRequestedAt { get; init; }
        public string? InstallRequestedVersion { get; init; }
    }

    public sealed record LastResult(DateTimeOffset? TimeUtc, string? Status, string? Version, string? Message);

    public string UpdateFolder => Path.Combine(paths.Root, "update");

    public async Task<State> LoadAsync(CancellationToken ct)
    {
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == SettingKey, ct);
        if (row?.Value is not { Length: > 0 } value)
        {
            return new State();
        }
        try
        {
            return JsonSerializer.Deserialize<State>(value, Json) ?? new State();
        }
        catch (JsonException)
        {
            return new State();
        }
    }

    public async Task SaveAsync(State state, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(state, Json);
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == SettingKey, ct);
        if (row is null)
        {
            db.Settings.Add(new Setting { Key = SettingKey, Value = json, UpdatedAt = time.GetUtcNow() });
        }
        else
        {
            row.Value = json;
            row.UpdatedAt = time.GetUtcNow();
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<State> CheckAsync(CancellationToken ct)
    {
        var state = await LoadAsync(ct);
        try
        {
            var latest = await releases.LatestAsync(ct);
            state = state with { LastCheckAt = time.GetUtcNow(), LastError = latest is null ? "No published release found." : null, Latest = latest ?? state.Latest };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException)
        {
            logger.LogWarning(ex, "Release check failed");
            state = state with { LastCheckAt = time.GetUtcNow(), LastError = ex.Message };
        }
        await SaveAsync(state, ct);
        return state;
    }

    public static bool IsNewer(string? candidate, string current) =>
        AgentVersions.Parse(candidate) is { } c && AgentVersions.Parse(current) is { } v && c > v;

    /// <summary>Why the portal cannot install updates by itself here (null = it can).</summary>
    public string? InstallBlocker() => launcher.Blocker();

    public LastResult? ReadLastResult()
    {
        var file = Path.Combine(UpdateFolder, "last-result.json");
        try
        {
            if (!File.Exists(file) || new FileInfo(file).Length > 64 * 1024)
            {
                return null;
            }
            return JsonSerializer.Deserialize<LastResult>(File.ReadAllText(file), Json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Downloads, verifies and stages the gateway setup of <paramref name="version"/>, then starts the updater task.</summary>
    public async Task<(string? Code, string? Message)> InstallAsync(string version, CancellationToken ct)
    {
        var state = await LoadAsync(ct);
        if (InstallBlocker() is { } blocker)
        {
            return (blocker, "The portal cannot install updates by itself here.");
        }
        if (state.Latest is not { } latest || latest.Version != version)
        {
            return ("not_latest", "Check for updates first: this is not the latest release found.");
        }
        if (!IsNewer(latest.Version, ProductInfo.Version))
        {
            return ("not_newer", $"The portal already runs {ProductInfo.Version}.");
        }
        if (latest.GatewaySetup is not { } asset)
        {
            return ("no_setup", "The release has no HvPortal.Gateway.Setup.exe.");
        }
        if (asset.Sha256 is not { Length: 64 } expected)
        {
            // Without a published checksum the download cannot be checked before it reaches the updater.
            return ("no_checksum", "The release does not publish the SHA-256 of the setup: install it by hand.");
        }
        if (asset.Size is <= 0 or > MaxSetupBytes)
        {
            return ("invalid_size", "The size of the setup is not plausible.");
        }
        Directory.CreateDirectory(UpdateFolder);
        var partial = Path.Combine(UpdateFolder, "HvPortal.Gateway.Setup.exe.partial");
        var staged = Path.Combine(UpdateFolder, "HvPortal.Gateway.Setup.exe");
        try
        {
            await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await releases.DownloadAsync(asset, file, asset.Size, ct);
            }
            var length = new FileInfo(partial).Length;
            string actual;
            await using (var read = File.OpenRead(partial))
            {
                actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(read, ct));
            }
            if (length != asset.Size || !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                return ("checksum_mismatch", "The downloaded setup does not match the size and SHA-256 published with the release: nothing was installed.");
            }
            File.Move(partial, staged, overwrite: true);
            await File.WriteAllTextAsync(Path.Combine(UpdateFolder, "request.json"), JsonSerializer.Serialize(new { version = latest.Version, sha256 = actual }, Json), ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or TaskCanceledException)
        {
            return ("download_failed", "The setup could not be downloaded: " + ex.Message);
        }
        finally
        {
            File.Delete(partial);
        }
        var (started, error) = launcher.Start();
        if (!started)
        {
            return ("updater_failed", "The setup was verified but the updater task could not be started: " + error);
        }
        await SaveAsync(state with { InstallRequestedAt = time.GetUtcNow(), InstallRequestedVersion = latest.Version }, ct);
        return (null, null);
    }
}

/// <summary>Automatic release checks (twice a day), only when an administrator turned them on.</summary>
public sealed class PortalUpdateChecker(IServiceScopeFactory scopes, TimeProvider time, IOptions<GatewayOptions> options, ILogger<PortalUpdateChecker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Updates.CheckerEnabled)
        {
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), time, stoppingToken);
                using var scope = scopes.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<PortalUpdateService>();
                var state = await service.LoadAsync(stoppingToken);
                if (state.AutoCheck && (state.LastCheckAt is not { } at || time.GetUtcNow() - at > TimeSpan.FromHours(12)))
                {
                    await service.CheckAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Automatic release check failed");
            }
        }
    }
}
