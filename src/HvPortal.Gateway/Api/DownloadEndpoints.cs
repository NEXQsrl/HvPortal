using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Configuration;
using Microsoft.Extensions.Options;

namespace HvPortal.Gateway.Api;

/// <summary>
/// Agent package download. The package is produced at build time (installer\Build-Release.ps1 or
/// scripts\dev\Build-AgentPackage.ps1) and placed in the packages directory; the gateway never builds or modifies it.
/// The file name is fixed (no user-supplied path). The SHA-256 is computed by the gateway itself so the operator can
/// verify the copy on the host; the signature state comes from the build manifest and is shown as-is.
/// </summary>
public static class DownloadEndpoints
{
    public const string AgentPackageFile = "HvPortal.Agent-win-x64.zip";
    public const string AgentManifestFile = "HvPortal.Agent-win-x64.json";
    /// <summary>Graphical setup of the agent (installer\HvPortal.Setup, built by Build-Release.ps1): optional, same folder.</summary>
    public const string AgentSetupFile = "HvPortal.Agent.Setup.exe";

    private static readonly ConcurrentDictionary<string, (long Length, DateTime Modified, string Sha256)> HashCache = new();

    public sealed record PackageManifest(string? Version, DateTimeOffset? BuiltAtUtc, bool Signed, string? Signer, bool SelfSigned = false, bool CatalogSigned = false);

    public static string PackagesDirectory(GatewayOptions options) =>
        options.PackagesDirectory is { Length: > 0 } d ? Path.GetFullPath(d) : Path.Combine(AppContext.BaseDirectory, "packages");

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/downloads").WithTags("Downloads");

        g.MapGet("/", async (HttpContext http, IOptions<GatewayOptions> options) =>
        {
            if (await http.GuardAsync(Permissions.HostsManage, ResourceRef.Global, "downloads.list") is { } denied)
            {
                return denied;
            }
            var info = Describe(options.Value);
            return Results.Ok(new { agent = info, agentSetup = DescribeSetup(options.Value) });
        });

        g.MapGet("/agent-setup", async (HttpContext http, IOptions<GatewayOptions> options) =>
        {
            if (await http.GuardAsync(Permissions.HostsManage, ResourceRef.Global, "downloads.agent_setup") is { } denied)
            {
                return denied;
            }
            var path = Path.Combine(PackagesDirectory(options.Value), AgentSetupFile);
            if (!File.Exists(path))
            {
                return ApiSupport.NotFound();
            }
            var info = DescribeSetup(options.Value);
            await http.WriteAuditAsync("downloads.agent_setup", resourceType: "package", resourceId: AgentSetupFile, details: new { info?.Sha256, info?.SizeBytes });
            return Results.File(path, "application/octet-stream", AgentSetupFile, enableRangeProcessing: false);
        });

        g.MapGet("/agent", async (HttpContext http, IOptions<GatewayOptions> options) =>
        {
            if (await http.GuardAsync(Permissions.HostsManage, ResourceRef.Global, "downloads.agent") is { } denied)
            {
                return denied;
            }
            var path = Path.Combine(PackagesDirectory(options.Value), AgentPackageFile);
            if (!File.Exists(path))
            {
                return ApiSupport.NotFound();
            }
            var info = Describe(options.Value);
            await http.WriteAuditAsync("downloads.agent", resourceType: "package", resourceId: AgentPackageFile,
                details: new { info?.Version, info?.Sha256, info?.Signed, info?.SelfSigned });
            return Results.File(path, "application/zip", AgentPackageFile, enableRangeProcessing: false);
        });
    }

    /// <summary>CatalogSigned: the package carries a signed catalog of all its files (needed to update agents from the portal).</summary>
    public sealed record PackageInfo(string FileName, long SizeBytes, string Sha256, string? Version, DateTimeOffset? BuiltAtUtc, bool Signed, string? Signer,
        bool SelfSigned, bool CatalogSigned = false);

    public static PackageInfo? Describe(GatewayOptions options)
    {
        var dir = PackagesDirectory(options);
        var path = Path.Combine(dir, AgentPackageFile);
        if (!File.Exists(path))
        {
            return null;
        }
        var fi = new FileInfo(path);
        var cached = Hash(fi);
        PackageManifest? manifest = null;
        var manifestPath = Path.Combine(dir, AgentManifestFile);
        if (File.Exists(manifestPath))
        {
            try
            {
                manifest = JsonSerializer.Deserialize<PackageManifest>(File.ReadAllText(manifestPath), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
            catch (JsonException)
            {
                manifest = null;
            }
        }
        return new PackageInfo(AgentPackageFile, fi.Length, cached.Sha256, manifest?.Version, manifest?.BuiltAtUtc,
            manifest?.Signed ?? false, manifest?.Signer, manifest is { Signed: true, SelfSigned: true }, manifest is { Signed: true, CatalogSigned: true });
    }

    public sealed record SetupInfo(string FileName, long SizeBytes, string Sha256);

    /// <summary>The agent setup file when this gateway has one (size and SHA-256 computed here, like the package).</summary>
    public static SetupInfo? DescribeSetup(GatewayOptions options)
    {
        var fi = new FileInfo(Path.Combine(PackagesDirectory(options), AgentSetupFile));
        return fi.Exists ? new SetupInfo(AgentSetupFile, fi.Length, Hash(fi).Sha256) : null;
    }

    private static (long Length, DateTime Modified, string Sha256) Hash(FileInfo fi)
    {
        var key = fi.FullName;
        if (!HashCache.TryGetValue(key, out var cached) || cached.Length != fi.Length || cached.Modified != fi.LastWriteTimeUtc)
        {
            using var stream = fi.OpenRead();
            cached = (fi.Length, fi.LastWriteTimeUtc, Convert.ToHexString(SHA256.HashData(stream)));
            HashCache[key] = cached;
        }
        return cached;
    }
}
