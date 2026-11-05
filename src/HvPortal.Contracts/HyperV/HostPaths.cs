using System.Text.RegularExpressions;

namespace HvPortal.Contracts.HyperV;

/// <summary>
/// Rules for host folders chosen in the portal (VM location, folder browser). Shared by the gateway (syntax) and the
/// agent (syntax + real file system: fixed local drive, no reparse points, no hidden/system folders).
/// </summary>
public static partial class HostPaths
{
    public const int MaxPathLength = 200;
    public const int MaxEntries = 500;

    /// <summary>
    /// Top-level folders of a drive that are never offered nor accepted as VM location: operating system, user profiles,
    /// IIS, Veeam catalog, OVH update agent, Windows packages, logs. Folders starting with '$' are always excluded.
    /// </summary>
    public static readonly string[] BlockedRootFolders =
    [
        "Windows", "Users", "User", "inetpub", "VBRCatalog", "ovhupd", "Packages", "PerfLogs",
        "Program Files", "Program Files (x86)", "ProgramData", "Recovery", "Boot", "System Volume Information",
        "Documents and Settings", "Config.Msi", "MSOCache", "OneDriveTemp", "Intel", "AMD", "NVIDIA",
    ];

    /// <summary>Drive-letter path, no UNC/device prefix, no traversal, no wildcard or stream characters.</summary>
    [GeneratedRegex(@"^[A-Za-z]:\\(?:[^\x00-\x1f""*?<>|:\\/]+\\?)*$", RegexOptions.CultureInvariant)]
    private static partial Regex LocalFolderRegex();

    public static bool IsValidLocalFolder(string? path) =>
        path is { Length: >= 3 and <= MaxPathLength } && LocalFolderRegex().IsMatch(path)
        && !path.Split('\\').Any(s => s is "." or ".." || s.EndsWith(' ') || s.EndsWith('.'));

    /// <summary>Normalized form: upper-case drive letter, no trailing backslash except for the drive root ("D:\").</summary>
    public static string Normalize(string path)
    {
        var p = path.Trim();
        p = char.ToUpperInvariant(p[0]) + p[1..];
        return p.Length > 3 ? p.TrimEnd('\\') : p;
    }

    public static bool IsBlockedRootFolder(string name) =>
        name.StartsWith('$') || BlockedRootFolders.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the path is inside (or is) a blocked top-level folder of its drive.</summary>
    public static bool IsBlocked(string path)
    {
        var segments = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 1 && IsBlockedRootFolder(segments[1]);
    }

    /// <summary>Null when acceptable as VM location, otherwise an error code.</summary>
    public static string? ValidateLocation(string? path)
    {
        if (path is null)
        {
            return null;
        }
        if (!IsValidLocalFolder(path))
        {
            return "invalid_location";
        }
        return IsBlocked(path) ? "location_not_allowed" : null;
    }
}
