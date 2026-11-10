using System.Security.Cryptography;
using HvPortal.Gateway.Updates;

namespace HvPortal.Gateway.Tests.Infrastructure;

/// <summary>
/// TEST DOUBLE of the GitHub releases and of the updater task: never contacts the Internet nor starts anything. Lives
/// only in the test project.
/// </summary>
public sealed class FakeReleases : IReleaseSource, IUpdaterLauncher
{
    /// <summary>Content served as the gateway setup of <see cref="Latest"/>.</summary>
    public byte[] SetupBytes { get; set; } = "MZ fake setup"u8.ToArray();

    public ReleaseInfo? Latest { get; set; }
    public Exception? CheckError { get; set; }
    public string? BlockerValue { get; set; }
    public int Checks { get; private set; }
    public int Starts { get; private set; }

    /// <summary>A release <paramref name="version"/> whose published checksum matches <see cref="SetupBytes"/> (or not).</summary>
    public void Publish(string version, bool checksum = true, bool tampered = false)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(SetupBytes));
        Latest = new ReleaseInfo(version, "v" + version, DateTimeOffset.UtcNow, "https://github.com/NEXQsrl/HvPortal/releases/tag/v" + version, "notes",
            new ReleaseAsset("HvPortal.Gateway.Setup.exe", SetupBytes.Length, "https://github.com/NEXQsrl/HvPortal/releases/download/v" + version + "/HvPortal.Gateway.Setup.exe",
                checksum ? (tampered ? new string('0', 64) : hash) : null), null);
    }

    public Task<ReleaseInfo?> LatestAsync(CancellationToken ct)
    {
        Checks++;
        return CheckError is { } e ? Task.FromException<ReleaseInfo?>(e) : Task.FromResult(Latest);
    }

    public async Task DownloadAsync(ReleaseAsset asset, Stream target, long maxBytes, CancellationToken ct) => await target.WriteAsync(SetupBytes, ct);

    public string? Blocker() => BlockerValue;

    public (bool Started, string? Error) Start()
    {
        Starts++;
        return (true, null);
    }
}
