using System.Security.Cryptography;
using HvPortal.Agent.Commands;
using HvPortal.Agent.HyperV;
using HvPortal.Contracts.Agent;
using HvPortal.Contracts.HyperV;

namespace HvPortal.Agent.Tests;

/// <summary>Host file explorer in the agent: upload state machine, verified commit, read chunks, in-use files, names.</summary>
public sealed class HostFileServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hvp-files-" + Guid.NewGuid().ToString("N"));

    public HostFileServiceTests()
    {
        Directory.CreateDirectory(_root);
        // The real rule refuses C:\Users (where the temp folder is): these tests check the transfer logic in a temp folder.
        HostFileService.CheckFolder = (p, mustExist, _) => Directory.Exists(p) || !mustExist ? Path.GetFullPath(p) : throw new HostPathException("not_found: folder");
    }

    public void Dispose()
    {
        HostFileService.CheckFolder = (p, mustExist, fs) => HostFolderBrowser.CheckFolder(p, mustExist, fs);
        Directory.Delete(_root, recursive: true);
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    [Fact]
    public async Task Upload_appears_only_after_a_verified_commit_and_does_not_overwrite_by_default()
    {
        var data = RandomNumberGenerator.GetBytes(FileTransfer.ChunkBytes + 100);
        var begin = HostFileService.Begin(new FileWriteBeginRequest(_root, "disk.iso", data.Length, false));
        var final = Path.Combine(_root, "disk.iso");
        Assert.False(File.Exists(final));
        Assert.DoesNotContain(HostFileService.List(new FileListRequest(_root)).Files, f => f.Name.Contains("hvpart"));
        await HostFileService.ChunkAsync(new FileWriteChunkRequest(begin.UploadId, 0, Convert.ToBase64String(data, 0, FileTransfer.ChunkBytes)), CancellationToken.None);
        // A retried chunk is ignored; a gap is refused.
        await HostFileService.ChunkAsync(new FileWriteChunkRequest(begin.UploadId, 0, Convert.ToBase64String(data, 0, FileTransfer.ChunkBytes)), CancellationToken.None);
        await Assert.ThrowsAsync<HostPathException>(() => HostFileService.ChunkAsync(new FileWriteChunkRequest(begin.UploadId, FileTransfer.ChunkBytes + 50, "AAAA"), CancellationToken.None));
        await HostFileService.ChunkAsync(new FileWriteChunkRequest(begin.UploadId, FileTransfer.ChunkBytes, Convert.ToBase64String(data, FileTransfer.ChunkBytes, 100)), CancellationToken.None);
        var result = HostFileService.Commit(new FileWriteCommitRequest(begin.UploadId, Sha(data)));
        Assert.Equal(data, File.ReadAllBytes(final));
        Assert.Equal(Sha(data), result.Sha256);
        Assert.Equal(FileAttributes.Archive, File.GetAttributes(final) & (FileAttributes.Hidden | FileAttributes.Archive));
        Assert.Single(Directory.GetFiles(_root));

        var ex = Assert.Throws<HostPathException>(() => HostFileService.Begin(new FileWriteBeginRequest(_root, "disk.iso", 1, false)));
        Assert.StartsWith("file_exists", ex.Message);

        // Wrong hash: the existing file is not replaced and the temporary file is removed.
        var b2 = HostFileService.Begin(new FileWriteBeginRequest(_root, "disk.iso", 3, true));
        await HostFileService.ChunkAsync(new FileWriteChunkRequest(b2.UploadId, 0, Convert.ToBase64String(new byte[] { 1, 2, 3 })), CancellationToken.None);
        Assert.StartsWith("hash_mismatch", Assert.Throws<HostPathException>(() => HostFileService.Commit(new FileWriteCommitRequest(b2.UploadId, new string('0', 64)))).Message);
        Assert.Equal(data, File.ReadAllBytes(final));
        Assert.Single(Directory.GetFiles(_root));

        // Abort removes the temporary file.
        var b3 = HostFileService.Begin(new FileWriteBeginRequest(_root, "other.iso", 10, false));
        HostFileService.Abort(new FileWriteAbortRequest(b3.UploadId));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public void Read_returns_chunks_and_refuses_files_open_for_writing()
    {
        var path = Path.Combine(_root, "a.bin");
        var data = RandomNumberGenerator.GetBytes(1000);
        File.WriteAllBytes(path, data);
        var c1 = HostFileService.Read(new FileReadRequest(path, 0, 600));
        var c2 = HostFileService.Read(new FileReadRequest(path, 600, 600));
        Assert.False(c1.Eof);
        Assert.True(c2.Eof);
        Assert.Equal(data, Convert.FromBase64String(c1.Data).Concat(Convert.FromBase64String(c2.Data)).ToArray());
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            Assert.StartsWith("file_in_use", Assert.Throws<HostPathException>(() => HostFileService.Read(new FileReadRequest(path, 0, 10))).Message);
        }
        File.SetAttributes(path, FileAttributes.Hidden);
        Assert.StartsWith("location_not_allowed", Assert.Throws<HostPathException>(() => HostFileService.Read(new FileReadRequest(path, 0, 10))).Message);
        File.SetAttributes(path, FileAttributes.Normal);
    }

    [Theory]
    [InlineData("disk.iso", true)]
    [InlineData("Windows Server 2025 (EN).iso", true)]
    [InlineData("a:b", false)]
    [InlineData(@"..\x", false)]
    [InlineData("CON", false)]
    [InlineData("nul.txt", false)]
    [InlineData("file.", false)]
    [InlineData("x*?.iso", false)]
    public void File_names(string name, bool ok) => Assert.Equal(ok, HostPaths.IsValidFileName(name));

    // ------------------------------------------------------------------ 0.9.4: rename and delete (protocol 13)

    /// <summary>
    /// A folder for the rename / delete tests next to the test binaries: unlike the temp folder (under C:\Users, a
    /// system folder for the portal) it passes the real path rules. Null when the build output itself is under a system
    /// folder: those tests then have nowhere to run and return at once.
    /// </summary>
    private static string? WorkFolder()
    {
        var folder = Path.Combine(AppContext.BaseDirectory.TrimEnd('\\'), "fx-" + Guid.NewGuid().ToString("N")[..8]);
        if (!HostPaths.IsValidLocalFolder(folder) || HostPaths.IsBlocked(folder) || folder.Length > 150)
        {
            return null;
        }
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string Code(Action action) => Assert.Throws<HostPathException>(action).Message.Split(':')[0];

    /// <summary>
    /// A change that must succeed, tried again for a moment when the file is busy: on the development machine the
    /// antivirus opens files that were just written, and Windows then refuses to rename or delete them for an instant.
    /// </summary>
    private static T Eventually<T>(Func<T> change)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return change();
            }
            catch (HostPathException ex) when (attempt < 20 && (ex.Message.StartsWith("file_in_use", StringComparison.Ordinal)
                                                               || ex.Message.StartsWith("access_denied", StringComparison.Ordinal)
                                                               || ex.Message.StartsWith("folder_not_empty: the folder is not empty or is in use", StringComparison.Ordinal)))
            {
                Thread.Sleep(100);
            }
        }
    }

    [Fact]
    public void Rename_never_replaces_an_existing_name_and_never_touches_hyper_v_files()
    {
        if (WorkFolder() is not { } root)
        {
            return;
        }
        try
        {
            File.WriteAllText(Path.Combine(root, "notes.txt"), "a");
            File.WriteAllText(Path.Combine(root, "other.txt"), "b");
            File.WriteAllText(Path.Combine(root, "disk.vhdx"), "c");

            var renamed = Eventually(() => HostFileService.Rename(new RenamePathRequest(Path.Combine(root, "notes.txt"), "readme.txt")));
            Assert.False(renamed.IsFolder);
            Assert.Equal(Path.Combine(root, "readme.txt"), renamed.NewPath);
            Assert.True(File.Exists(Path.Combine(root, "readme.txt")));
            Assert.False(File.Exists(Path.Combine(root, "notes.txt")));

            Assert.Equal("file_exists", Code(() => HostFileService.Rename(new RenamePathRequest(Path.Combine(root, "readme.txt"), "other.txt"))));
            Assert.Equal("b", File.ReadAllText(Path.Combine(root, "other.txt")));
            Assert.Equal("not_found", Code(() => HostFileService.Rename(new RenamePathRequest(Path.Combine(root, "missing.txt"), "x.txt"))));

            // Hyper-V files: neither renamed, nor is anything renamed into one.
            Assert.Equal("protected_file", Code(() => HostFileService.Rename(new RenamePathRequest(Path.Combine(root, "disk.vhdx"), "disk.old"))));
            Assert.Equal("protected_file", Code(() => HostFileService.Rename(new RenamePathRequest(Path.Combine(root, "readme.txt"), "readme.avhdx"))));
            Assert.Equal("protected_file", Code(() => HostFileService.Delete(new DeletePathRequest(Path.Combine(root, "disk.vhdx")))));
            Assert.True(File.Exists(Path.Combine(root, "disk.vhdx")));

            // A file another process has open is not renamed.
            using (new FileStream(Path.Combine(root, "other.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.Equal("file_in_use", Code(() => HostFileService.Rename(new RenamePathRequest(Path.Combine(root, "other.txt"), "busy.txt"))));
            }
            // Hidden entries are not part of what the portal manages.
            File.SetAttributes(Path.Combine(root, "other.txt"), FileAttributes.Hidden);
            Assert.Equal("location_not_allowed", Code(() => HostFileService.Rename(new RenamePathRequest(Path.Combine(root, "other.txt"), "x.txt"))));
            Assert.Equal("location_not_allowed", Code(() => HostFileService.Delete(new DeletePathRequest(Path.Combine(root, "other.txt")))));
            File.SetAttributes(Path.Combine(root, "other.txt"), FileAttributes.Normal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_folder_is_renamed_only_when_nothing_inside_belongs_to_hyper_v_or_is_a_link()
    {
        if (WorkFolder() is not { } root)
        {
            return;
        }
        try
        {
            var plain = Directory.CreateDirectory(Path.Combine(root, "iso", "old")).Parent!.FullName;
            File.WriteAllText(Path.Combine(plain, "old", "a.iso"), "x");
            var renamed = Eventually(() => HostFileService.Rename(new RenamePathRequest(plain, "ISO library")));
            Assert.True(renamed.IsFolder);
            Assert.True(File.Exists(Path.Combine(root, "ISO library", "old", "a.iso")));

            var vm = Directory.CreateDirectory(Path.Combine(root, "vm1", "Virtual Hard Disks")).Parent!.FullName;
            File.WriteAllText(Path.Combine(vm, "Virtual Hard Disks", "vm1.vhdx"), "x");
            Assert.Equal("protected_content", Code(() => HostFileService.Rename(new RenamePathRequest(vm, "vm1-old"))));
            Assert.True(Directory.Exists(vm));
            File.Delete(Path.Combine(vm, "Virtual Hard Disks", "vm1.vhdx"));
            File.WriteAllText(Path.Combine(vm, "Virtual Hard Disks", "vm1.VMCX"), "x");
            Assert.Equal("protected_content", Code(() => HostFileService.Rename(new RenamePathRequest(vm, "vm1-old"))));

            // A junction inside the folder: what it points to is outside what was checked.
            var linked = Directory.CreateDirectory(Path.Combine(root, "linked")).FullName;
            try
            {
                Directory.CreateSymbolicLink(Path.Combine(linked, "elsewhere"), Path.Combine(root, "ISO library"));
                Assert.Equal("protected_content", Code(() => HostFileService.Rename(new RenamePathRequest(linked, "linked2"))));
                // The link itself is not something the portal renames or deletes.
                Assert.Equal("location_not_allowed", Code(() => HostFileService.Delete(new DeletePathRequest(Path.Combine(linked, "elsewhere")))));
                Assert.True(Directory.Exists(Path.Combine(root, "ISO library")));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Creating symbolic links needs a privilege this account does not have: that part cannot run here.
            }
        }
        finally
        {
            // The link first: a recursive delete must never follow it.
            var link = Path.Combine(root, "linked", "elsewhere");
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Delete_removes_a_file_or_an_empty_folder_and_nothing_else()
    {
        if (WorkFolder() is not { } root)
        {
            return;
        }
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(root, "logs")).FullName;
            File.WriteAllText(Path.Combine(folder, "a.log"), "x");
            File.WriteAllText(Path.Combine(folder, "ro.log"), "x");
            File.SetAttributes(Path.Combine(folder, "ro.log"), FileAttributes.ReadOnly);

            Assert.Equal("folder_not_empty", Code(() => HostFileService.Delete(new DeletePathRequest(folder))));
            Assert.True(File.Exists(Path.Combine(folder, "a.log")));

            using (new FileStream(Path.Combine(folder, "a.log"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.Equal("file_in_use", Code(() => HostFileService.Delete(new DeletePathRequest(Path.Combine(folder, "a.log")))));
            }
            var deleted = Eventually(() => HostFileService.Delete(new DeletePathRequest(Path.Combine(folder, "a.log"))));
            Assert.False(deleted.IsFolder);
            Assert.Null(deleted.NewPath);
            Assert.False(File.Exists(Path.Combine(folder, "a.log")));

            Assert.Equal("access_denied", Code(() => HostFileService.Delete(new DeletePathRequest(Path.Combine(folder, "ro.log")))));
            File.SetAttributes(Path.Combine(folder, "ro.log"), FileAttributes.Normal);
            Eventually(() => HostFileService.Delete(new DeletePathRequest(Path.Combine(folder, "ro.log"))));

            // A hidden file still makes the folder "not empty": nothing the user cannot see is removed with it.
            File.WriteAllText(Path.Combine(folder, "hidden.tmp"), "x");
            File.SetAttributes(Path.Combine(folder, "hidden.tmp"), FileAttributes.Hidden);
            Assert.Equal("folder_not_empty", Code(() => HostFileService.Delete(new DeletePathRequest(folder))));
            File.SetAttributes(Path.Combine(folder, "hidden.tmp"), FileAttributes.Normal);
            File.Delete(Path.Combine(folder, "hidden.tmp"));

            Assert.True(Eventually(() => HostFileService.Delete(new DeletePathRequest(folder))).IsFolder);
            Assert.False(Directory.Exists(folder));
            Assert.Equal("not_found", Code(() => HostFileService.Delete(new DeletePathRequest(folder))));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Rename_and_delete_need_protocol_13_and_are_validated_before_the_file_system_is_touched()
    {
        AgentCommand Op(HvOperation op) => new() { CommandId = Guid.NewGuid(), Kind = AgentCommandKind.HyperVOperation, Operation = op };
        Assert.Equal(13, AgentProtocol.Version);
        Assert.Equal("0.9.4", AgentProtocol.MinimumAgentFor(13));
        Assert.Equal("0.9.1", AgentProtocol.MinimumAgentFor(12));
        Assert.Equal(13, AgentProtocol.RequiredVersion(Op(HvOperation.RenamePath)));
        Assert.Equal(13, AgentProtocol.RequiredVersion(Op(HvOperation.DeletePath)));
        Assert.Equal(12, AgentProtocol.RequiredVersion(Op(HvOperation.MoveClusterSharedVolume)));
        Assert.True(CommandExecutor.IsMutating(Op(HvOperation.RenamePath)));
        Assert.True(CommandExecutor.IsMutating(Op(HvOperation.DeletePath)));

        string? Rename(string path, string name) => PowerShellOperationRunner.Validate(HvOperation.RenamePath, HvOperations.ToElement(new RenamePathRequest(path, name)));
        string? Delete(string path) => PowerShellOperationRunner.Validate(HvOperation.DeletePath, HvOperations.ToElement(new DeletePathRequest(path)));
        Assert.Null(Rename(@"D:\ISO\win.iso", "windows.iso"));
        Assert.Null(Delete(@"D:\ISO\old"));
        // Traversal, drives, system folders, streams, device names.
        Assert.Equal("invalid_path", Delete(@"D:\ISO\..\Windows"));
        Assert.Equal("invalid_path", Delete(@"\\server\share\x"));
        Assert.Equal("invalid_path", Delete(@"D:\ISO\a.txt:stream"));
        Assert.Equal("location_not_allowed", Delete(@"D:\"));
        Assert.Equal("location_not_allowed", Delete(@"C:\Windows\System32\drivers\etc\hosts"));
        Assert.Equal("location_not_allowed", Delete(@"C:\ProgramData"));
        Assert.Equal("location_not_allowed", Delete(@"D:\$RECYCLE.BIN"));
        Assert.Equal("location_not_allowed", Delete(@"D:\ISO\.hidden"));
        Assert.Equal("invalid_file_name", Rename(@"D:\ISO\win.iso", @"..\win.iso"));
        Assert.Equal("invalid_file_name", Rename(@"D:\ISO\win.iso", @"sub\win.iso"));
        Assert.Equal("invalid_file_name", Rename(@"D:\ISO\win.iso", "CON"));
        Assert.Equal("invalid_file_name", Rename(@"D:\ISO\win.iso", "$x"));
        Assert.Equal("same_name", Rename(@"D:\ISO\win.iso", "win.iso"));
        Assert.Equal("location_not_allowed", Rename(@"D:\data", "Windows"));
        Assert.Equal("path_too_long", Rename(@"D:\" + new string('a', 150) + @"\x.txt", new string('b', 60)));
        // Hyper-V files, whatever the case of the extension.
        foreach (var name in new[] { "vm.vhdx", "vm.VHDX", "vm.vhd", "vm_1234.avhdx", "vm.vmcx", "vm.vmrs", "vm.vmgs", "vm.vsv", "shared.vhds", "vm.hrl", "vm.vhdx.rct", "vm.vhdx.mrt" })
        {
            Assert.True(ProtectedHostFiles.IsProtected(name), name);
            Assert.Equal("protected_file", Delete(@"D:\VMs\" + name));
            Assert.Equal("protected_file", Rename(@"D:\VMs\" + name, "x.txt"));
            Assert.Equal("protected_file", Rename(@"D:\VMs\x.txt", name));
        }
        Assert.False(ProtectedHostFiles.IsProtected("win.iso"));
        Assert.False(ProtectedHostFiles.IsProtected("vhdx-notes.txt"));

        // A long refused input is refused at once (no backtracking in the path rules).
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal("invalid_path", Delete(@"D:\" + new string('a', 180) + "*" + new string('b', 5000)));
        Assert.Equal("invalid_file_name", Rename(@"D:\ISO\win.iso", new string('a', 199) + "|" + new string(' ', 5000)));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void File_operations_need_protocol_5_and_are_validated()
    {
        Assert.Equal(5, AgentProtocol.RequiredVersion(new AgentCommand { CommandId = Guid.NewGuid(), Kind = AgentCommandKind.HyperVOperation, Operation = HvOperation.ReadFile }));
        Assert.Equal("location_not_allowed", PowerShellOperationRunner.Validate(HvOperation.ReadFile, HvOperations.ToElement(new FileReadRequest(@"C:\Windows\System32\config\SAM", 0, 10))));
        Assert.Equal("invalid_range", PowerShellOperationRunner.Validate(HvOperation.ReadFile, HvOperations.ToElement(new FileReadRequest(@"D:\ISO\a.iso", 0, FileTransfer.ChunkBytes + 1))));
        Assert.Equal("location_not_allowed", PowerShellOperationRunner.Validate(HvOperation.WriteFileBegin, HvOperations.ToElement(new FileWriteBeginRequest(@"C:\Users\Public", "a.ps1", 1, false))));
        Assert.Equal("invalid_commit", PowerShellOperationRunner.Validate(HvOperation.WriteFileCommit, HvOperations.ToElement(new FileWriteCommitRequest(Guid.NewGuid(), "xyz"))));
        Assert.False(CommandExecutor.IsMutating(new AgentCommand { CommandId = Guid.NewGuid(), Kind = AgentCommandKind.HyperVOperation, Operation = HvOperation.WriteFileChunk }));
    }
}
