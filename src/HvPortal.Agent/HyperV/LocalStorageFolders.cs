using System.Text.Json;
using HvPortal.Contracts.HyperV;

namespace HvPortal.Agent.HyperV;

/// <summary>
/// Folders of this host the portal may use (protocol 9), from &lt;agent data&gt;\storage.json written by
/// Install-Agent.ps1 -StorageFolders (elevated; the file is private to SYSTEM, Administrators and the service). When the
/// list is not empty, the file explorer, the folder pickers, VM locations, new disks, ISO and template libraries and
/// backup folders are accepted only inside these folders. The gateway cannot change the list: it is decided on the host.
/// A file that cannot be read stops the agent from using any folder (fail closed) instead of falling back to "all".
/// </summary>
public static class LocalStorageFolders
{
    public const string FileName = "storage.json";

    private sealed record Config(IReadOnlyList<string>? Folders);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Normalized folders ("D:\VMs"); empty = not restricted.</summary>
    public static IReadOnlyList<string> Folders { get; private set; } = [];

    /// <summary>The configuration file exists but is not valid: every folder operation is refused.</summary>
    public static string? LoadError { get; private set; }

    public static void Load(string dataDirectory) => (Folders, LoadError) = Read(dataDirectory);

    /// <summary>The folders of the configuration file, or the reason it cannot be used (then no folder at all).</summary>
    internal static (IReadOnlyList<string> Folders, string? Error) Read(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, FileName);
        if (!File.Exists(path))
        {
            return ([], null);
        }
        try
        {
            var config = JsonSerializer.Deserialize<Config>(File.ReadAllText(path), Json);
            var folders = (config?.Folders ?? []).Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => HostPaths.Normalize(f.Trim())).ToList();
            if (StorageFolderRules.Validate(folders) is { } error)
            {
                throw new InvalidDataException(error);
            }
            return (folders, null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return ([], $"{path} is not valid ({ex.Message}): fix it with Install-Agent.ps1 -StorageFolders.");
        }
    }

    /// <summary>For tests.</summary>
    internal static void Set(IReadOnlyList<string> folders, string? error = null)
    {
        Folders = folders;
        LoadError = error;
    }
}
