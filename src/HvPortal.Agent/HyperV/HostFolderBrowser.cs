using HvPortal.Contracts.HyperV;

namespace HvPortal.Agent.HyperV;

/// <summary>
/// Read-only folder browser for the portal's VM-location and ISO pickers (operation BrowseFolders), and the file-system
/// checks applied to a requested VM location. Only local fixed drives of the host; hidden, system and reparse-point
/// folders (junctions, symbolic links, mount points) are neither listed nor accepted; top-level system folders are
/// excluded (HostPaths.BlockedRootFolders). Nothing is created or modified. Paths in and out are host-local; for a
/// remote host (management agent) <see cref="HostFileAccess"/> maps them to the administrative shares.
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

    public static FolderListing Browse(FolderBrowseRequest request, HostFileAccess? access = null)
    {
        var fs = access ?? HostFileAccess.Local;
        if (request.Path is null)
        {
            return new FolderListing { Drives = TopLevel(fs) };
        }
        var path = CheckFolder(request.Path, mustExist: true, fs);
        return fs.Run(() =>
        {
            var dir = new DirectoryInfo(fs.ToPhysical(path));
            var atRoot = dir.Parent is null;
            var top = atRoot || fs.IsStorageRoot(path);
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
                folders.Add(new FolderEntry(d.Name, fs.ToHost(d.FullName)));
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
                    isos.Add(new IsoFile(fs.ToHost(f.FullName), f.Length, f.LastWriteTimeUtc));
                }
            }
            return new FolderListing
            {
                Path = path,
                // An allowed storage folder is the top of what the portal sees: no way up from there.
                Parent = top ? null : fs.ToHost(dir.Parent!.FullName),
                Folders = folders.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
                Isos = isos.OrderBy(f => f.Path, StringComparer.CurrentCultureIgnoreCase).ToList(),
                FreeBytes = fs.FreeSpace(path[..3]),
                Truncated = truncated,
            };
        });
    }

    public static IReadOnlyList<DriveEntry> FixedDrives() => HostFileAccess.Local.FixedDrives();

    /// <summary>
    /// What the portal sees at the top: the local fixed drives, or only the allowed storage folders (each with the size and
    /// free space of its drive) when the host restricts them. Allowed folders that do not exist (yet) are left out.
    /// </summary>
    public static IReadOnlyList<DriveEntry> TopLevel(HostFileAccess fs)
    {
        if (fs.StorageError is { } error)
        {
            throw new HostPathException("location_not_allowed: " + error);
        }
        var drives = fs.FixedDrives();
        if (fs.StorageFolders.Count == 0)
        {
            return drives;
        }
        var list = new List<DriveEntry>();
        foreach (var folder in fs.StorageFolders)
        {
            var drive = drives.FirstOrDefault(d => d.Path.Equals(folder[..3], StringComparison.OrdinalIgnoreCase));
            if (drive is null)
            {
                continue;
            }
            var exists = fs.Run(() => Directory.Exists(fs.ToPhysical(folder)));
            if (exists)
            {
                list.Add(new DriveEntry(folder, folder.Length > 3 ? Path.GetFileName(folder.TrimEnd('\\')) : drive.Label, drive.TotalBytes, drive.FreeBytes));
            }
        }
        return list;
    }

    /// <summary>
    /// Validates a host folder against the real file system and returns its normalized host-local path.
    /// mustExist = false (VM location): the missing tail is created later by the operations script; every existing
    /// ancestor is still checked.
    /// </summary>
    /// <exception cref="HostPathException">The folder is not acceptable (message is safe to show).</exception>
    public static string CheckFolder(string path, bool mustExist, HostFileAccess? access = null)
    {
        var fs = access ?? HostFileAccess.Local;
        if (!HostPaths.IsValidLocalFolder(path))
        {
            throw new HostPathException("Invalid folder: use a local path such as D:\\VMs.");
        }
        var normalized = HostPaths.Normalize(path);
        // Path.GetFullPath on the host-local form only normalizes text (no I/O): it must not change an accepted path.
        var full = HostPaths.Normalize(Path.GetFullPath(normalized));
        if (!full.Equals(normalized, StringComparison.OrdinalIgnoreCase))
        {
            throw new HostPathException("Invalid folder.");
        }
        if (HostPaths.IsBlocked(full))
        {
            throw new HostPathException("System folders cannot be used.");
        }
        fs.CheckAllowed(full);
        if (!fs.IsFixedDrive(full[..3]))
        {
            throw new HostPathException("Only local fixed drives can be used (no network, removable or optical drives).");
        }
        fs.Run(() =>
        {
            var current = new DirectoryInfo(fs.ToPhysical(full));
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
        });
        return full;
    }
}

public sealed class HostPathException(string message) : Exception(message);
