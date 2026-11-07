using System.Collections.Concurrent;
using System.Text.Json;
using HvPortal.Contracts.HyperV;

namespace HvPortal.Agent.HyperV;

/// <summary>
/// Disks of restore points attached read-only to this host for file-level restore (MountBackupDisk, protocol 9).
/// Only the volumes of a mount registered here can be browsed (ListBackupFiles / ReadBackupFile): the gateway names a
/// mount id, never a disk or a volume. The list is also written to &lt;agent data&gt;\backup-mounts.json so that disks left
/// attached by a stopped agent are detached when it starts again. A mount not used for <see cref="IdleLimit"/> is
/// detached by <see cref="BackupMountJanitor"/>.
/// </summary>
public static class BackupMounts
{
    public static readonly TimeSpan IdleLimit = TimeSpan.FromHours(2);
    public const int MaxMounts = 4;

    public sealed record Entry
    {
        public Guid MountId { get; init; }
        /// <summary>Local path of the attached disk file (the restore point copy, or a staged copy of it).</summary>
        public required string DiskPath { get; init; }
        /// <summary>DiskPath is a temporary copy made by the agent: deleted after the disk is detached.</summary>
        public bool Staged { get; init; }
        public IReadOnlyList<MountedVolume> Volumes { get; init; } = [];
        public DateTimeOffset CreatedUtc { get; init; }
        public DateTimeOffset LastUsedUtc { get; set; }
    }

    private static readonly ConcurrentDictionary<Guid, Entry> Entries = new();
    private static readonly object FileGate = new();
    private static string? _file;

    public static void Configure(string dataDirectory) => _file = Path.Combine(dataDirectory, "backup-mounts.json");

    public static int Count => Entries.Count;

    public static Entry Register(string diskPath, bool staged, IReadOnlyList<MountedVolume> volumes)
    {
        var now = DateTimeOffset.UtcNow;
        var e = new Entry { MountId = Guid.NewGuid(), DiskPath = diskPath, Staged = staged, Volumes = volumes, CreatedUtc = now, LastUsedUtc = now };
        Entries[e.MountId] = e;
        Persist();
        return e;
    }

    /// <exception cref="HostPathException">Unknown or already detached mount.</exception>
    public static Entry Get(Guid mountId)
    {
        if (!Entries.TryGetValue(mountId, out var e))
        {
            throw new HostPathException("not_found: the backup disk is no longer attached (it is detached after 2 hours without use). Open it again.");
        }
        e.LastUsedUtc = DateTimeOffset.UtcNow;
        return e;
    }

    public static Entry? Remove(Guid mountId)
    {
        if (Entries.TryRemove(mountId, out var e))
        {
            Persist();
            return e;
        }
        return null;
    }

    public static IReadOnlyList<Entry> Idle(DateTimeOffset now) => Entries.Values.Where(e => now - e.LastUsedUtc > IdleLimit).ToList();

    /// <summary>Mounts recorded by a previous run of the agent (to detach at start).</summary>
    public static IReadOnlyList<Entry> Leftovers()
    {
        if (_file is null || !File.Exists(_file))
        {
            return [];
        }
        try
        {
            return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_file)) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static void ClearLeftovers()
    {
        if (Entries.IsEmpty)
        {
            Persist();
        }
    }

    private static void Persist()
    {
        if (_file is null)
        {
            return;
        }
        lock (FileGate)
        {
            try
            {
                File.WriteAllText(_file, JsonSerializer.Serialize(Entries.Values.ToList()));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: the janitor still detaches idle disks while the agent runs.
            }
        }
    }

    /// <summary>For tests.</summary>
    internal static void Reset()
    {
        Entries.Clear();
        _file = null;
    }

