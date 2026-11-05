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

    private static readonly ConcurrentDictionary<string, (long Length, DateTime Modified, string Sha256)> HashCache = new();

    public sealed record PackageManifest(string? Version, DateTimeOffset? BuiltAtUtc, bool Signed, string? Signer);

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
            return Results.Ok(new { agent = info });
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
                details: new { info?.Version, info?.Sha256, info?.Signed });
            return Results.File(path, "application/zip", AgentPackageFile, enableRangeProcessing: false);
        });
    }

    public sealed record PackageInfo(string FileName, long SizeBytes, string Sha256, string? Version, DateTimeOffset? BuiltAtUtc, bool Signed, string? Signer);

    public static PackageInfo? Describe(GatewayOptions options)
    {
        var dir = PackagesDirectory(options);
        var path = Path.Combine(dir, AgentPackageFile);
        if (!File.Exists(path))
        {
            return null;
        }
        var fi = new FileInfo(path);
        var key = fi.FullName;
        if (!HashCache.TryGetValue(key, out var cached) || cached.Length != fi.Length || cached.Modified != fi.LastWriteTimeUtc)
        {
            using var stream = fi.OpenRead();
            cached = (fi.Length, fi.LastWriteTimeUtc, Convert.ToHexString(SHA256.HashData(stream)));
            HashCache[key] = cached;
        }
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
            manifest?.Signed ?? false, manifest?.Signer);
    }
}
