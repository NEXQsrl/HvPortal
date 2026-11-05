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
/// </summary>
public static class HostFileService
{
    public const int MaxListEntries = 2000;
    public const int MaxConcurrentUploads = 4;
    private static readonly TimeSpan UploadIdle = TimeSpan.FromMinutes(10);
    private const FileAttributes Excluded = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint;
    private static readonly EnumerationOptions Options = new() { IgnoreInaccessible = true, AttributesToSkip = Excluded, ReturnSpecialDirectories = false };
    private static readonly ConcurrentDictionary<Guid, Upload> Uploads = new();

    /// <summary>Folder check (replaceable in tests, which cannot write outside the user profile).</summary>
    internal static Func<string, bool, string> CheckFolder { get; set; } = HostFolderBrowser.CheckFolder;

    private sealed class Upload : IDisposable
    {
        public required string FinalPath { get; init; }
        public required string TempPath { get; init; }
        public required long Size { get; init; }
        public required bool Overwrite { get; init; }
        public required FileStream Stream { get; init; }
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

    public static FolderListing List(FileListRequest request)
    {
        var path = CheckFolder(request.Path, true);
        var dir = new DirectoryInfo(path);
        var atRoot = dir.Parent is null;
        var folders = new List<FolderEntry>();
        var files = new List<FileEntry>();
        var truncated = false;
        foreach (var d in dir.EnumerateDirectories("*", Options))
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
            folders.Add(new FolderEntry(d.Name, d.FullName));
        }
        foreach (var f in dir.EnumerateFiles("*", Options))
        {
            if (files.Count >= MaxListEntries)
            {
                truncated = true;
                break;
            }
            files.Add(new FileEntry(f.Name, f.FullName, f.Length, f.LastWriteTimeUtc));
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
            Folders = folders.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            Files = files.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            FreeBytes = free,
            Truncated = truncated,
        };
    }

    private static FileInfo CheckFile(string path)
    {
        if (!HostPaths.IsValidFilePath(path))
        {
            throw new HostPathException("invalid_path: not a valid file path.");
        }
        var folder = CheckFolder(Path.GetDirectoryName(path)!, true);
        var file = new FileInfo(Path.Combine(folder, Path.GetFileName(path)));
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

    public static FileChunk Read(FileReadRequest request)
    {
        var file = CheckFile(request.Path);
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

    public static FileWriteBeginResult Begin(FileWriteBeginRequest request)
    {
        Sweep();
        if (Uploads.Count >= MaxConcurrentUploads)
        {
            throw new HostPathException("too_many_uploads: too many uploads in progress on this host.");
        }
        if (!HostPaths.IsValidFileName(request.Name))
        {
            throw new HostPathException("invalid_file_name: invalid file name.");
        }
        var folder = CheckFolder(request.Folder, true);
        var final = Path.Combine(folder, request.Name);
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
            if ((File.GetAttributes(final) & (Excluded | FileAttributes.ReadOnly)) != 0)
            {
                throw new HostPathException("location_not_allowed: read-only, hidden, system or linked files cannot be replaced.");
            }
        }
        if (new DriveInfo(folder[..3]).AvailableFreeSpace < request.SizeBytes)
        {
            throw new HostPathException("insufficient_space: not enough free space on the drive.");
        }
        var id = Guid.NewGuid();
        var temp = Path.Combine(folder, $".{request.Name}.{id:N}.hvpart");
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
        Uploads[id] = new Upload { FinalPath = final, TempPath = temp, Size = request.SizeBytes, Overwrite = request.Overwrite, Stream = stream };
        return new FileWriteBeginResult(id, final);
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
            if (File.Exists(u.FinalPath) && !u.Overwrite)
            {
                throw new HostPathException("file_exists: the file was created meanwhile.");
            }
            File.Move(u.TempPath, u.FinalPath, u.Overwrite);
            File.SetAttributes(u.FinalPath, FileAttributes.Archive);
            return new FileWriteResult(u.FinalPath, u.Size, hash);
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
            if (File.Exists(u.TempPath))
            {
                File.SetAttributes(u.TempPath, FileAttributes.Normal);
                File.Delete(u.TempPath);
            }
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
