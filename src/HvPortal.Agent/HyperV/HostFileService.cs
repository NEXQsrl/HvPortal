using System.Collections.Concurrent;
using System.Security.Cryptography;
using HvPortal.Contracts.HyperV;

namespace HvPortal.Agent.HyperV;

/// <summary>
/// Host file explorer for the portal (protocol 5): list a folder, read a file in chunks, upload a file in chunks.
/// Same folder rules as the folder browser (local fixed drives, no hidden/system/reparse entries, no top-level system
/// folders). Reads use FileShare.Read, so files opened for writing by another process (e.g. the disk of a running VM)
/// cannot be downloaded — the portal never produces an inconsistent copy of an active VHDX.
/// Uploads go to a hidden temporary file next to the target and become visible only after size and SHA-256 match
/// (commit). Existing files are replaced only with explicit overwrite. Idle uploads are discarded after 10 minutes.
/// Paths in and out are host-local; for a remote host (management agent) <see cref="HostFileAccess"/> maps them to the
/// administrative shares and runs the I/O with the host credentials.
/// </summary>
public static class HostFileService
{
    public const int MaxListEntries = 2000;
    public const int MaxConcurrentUploads = 4;
    private static readonly TimeSpan UploadIdle = TimeSpan.FromMinutes(10);
    private const FileAttributes Excluded = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint;
    private static readonly EnumerationOptions Options = new() { IgnoreInaccessible = true, AttributesToSkip = Excluded, ReturnSpecialDirectories = false };
    private static readonly ConcurrentDictionary<Guid, Upload> Uploads = new();

    /// <summary>Folder check returning the host-local path (replaceable in tests, which cannot write outside the user profile).</summary>
    internal static Func<string, bool, HostFileAccess, string> CheckFolder { get; set; } = HostFolderBrowser.CheckFolder;

    private sealed class Upload : IDisposable
    {
        public required string FinalPath { get; init; }
        public required string TempPath { get; init; }
        public required long Size { get; init; }
        public required bool Overwrite { get; init; }
        public required FileStream Stream { get; init; }
        public required HostFileAccess Access { get; init; }
        public IncrementalHash Hash { get; } = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public long Written { get; set; }
        public DateTime LastActivityUtc { get; set; } = DateTime.UtcNow;
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public void Dispose()
        {
            Stream.Dispose();
            Hash.Dispose();
            Gate.Dispose();
        }
    }

    public static FolderListing List(FileListRequest request, HostFileAccess? access = null)
    {
        var fs = access ?? HostFileAccess.Local;
        var path = CheckFolder(request.Path, true, fs);
        return fs.Run(() => ListPhysical(path, fs));
    }

    private static FolderListing ListPhysical(string path, HostFileAccess fs)
    {
        var dir = new DirectoryInfo(fs.ToPhysical(path));
        var atRoot = dir.Parent is null;
        var top = atRoot || fs.IsStorageRoot(path);
        var folders = new List<FolderEntry>();
        var files = new List<FileEntry>();
        var truncated = false;
        foreach (var d in HostFolderBrowser.Subfolders(dir, fs))
        {
            if ((atRoot && HostPaths.IsBlockedRootFolder(d.Name)) || d.Name.StartsWith('$'))
            {
                continue;
            }
            if (folders.Count >= MaxListEntries)
            {
                truncated = true;
                break;
            }
            folders.Add(new FolderEntry(d.Name, fs.ToHost(d.FullName)));
        }
        foreach (var f in dir.EnumerateFiles("*", Options))
        {
            if (files.Count >= MaxListEntries)
            {
                truncated = true;
                break;
            }
            files.Add(new FileEntry(f.Name, fs.ToHost(f.FullName), f.Length, f.LastWriteTimeUtc));
        }
        return new FolderListing
        {
            Path = path,
            Parent = top ? null : fs.ToHost(dir.Parent!.FullName),
            Folders = folders.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            Files = files.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            FreeBytes = fs.FreeSpaceOf(path),
            Truncated = truncated,
        };
    }

