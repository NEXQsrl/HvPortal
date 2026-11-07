using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using HvPortal.Agent.Identity;
using HvPortal.Contracts.Agent;

namespace HvPortal.Agent.Channel;

/// <summary>
/// Update of this agent requested by the gateway (UpdateAgent, protocol 9; ADR-029).
/// The agent service (not an administrator) only:
///  1. downloads the agent package from the gateway's agent port (mutual TLS, pinned internal CA, same identity as the
///     channel) into &lt;agent data&gt;\update, checking size and SHA-256 against the command;
///  2. starts the scheduled task "\HvPortal\HvPortal Agent Update" that Install-Agent.ps1 registered (SYSTEM; the service
///     account may only run it, not change it).
/// The task (installer\Update-HvPortalAgent.ps1) copies the package to a private folder and installs it only when its
/// signed catalog covers every file and is signed by the same certificate as the installed agent, and the version is
/// not older: neither the gateway nor this service can make it install code the publisher did not sign. Unsigned builds
/// are never installed this way (they are updated by hand).
/// </summary>
public sealed partial class AgentUpdater
{
    public const string TaskPath = @"\HvPortal\";
    public const string TaskName = "HvPortal Agent Update";
    public const long MaxPackageBytes = 1L << 30;
    private static readonly TimeSpan TaskCheckCache = TimeSpan.FromMinutes(10);
    private static (DateTime At, bool Installed)? _taskCheck;

    private readonly AgentIdentityStore _store;
    private readonly ILogger _logger;

    public AgentUpdater(AgentIdentityStore store, ILogger<AgentUpdater> logger)
    {
        _store = store;
        _logger = logger;
    }

