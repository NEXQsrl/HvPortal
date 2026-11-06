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
