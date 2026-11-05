using HvPortal.Contracts.HyperV;

namespace HvPortal.Agent.HyperV;

/// <summary>
/// Read-only folder browser for the portal's VM-location and ISO pickers (operation BrowseFolders), and the file-system
/// checks applied to a requested VM location. Only local fixed drives; hidden, system and reparse-point folders
/// (junctions, symbolic links, mount points) are neither listed nor accepted; top-level system folders are excluded
/// (HostPaths.BlockedRootFolders). Runs in-process with the agent's own identity: nothing is created or modified.
/// </summary>
public static class HostFolderBrowser
{
    private const FileAttributes Excluded = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint;

    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = Excluded,
        ReturnSpecialDirectories = false,
    };

    public static FolderListing Browse(FolderBrowseRequest request)
    {
        if (request.Path is null)
        {
            return new FolderListing { Drives = FixedDrives() };
        }
        var path = CheckFolder(request.Path, mustExist: true);
        var dir = new DirectoryInfo(path);
        var atRoot = dir.Parent is null;
        var truncated = false;
        var folders = new List<FolderEntry>();
        foreach (var d in dir.EnumerateDirectories("*", Options))
        {
            if ((atRoot && HostPaths.IsBlockedRootFolder(d.Name)) || d.Name.StartsWith('$'))
            {
                continue;
            }
            if (folders.Count >= HostPaths.MaxEntries)
            {
                truncated = true;
                break;
            }
            folders.Add(new FolderEntry(d.Name, d.FullName));
        }
        var isos = new List<IsoFile>();
        if (request.IncludeIsos)
        {
            foreach (var f in dir.EnumerateFiles("*.iso", Options))
            {
                if (!f.Extension.Equals(".iso", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // 8.3 short-name matches such as *.iso_old
                }
                if (isos.Count >= HostPaths.MaxEntries)
                {
                    truncated = true;
                    break;
                }
                isos.Add(new IsoFile(f.FullName, f.Length, f.LastWriteTimeUtc));
            }
        }
        long? free = null;
        try
        {
            free = new DriveInfo(path[..3]).AvailableFreeSpace;
        }
        catch (IOException)
        {
        }
        return new FolderListing
        {
            Path = path,
            Parent = dir.Parent?.FullName,
            Folders = folders.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            Isos = isos.OrderBy(f => f.Path, StringComparer.CurrentCultureIgnoreCase).ToList(),
            FreeBytes = free,
            Truncated = truncated,
        };
    }

    public static IReadOnlyList<DriveEntry> FixedDrives()
    {
        var list = new List<DriveEntry>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType == DriveType.Fixed && d.IsReady)
                {
                    list.Add(new DriveEntry(d.RootDirectory.FullName, string.IsNullOrWhiteSpace(d.VolumeLabel) ? null : d.VolumeLabel, d.TotalSize, d.AvailableFreeSpace));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Drive vanished or locked (e.g. BitLocker): skip it.
            }
        }
        return list;
    }

    /// <summary>
    /// Validates a folder against the real file system and returns its normalized full path.
    /// mustExist = false (VM location): the missing tail is created later by the operations script; every existing
    /// ancestor is still checked.
    /// </summary>
    /// <exception cref="HostPathException">The folder is not acceptable (message is safe to show).</exception>
    public static string CheckFolder(string path, bool mustExist)
    {
        if (!HostPaths.IsValidLocalFolder(path))
        {
            throw new HostPathException("Invalid folder: use a local path such as D:\\VMs.");
        }
        var normalized = HostPaths.Normalize(path);
        var full = HostPaths.Normalize(Path.GetFullPath(normalized));
        if (!full.Equals(normalized, StringComparison.OrdinalIgnoreCase))
        {
            throw new HostPathException("Invalid folder.");
        }
        if (HostPaths.IsBlocked(full))
        {
            throw new HostPathException("System folders cannot be used.");
        }
        DriveInfo drive;
        try
        {
            drive = new DriveInfo(full[..3]);
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
            {
                throw new HostPathException("Only local fixed drives can be used (no network, removable or optical drives).");
            }
        }
        catch (ArgumentException)
        {
            throw new HostPathException("Drive not found.");
        }
        var current = new DirectoryInfo(full);
        if (mustExist && !current.Exists)
        {
            throw new HostPathException("Folder not found.");
        }
        while (current is { Parent: not null })
        {
            if (current.Exists && (current.Attributes & Excluded) != 0)
            {
                throw new HostPathException("Hidden or system folders, junctions and symbolic links cannot be used.");
            }
            if (File.Exists(current.FullName))
            {
                throw new HostPathException("The path points to a file, not a folder.");
            }
            current = current.Parent;
        }
        return full;
    }
}

public sealed class HostPathException(string message) : Exception(message);