    /// <summary>Call inside <see cref="HostFileAccess.Run{T}"/>.</summary>
    private static FileInfo CheckFile(string path, HostFileAccess fs)
    {
        if (!HostPaths.IsValidFilePath(path))
        {
            throw new HostPathException("invalid_path: not a valid file path.");
        }
        var folder = CheckFolder(Path.GetDirectoryName(path)!, true, fs);
        var file = new FileInfo(fs.ToPhysical(Path.Combine(folder, Path.GetFileName(path))));
        if (!file.Exists)
        {
            throw new HostPathException("not_found: file not found.");
        }
        if ((file.Attributes & Excluded) != 0)
        {
            throw new HostPathException("location_not_allowed: hidden, system or linked files cannot be transferred.");
        }
        return file;
    }

    public static FileChunk Read(FileReadRequest request, HostFileAccess? access = null)
    {
        var fs = access ?? HostFileAccess.Local;
        return fs.Run(() => ReadPhysical(request, fs));
    }

    private static FileChunk ReadPhysical(FileReadRequest request, HostFileAccess access)
    {
        var file = CheckFile(request.Path, access);
        try
        {
            using var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
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
        catch (IOException ex) when (ex.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021))
        {
            throw new HostPathException("file_in_use: the file is open for writing by another process (for example the disk of a running VM) and cannot be read.");
        }
    }

    public static FileWriteBeginResult Begin(FileWriteBeginRequest request, HostFileAccess? access = null)
    {
        var fs = access ?? HostFileAccess.Local;
        Sweep();
        if (Uploads.Count >= MaxConcurrentUploads)
        {
            throw new HostPathException("too_many_uploads: too many uploads in progress on this host.");
        }
        if (!HostPaths.IsValidFileName(request.Name))
        {
            throw new HostPathException("invalid_file_name: invalid file name.");
        }
        var folder = CheckFolder(request.Folder, true, fs);
        var hostFinal = Path.Combine(folder, request.Name);
        if (fs.FreeSpaceOf(folder) is { } free && free < request.SizeBytes)
        {
            throw new HostPathException("insufficient_space: not enough free space on the drive.");
        }
        var id = Guid.NewGuid();
        var upload = fs.Run(() => Open(request, fs.ToPhysical(hostFinal), fs.ToPhysical(Path.Combine(folder, $".{request.Name}.{id:N}.hvpart")), fs));
        Uploads[id] = upload;
        return new FileWriteBeginResult(id, hostFinal);
    }