    [GeneratedRegex(@"^[0-9]{1,5}\.[0-9]{1,5}\.[0-9]{1,5}(?:\.[0-9]{1,5})?$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"^[0-9A-Fa-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();

    public static string UpdateDirectory(string dataDirectory) => Path.Combine(dataDirectory, "update");

    /// <summary>Null when acceptable.</summary>
    internal static string? Validate(AgentUpdateRequest? u) =>
        u is null ? "missing update"
        : !VersionRegex().IsMatch(u.Version) ? "invalid version"
        : !Sha256Regex().IsMatch(u.Sha256) ? "invalid SHA-256"
        : u.SizeBytes is < 1 or > MaxPackageBytes ? "invalid size" : null;

    public async Task<AgentCommandResult> RunAsync(AgentCommand command, CancellationToken ct)
    {
        AgentCommandResult Result(AgentCommandStatus status, string message) => new() { CommandId = command.CommandId, Status = status, Message = message };
        if (Validate(command.Update) is { } invalid)
        {
            return Result(AgentCommandStatus.Rejected, "Invalid update request: " + invalid + ".");
        }
        var update = command.Update!;
        if (!UpdaterInstalled(refresh: true))
        {
            return Result(AgentCommandStatus.NotSupported,
                "The updater task is not registered on this machine. Update the agent once by hand with Install-Agent.ps1 (0.7.0 or later): "
                + "later versions can then be installed from the portal.");
        }
        var state = _store.LoadState();
        using var cert = _store.LoadCertificate();
        if (state is null || cert is null)
        {
            return Result(AgentCommandStatus.Failed, "Agent identity missing.");
        }
        var dir = UpdateDirectory(_store.DataDirectory);
        Directory.CreateDirectory(dir);
        var part = Path.Combine(dir, "incoming.zip.part");
        var package = Path.Combine(dir, "package.zip");
        try
        {
            using var ca = X509Certificate2.CreateFromPem(state.CaCertificatePem);
            using var handler = new SocketsHttpHandler();
            handler.SslOptions.ClientCertificates = [cert];
            handler.SslOptions.RemoteCertificateValidationCallback = GatewayTrust.CreateValidator(ca);
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
            var uri = new UriBuilder(state.GatewayUrl) { Scheme = "https", Path = AgentProtocol.PackagePath, Query = "" }.Uri;
            _logger.LogInformation("Downloading agent package {Version} from the gateway", update.Version);
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                return Result(AgentCommandStatus.Failed, $"The gateway refused the package download ({(int)response.StatusCode}).");
            }
            string sha;
            long total = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var target = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[1 << 16];
                int n;
                while ((n = await source.ReadAsync(buffer, ct)) > 0)
                {
                    total += n;
                    if (total > update.SizeBytes || total > MaxPackageBytes)
                    {
                        return Result(AgentCommandStatus.Failed, "The package is larger than announced: discarded.");
                    }
                    hash.AppendData(buffer, 0, n);
                    await target.WriteAsync(buffer.AsMemory(0, n), ct);
                }
                sha = Convert.ToHexString(hash.GetHashAndReset());
            }
            if (total != update.SizeBytes || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(sha), Convert.FromHexString(update.Sha256)))
            {
                return Result(AgentCommandStatus.Failed, "The downloaded package does not match the size and SHA-256 sent by the gateway: discarded.");
            }
            File.Move(part, package, overwrite: true);
            await File.WriteAllTextAsync(Path.Combine(dir, "request.json"), JsonSerializer.Serialize(new
            {
                version = update.Version,
                sha256 = sha,
                sizeBytes = total,
                requestedUtc = DateTimeOffset.UtcNow,
                commandId = command.CommandId,
            }), ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException
                                       or CryptographicException)
        {
            return Result(AgentCommandStatus.Failed, "Package download failed: " + ex.Message);
        }
        finally
        {
            try
            {
                File.Delete(part);
            }
            catch (IOException)
            {
            }
        }

        var (code, output) = await RunSchtasksAsync(["/Run", "/TN", TaskPath + TaskName], ct);
        if (code != 0)
        {
            return Result(AgentCommandStatus.Failed, $"The package was verified but the updater task could not be started (schtasks {code}): {output}");
        }
        _logger.LogWarning("Agent update {Version} handed to the updater task: the service will restart", update.Version);
        return Result(AgentCommandStatus.Succeeded,
            $"Package {update.Version} downloaded and verified (SHA-256); the updater task checks its signature and installs it. The agent restarts in a moment.");
    }

    /// <summary>True when the updater task exists (checked with schtasks /Query, cached for 10 minutes).</summary>
    public static bool UpdaterInstalled(bool refresh = false)
    {
        if (!refresh && _taskCheck is { } c && DateTime.UtcNow - c.At < TaskCheckCache)
        {
            return c.Installed;
        }
        bool installed;
        try
        {
            installed = RunSchtasksAsync(["/Query", "/TN", TaskPath + TaskName], CancellationToken.None).GetAwaiter().GetResult().Code == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            installed = false;
        }
        _taskCheck = (DateTime.UtcNow, installed);
        return installed;
    }

    /// <summary>"&lt;time&gt; &lt;status&gt; &lt;version&gt;: &lt;message&gt;" from update\last-result.json written by the updater task.</summary>
    public static string? LastResult(string dataDirectory)
    {
        var file = Path.Combine(UpdateDirectory(dataDirectory), "last-result.json");
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var r = doc.RootElement;
            string? S(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var text = $"{S("timeUtc")} {S("status")} {S("version")}: {S("message")}";
            return text.Length > 500 ? text[..500] : text;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task<(int Code, string Output)> RunSchtasksAsync(string[] arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in arguments)
        {
            psi.ArgumentList.Add(a);
        }
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("schtasks.exe could not be started.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var stdout = p.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = p.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await p.WaitForExitAsync(timeout.Token);
            var text = ((await stdout) + " " + (await stderr)).Trim();
            return (p.ExitCode, text.Length > 300 ? text[..300] : text);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // schtasks did not answer in 30 s: end it and report a failure instead of an unhandled exception.
            try
            {
                p.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            return (-1, "schtasks.exe did not answer within 30 seconds.");
        }
    }
}