    /// <summary>The volume root that contains <paramref name="path"/>, or an error.</summary>
    internal static string RootOf(Entry e, string path)
    {
        var p = HostPaths.Normalize(path);
        foreach (var v in e.Volumes)
        {
            var root = HostPaths.Normalize(v.Root);
            if (p.Equals(root, StringComparison.OrdinalIgnoreCase) || p.StartsWith(root.TrimEnd('\\') + @"\", StringComparison.OrdinalIgnoreCase))
            {
                return root;
            }
        }
        throw new HostPathException("location_not_allowed: the path is not inside the attached backup disk.");
    }
}

/// <summary>
/// Read-only browsing of the volumes of an attached restore point disk. The guest's file permissions apply: a file the
/// agent account may not read is reported as such (restore the disk to a VM to copy it with the guest's own accounts).
/// Junctions and symbolic links are neither followed nor listed.
/// </summary>
public static class BackupFileService
{
    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false,
    };

    public static FolderListing List(BackupFileListRequest request)
    {
        var e = BackupMounts.Get(request.MountId);
        var path = HostPaths.Normalize(request.Path);
        var root = BackupMounts.RootOf(e, path);
        var dir = new DirectoryInfo(path);
        CheckNoLinks(dir, root);
        if (!dir.Exists)
        {
            throw new HostPathException("not_found: folder not found.");
        }
        var folders = new List<FolderEntry>();
        var files = new List<FileEntry>();
        var truncated = false;
        try
        {
            foreach (var d in dir.EnumerateDirectories("*", Options))
            {
                if (folders.Count >= HostFileService.MaxListEntries)
                {
                    truncated = true;
                    break;
                }
                folders.Add(new FolderEntry(d.Name, d.FullName));
            }
            foreach (var f in dir.EnumerateFiles("*", Options))
            {
                if (files.Count >= HostFileService.MaxListEntries)
                {
                    truncated = true;
                    break;
                }
                files.Add(new FileEntry(f.Name, f.FullName, f.Length, f.LastWriteTimeUtc));
            }
        }
        catch (UnauthorizedAccessException)
        {
            throw new HostPathException("access_denied: the permissions of this folder inside the backup do not allow the agent account to read it. Restore the disk to a VM to copy it with the guest's accounts.");
        }
        return new FolderListing
        {
            Path = path,
            Parent = path.Equals(root, StringComparison.OrdinalIgnoreCase) ? null : dir.Parent?.FullName,
            Folders = folders.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            Files = files.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            Truncated = truncated,
        };
    }

    public static FileChunk Read(BackupFileReadRequest request)
    {
        var e = BackupMounts.Get(request.MountId);
        var root = BackupMounts.RootOf(e, request.Path);
        var file = new FileInfo(request.Path);
        CheckNoLinks(file.Directory!, root);
        if (!file.Exists)
        {
            throw new HostPathException("not_found: file not found.");
        }
        if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new HostPathException("location_not_allowed: linked files cannot be transferred.");
        }
        try
        {
            using var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan);
            if (request.Offset > fs.Length)
            {
                throw new HostPathException("invalid_range: offset beyond the end of the file.");
            }
            fs.Seek(request.Offset, SeekOrigin.Begin);
            var buffer = new byte[(int)Math.Min(request.Length, fs.Length - request.Offset)];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = fs.Read(buffer, read, buffer.Length - read);
                if (n == 0)
                {
                    break;
                }
                read += n;
            }
            return new FileChunk(request.Offset, fs.Length, file.LastWriteTimeUtc, Convert.ToBase64String(buffer, 0, read), request.Offset + read >= fs.Length);
        }
        catch (UnauthorizedAccessException)
        {
            throw new HostPathException("access_denied: the permissions of this file inside the backup do not allow the agent account to read it. Restore the disk to a VM to copy it with the guest's accounts.");
        }
    }

    private static void CheckNoLinks(DirectoryInfo dir, string root)
    {
        var current = dir;
        var top = HostPaths.Normalize(root).TrimEnd('\\');
        while (current is not null && !current.FullName.TrimEnd('\\').Equals(top, StringComparison.OrdinalIgnoreCase))
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new HostPathException("location_not_allowed: junctions and symbolic links are not followed.");
            }
            current = current.Parent;
        }
    }
}