    private static Upload Open(FileWriteBeginRequest request, string final, string temp, HostFileAccess fs)
    {
        if (Directory.Exists(final))
        {
            throw new HostPathException("file_exists: a folder with this name already exists.");
        }
        if (File.Exists(final))
        {
            if (!request.Overwrite)
            {
                throw new HostPathException("file_exists: the file already exists.");
            }
            // 0.9.4: the files the explorer never renames or deletes are not replaced by an upload either (a new
            // virtual disk can still be uploaded under a name that is free).
            if (ProtectedHostFiles.IsProtected(request.Name))
            {
                throw new HostPathException("protected_file: an existing virtual disk, checkpoint or VM configuration file is not replaced by an upload. Upload it with another name.");
            }
            if ((File.GetAttributes(final) & (Excluded | FileAttributes.ReadOnly)) != 0)
            {
                throw new HostPathException("location_not_allowed: read-only, hidden, system or linked files cannot be replaced.");
            }
        }
        FileStream stream;
        try
        {
            stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.SequentialScan);
            File.SetAttributes(temp, FileAttributes.Hidden);
        }
        catch (UnauthorizedAccessException)
        {
            throw new HostPathException("access_denied: the agent service account cannot write in this folder.");
        }
        return new Upload { FinalPath = final, TempPath = temp, Size = request.SizeBytes, Overwrite = request.Overwrite, Stream = stream, Access = fs };
    }

    public static async Task<long> ChunkAsync(FileWriteChunkRequest request, CancellationToken ct)
    {
        var u = Get(request.UploadId);
        var tooMuch = false;
        await u.Gate.WaitAsync(ct);
        try
        {
            var data = Convert.FromBase64String(request.Data);
            if (request.Offset < u.Written && request.Offset + data.Length == u.Written)
            {
                return u.Written; // retry of the last chunk
            }
            if (request.Offset != u.Written)
            {
                throw new HostPathException($"invalid_range: expected offset {u.Written}.");
            }
            if (u.Written + data.Length > u.Size)
            {
                tooMuch = true;
                throw new HostPathException("invalid_size: more data than announced.");
            }
            await u.Stream.WriteAsync(data, ct);
            u.Hash.AppendData(data);
            u.Written += data.Length;
            u.LastActivityUtc = DateTime.UtcNow;
            return u.Written;
        }
        finally
        {
            u.Gate.Release();
            if (tooMuch)
            {
                Discard(request.UploadId);
            }
        }
    }

    public static FileWriteResult Commit(FileWriteCommitRequest request)
    {
        var u = Get(request.UploadId);
        try
        {
            if (u.Written != u.Size)
            {
                throw new HostPathException($"invalid_size: received {u.Written} of {u.Size} bytes.");
            }
            var hash = Convert.ToHexString(u.Hash.GetHashAndReset()).ToLowerInvariant();
            if (!hash.Equals(request.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new HostPathException("hash_mismatch: the received data does not match the SHA-256 computed by the gateway.");
            }
            u.Stream.Flush(flushToDisk: true);
            u.Stream.Dispose();
            u.Access.Run(() =>
            {
                if (File.Exists(u.FinalPath) && (!u.Overwrite || ProtectedHostFiles.IsProtected(Path.GetFileName(u.FinalPath))))
                {
                    throw new HostPathException("file_exists: the file was created meanwhile.");
                }
                File.Move(u.TempPath, u.FinalPath, u.Overwrite);
                File.SetAttributes(u.FinalPath, FileAttributes.Archive);
            });
            return new FileWriteResult(u.Access.ToHost(u.FinalPath), u.Size, hash);
        }
        catch (IOException ex)
        {
            throw new HostPathException("file_in_use: cannot replace the file (" + ex.Message + ")");
        }
        finally
        {
            Discard(request.UploadId);
        }
    }

    public static void Abort(FileWriteAbortRequest request) => Discard(request.UploadId);

    /// <summary>
    /// Protocol 9: creates &lt;Parent&gt;\&lt;Name&gt;. The parent must be an acceptable folder (inside the allowed storage folders
    /// when the host restricts them); an existing file or folder with that name is never touched.
    /// </summary>
    public static FolderEntry NewFolder(NewFolderRequest request, HostFileAccess? access = null)
    {
        var fs = access ?? HostFileAccess.Local;
        if (OperationValidation.Validate(request) is { } invalid)
        {
            throw new HostPathException(invalid + ": invalid folder name or location.");
        }
        var parent = CheckFolder(request.Parent, true, fs);
        var hostPath = parent.Length == 3 ? parent + request.Name : parent + @"\" + request.Name;
        fs.CheckAllowed(hostPath);
        return fs.Run(() =>
        {
            var physical = fs.ToPhysical(hostPath);
            if (Directory.Exists(physical) || File.Exists(physical))
            {
                throw new HostPathException("file_exists: a file or folder with this name already exists.");
            }
            try
            {
                Directory.CreateDirectory(physical);
            }
            catch (UnauthorizedAccessException)
            {
                throw new HostPathException("access_denied: the agent service account cannot create folders here.");
            }
            return new FolderEntry(request.Name, hostPath);
        });
    }

    // ------------------------------------------------------------------ rename / delete (protocol 13)

    /// <summary>Entries looked at under a folder before it is renamed; a larger folder is refused (it cannot be checked).</summary>
    public const int MaxRenameScanEntries = 20000;

    private sealed record Entry(string HostPath, string Parent, string Name, string Physical, bool IsFolder);

    /// <summary>
    /// The file or folder a rename / delete is about, checked against the real file system: its parent is an acceptable
    /// folder (allowed storage, fixed drive, no link on the way), the entry exists, is not hidden, system or a link, is
    /// not an allowed storage folder itself nor the mount point of a cluster shared volume, and is not a Hyper-V file.
    /// Call inside <see cref="HostFileAccess.Run{T}"/>.
    /// </summary>
    private static Entry ResolveEntry(string path, HostFileAccess fs)
    {
        if (OperationValidation.ValidateEntryPath(path) is { } invalid)
        {
            throw new HostPathException(invalid + (invalid == "protected_file"
                ? ": virtual disks, checkpoints and VM configuration files belong to Hyper-V and cannot be renamed or deleted here."
                : ": this file or folder cannot be renamed or deleted."));
        }
        var normalized = HostPaths.Normalize(path);
        var cut = normalized.LastIndexOf('\\');
        var name = normalized[(cut + 1)..];
        var parent = CheckFolder(cut == 2 ? normalized[..3] : normalized[..cut], true, fs);
        var hostPath = parent.Length == 3 ? parent + name : parent + @"\" + name;
        fs.CheckAllowed(hostPath);
        if (fs.IsStorageRoot(hostPath))
        {
            throw new HostPathException("location_not_allowed: an allowed storage folder of the host cannot be renamed or deleted.");
        }
        var physical = fs.ToPhysical(hostPath);
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(physical);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new HostPathException("not_found: file or folder not found.");
        }
        if ((attributes & Excluded) != 0)
        {
            // Also the mount point of a cluster shared volume (a reparse point): never renamed or deleted from here.
            throw new HostPathException("location_not_allowed: hidden, system or linked files and folders cannot be renamed or deleted.");
        }
        return new Entry(hostPath, parent, name, physical, (attributes & FileAttributes.Directory) != 0);
    }

    private static HostPathException InUse(IOException ex) =>
        new("file_in_use: the file or folder is in use by another process (for example a running VM) or cannot be changed (" + ex.Message + ")");

    /// <summary>Sharing violation / lock violation: another process has the file open.</summary>
    private static bool IsSharingViolation(IOException ex) => ex.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021);

    /// <summary>
    /// Protocol 13: renames a file or a folder in place (same parent). Never over an existing name. A folder is renamed
    /// only when nothing below it is a Hyper-V file, a link or unreadable: the paths of registered VMs, checkpoints,
    /// templates and restore points must not change under Hyper-V.
    /// </summary>
    public static PathChangeResult Rename(RenamePathRequest request, HostFileAccess? access = null)
    {
        var fs = access ?? HostFileAccess.Local;
        if (OperationValidation.Validate(request) is { } invalid)
        {
            throw new HostPathException(invalid + ": invalid name or location.");
        }
        return fs.Run(() =>
        {
            var entry = ResolveEntry(request.Path, fs);
            var newHost = entry.Parent.Length == 3 ? entry.Parent + request.NewName : entry.Parent + @"\" + request.NewName;
            fs.CheckAllowed(newHost);
            var target = fs.ToPhysical(newHost);
            // A change of case only is the same entry; anything else must not exist yet.
            var caseOnly = string.Equals(entry.Name, request.NewName, StringComparison.OrdinalIgnoreCase);
            if (!caseOnly && (File.Exists(target) || Directory.Exists(target)))
            {
                throw new HostPathException("file_exists: a file or folder with this name already exists.");
            }
            try
            {
                if (entry.IsFolder)
                {
                    CheckFolderContent(entry.Physical);
                    Directory.Move(entry.Physical, target);
                }
                else
                {
                    File.Move(entry.Physical, target, overwrite: false);
                }
            }
            catch (UnauthorizedAccessException)
            {
                throw new HostPathException("access_denied: the agent service account cannot rename this (NTFS permissions, or a file inside is in use).");
            }
            catch (IOException ex) when (ex is not FileNotFoundException and not DirectoryNotFoundException)
            {
                throw InUse(ex);
            }
            return new PathChangeResult(entry.HostPath, newHost, entry.IsFolder);
        });
    }

    /// <summary>
    /// Everything below a folder that is about to be renamed: no Hyper-V file, no link, nothing unreadable, and not more
    /// than <see cref="MaxRenameScanEntries"/> entries (what cannot be checked is refused).
    /// </summary>
    private static void CheckFolderContent(string physical)
    {
        var all = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = 0, ReturnSpecialDirectories = false };
        var count = 0;
        try
        {
            foreach (var e in new DirectoryInfo(physical).EnumerateFileSystemInfos("*", all))
            {
                if (++count > MaxRenameScanEntries)
                {
                    throw new HostPathException($"folder_too_large: the folder holds more than {MaxRenameScanEntries} files and folders: it cannot be checked, so it is not renamed.");
                }
                if ((e.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new HostPathException("protected_content: the folder contains a junction or a symbolic link (" + e.Name + "): it is not renamed.");
                }
                if (ProtectedHostFiles.IsProtected(e.Name))
                {
                    throw new HostPathException("protected_content: the folder contains virtual disks, checkpoints or VM configuration files (" + e.Name
                        + "): renaming it would break the VM, template or restore point that uses them. Move the VM storage instead.");
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            throw new HostPathException("access_denied: part of the folder cannot be read by the agent service account, so it cannot be checked and is not renamed.");
        }
    }

    /// <summary>
    /// Protocol 13: deletes a file, or a folder that is empty (hidden and system entries count: Windows itself refuses
    /// to remove a folder that holds anything). Never recursive. Read-only files are not deleted.
    /// </summary>
    public static PathChangeResult Delete(DeletePathRequest request, HostFileAccess? access = null)
    {
        var fs = access ?? HostFileAccess.Local;
        if (OperationValidation.Validate(request) is { } invalid)
        {
            throw new HostPathException(invalid + ": this file or folder cannot be deleted.");
        }
        return fs.Run(() =>
        {
            var entry = ResolveEntry(request.Path, fs);
            try
            {
                if (entry.IsFolder)
                {
                    if (Directory.EnumerateFileSystemEntries(entry.Physical).Any())
                    {
                        throw new HostPathException("folder_not_empty: only empty folders can be deleted. Delete or move its content first.");
                    }
                    // Not recursive: when something appeared meanwhile, Windows refuses and nothing is lost.
                    Directory.Delete(entry.Physical, recursive: false);
                }
                else
                {
                    if ((File.GetAttributes(entry.Physical) & FileAttributes.ReadOnly) != 0)
                    {
                        throw new HostPathException("access_denied: the file is read-only: it is not deleted.");
                    }
                    File.Delete(entry.Physical);
                }
            }
            catch (UnauthorizedAccessException)
            {
                throw new HostPathException("access_denied: the agent service account cannot delete this (NTFS permissions).");
            }
            catch (IOException ex) when (ex is not FileNotFoundException and not DirectoryNotFoundException)
            {
                throw entry.IsFolder && !IsSharingViolation(ex)
                    ? new HostPathException("folder_not_empty: the folder is not empty or is in use (" + ex.Message + ")")
                    : InUse(ex);
            }
            return new PathChangeResult(entry.HostPath, null, entry.IsFolder);
        });
    }

    private static Upload Get(Guid id) =>
        Uploads.TryGetValue(id, out var u) ? u : throw new HostPathException("upload_not_found: unknown or expired upload.");

    private static void Discard(Guid id)
    {
        if (!Uploads.TryRemove(id, out var u))
        {
            return;
        }
        u.Dispose();
        try
        {
            u.Access.Run(() =>
            {
                if (File.Exists(u.TempPath))
                {
                    File.SetAttributes(u.TempPath, FileAttributes.Normal);
                    File.Delete(u.TempPath);
                }
            });
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void Sweep()
    {
        foreach (var (id, u) in Uploads)
        {
            if (DateTime.UtcNow - u.LastActivityUtc > UploadIdle)
            {
                Discard(id);
            }
        }
    }
}
