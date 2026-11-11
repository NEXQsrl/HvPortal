using System.Diagnostics;
using HvPortal.Agent.Channel;
using HvPortal.Agent.Commands;
using HvPortal.Agent.HyperV;
using HvPortal.Contracts.Agent;
using HvPortal.Contracts.HyperV;

namespace HvPortal.Agent.Tests;

/// <summary>
/// Protocol 9 (v0.7.0): which commands need it, the shared validation of VLANs, moves, folders, repositories, backups and
/// restores, the allowed storage folders of a host, the agent update request and the rules of the operations script.
/// </summary>
public sealed class Protocol9Tests
{
    private static string Script()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Ops", "HvPortal.Ops.ps1"));
        return string.Join('\n', System.Text.RegularExpressions.Regex.Replace(source, "(?s)<#.*?#>", "").Split('\n').Where(l => !l.TrimStart().StartsWith('#')));
    }

    private static string Branch(string script, string op)
    {
        var start = script.IndexOf($"'{op}' {{", StringComparison.Ordinal);
        Assert.True(start >= 0, op);
        var next = System.Text.RegularExpressions.Regex.Match(script[(start + 10)..], @"\n        '[A-Za-z]+' \{");
        return next.Success ? script.Substring(start, next.Index + 10) : script[start..];
    }

    private static AgentCommand Op(HvOperation op, object? args = null) => new()
    {
        CommandId = Guid.NewGuid(), Kind = AgentCommandKind.HyperVOperation, Operation = op, OperationArgs = HvOperations.ToElement(args ?? new { }),
    };

    private static BackupTarget Share(bool credentials = false) => credentials
        ? new BackupTarget { Path = @"\\nas01\backups\hv", UserName = @"NAS01\backup", Password = "s3cret!", StagingPath = @"D:\Staging" }
        : new BackupTarget { Path = @"\\nas01\backups\hv" };

    private static string PointFolder(Guid id) => @"web01_1a2b3c4d\" + id.ToString("D");

    [Fact]
    public void Protocol_9_is_required_for_every_new_operation_for_vlans_and_for_agent_updates()
    {
        Assert.Equal(13, AgentProtocol.Version);
        foreach (var op in Enum.GetValues<HvOperation>().Where(o => o >= HvOperation.GetHostHealth && o < HvOperation.GetVmMetrics))
        {
            Assert.Equal(9, AgentProtocol.RequiredVersion(Op(op)));
        }
        Assert.Equal(9, AgentProtocol.RequiredVersion(new AgentCommand { CommandId = Guid.NewGuid(), Kind = AgentCommandKind.UpdateAgent }));
        // An older agent would create the VM untagged, on the wrong network: never sent to it.
        Assert.Equal(9, AgentProtocol.RequiredVersion(Op(HvOperation.NewVm, new NewVmRequest { Name = "web", SwitchName = "vSwitch", VlanId = 20 })));
        Assert.True(AgentProtocol.RequiredVersion(Op(HvOperation.NewVm, new NewVmRequest { Name = "web", SwitchName = "vSwitch" })) < 9);
        Assert.Equal(9, AgentProtocol.RequiredVersion(Op(HvOperation.SetVmConfig, new VmConfigChange { VmId = Guid.NewGuid(), AddNetworkAdapterSwitch = "vSwitch", AddNetworkAdapterVlanId = 20 })));
        Assert.True(AgentProtocol.RequiredVersion(Op(HvOperation.SetVmConfig, new VmConfigChange { VmId = Guid.NewGuid(), AddNetworkAdapterSwitch = "vSwitch" })) < 9);
        Assert.Equal("0.7.0", AgentProtocol.MinimumAgentFor(9));
        Assert.Equal("0.6.0", AgentProtocol.MinimumAgentFor(8));
        // Long copies get the long timeout; host health does not.
        Assert.Equal(TimeSpan.FromHours(12), CommandExecutor.TimeoutFor(Op(HvOperation.BackupVm) with { TimeoutSeconds = 100 * 3600 }, HvOperation.BackupVm));
        Assert.Equal(TimeSpan.FromMinutes(30), CommandExecutor.TimeoutFor(Op(HvOperation.GetHostHealth) with { TimeoutSeconds = 100 * 3600 }, HvOperation.GetHostHealth));
        Assert.True(CommandExecutor.IsMutating(Op(HvOperation.BackupVm)));
        Assert.True(CommandExecutor.IsMutating(Op(HvOperation.MoveVm)));
        Assert.False(CommandExecutor.IsMutating(Op(HvOperation.GetHostHealth)));
        Assert.False(CommandExecutor.IsMutating(Op(HvOperation.ListBackupFiles)));
    }

    [Fact]
    public void Deleting_the_disks_of_a_vm_needs_protocol_9_and_never_touches_files_of_other_vms()
    {
        var delete = new AgentCommand { CommandId = Guid.NewGuid(), Kind = AgentCommandKind.DeleteVm, VmId = Guid.NewGuid() };
        Assert.True(AgentProtocol.RequiredVersion(delete) < 9);
        Assert.Equal(9, AgentProtocol.RequiredVersion(delete with { DeleteDisks = true }));
        Assert.True(CommandExecutor.IsMutating(delete with { DeleteDisks = true }));
        Assert.Null(PowerShellOperationRunner.Validate(HvOperation.RemoveVmWithDisks, HvOperations.ToElement(new VmRef(Guid.NewGuid()))));
        Assert.Equal("invalid_vm", PowerShellOperationRunner.Validate(HvOperation.RemoveVmWithDisks, HvOperations.ToElement(new VmRef(Guid.Empty))));

        var branch = Branch(Script(), "RemoveVmWithDisks").Replace("\r", "");
        // Every check happens before Remove-VM; only then files are deleted, one by one, never recursively.
        var removeVm = branch.IndexOf("Remove-VM -VM $vm -Force", StringComparison.Ordinal);
        Assert.True(removeVm > 0);
        foreach (var check in new[] { "-notin @('Off', 'Saved')", "Get-VMSnapshot -VM $vm", "$inUse.Contains($d)", "Assert-StorageFolder", @"-notmatch '\.(vhd|vhdx|avhdx)$'" })
        {
            var at = branch.IndexOf(check, StringComparison.Ordinal);
            Assert.True(at > 0 && at < removeVm, check);
        }
        Assert.Contains("ParentPath", branch, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath $d -Force -ErrorAction Stop", branch, StringComparison.Ordinal);
        Assert.DoesNotContain("-Recurse", branch, StringComparison.Ordinal);
    }

    [Fact]
    public void Vlans_are_1_to_4094_and_need_a_switch()
    {
        Assert.Null(OperationValidation.Validate(new NewVmRequest { Name = "web", SwitchName = "vSwitch", VlanId = 4094 }));
        Assert.Equal("invalid_vlan", OperationValidation.Validate(new NewVmRequest { Name = "web", SwitchName = "vSwitch", VlanId = 0 }));
        Assert.Equal("invalid_vlan", OperationValidation.Validate(new NewVmRequest { Name = "web", SwitchName = "vSwitch", VlanId = 4095 }));
        Assert.Equal("invalid_vlan", OperationValidation.Validate(new NewVmRequest { Name = "web", VlanId = 10 }));
        var vm = Guid.NewGuid();
        Assert.Null(OperationValidation.Validate(new VmConfigChange { VmId = vm, AddNetworkAdapterSwitch = "vSwitch", AddNetworkAdapterVlanId = 1 }));
        Assert.Equal("invalid_vlan", OperationValidation.Validate(new VmConfigChange { VmId = vm, AddNetworkAdapterVlanId = 10 }));
        Assert.Equal("invalid_vlan", OperationValidation.Validate(new VmConfigChange { VmId = vm, AddNetworkAdapterSwitch = "vSwitch", AddNetworkAdapterVlanId = -1 }));
    }

    [Fact]
    public void Share_paths_are_checked_without_backtracking_and_hidden_shares_are_refused()
    {
        foreach (var ok in new[] { @"\\nas01\backups", @"\\nas01.contoso.local\backups\hyper-v", @"\\10.0.0.5\bk\a b\c" })
        {
            Assert.True(OperationValidation.IsValidSharePath(ok), ok);
            Assert.True(OperationValidation.IsValidRepositoryPath(ok), ok);
        }
        foreach (var bad in new[] { @"\\nas01", @"\\nas01\c$", @"\\nas01\ADMIN$\x", @"\\nas01\bk\..\x", @"\\nas01\bk\x.", @"\\-bad-\bk", @"\\nas01\bk\a*b", @"\\localhost\bk", @"\\nas01\\bk" })
        {
            Assert.False(OperationValidation.IsValidSharePath(bad), bad);
        }
        Assert.True(OperationValidation.IsValidRepositoryPath(@"D:\Backups"));
        Assert.False(OperationValidation.IsValidRepositoryPath(@"D:\"));
        Assert.False(OperationValidation.IsValidRepositoryPath(@"C:\Windows\Backups"));
        // A long refused input is answered at once (the pattern must not backtrack).
        var hostile = @"\\nas01\" + string.Join(@"\", Enumerable.Repeat("aaaaaaaaaaaaaaaa", 40)) + "\u0001";
        var clock = Stopwatch.StartNew();
        Assert.False(OperationValidation.IsValidSharePath(hostile));
        Assert.True(clock.ElapsedMilliseconds < 500, $"took {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Repository_credentials_are_only_for_shares_need_a_staging_folder_and_never_appear_in_text()
    {
        Assert.Null(OperationValidation.Validate(Share()));
        Assert.Null(OperationValidation.Validate(Share(credentials: true)));
        Assert.Null(OperationValidation.Validate(new BackupTarget { Path = @"D:\Backups", StagingPath = @"E:\Staging" }));
        Assert.Equal("credentials_only_for_shares", OperationValidation.Validate(new BackupTarget { Path = @"D:\Backups", UserName = "u", Password = "p" }));
        Assert.Equal("staging_required", OperationValidation.Validate(new BackupTarget { Path = @"\\nas01\bk", UserName = "u", Password = "p" }));
        Assert.Equal("invalid_repository_credentials", OperationValidation.Validate(new BackupTarget { Path = @"\\nas01\bk", UserName = "u", StagingPath = @"D:\S" }));
        Assert.Equal("invalid_staging_path", OperationValidation.Validate(new BackupTarget { Path = @"\\nas01\bk", StagingPath = @"C:\Windows\Temp" }));
        Assert.Equal("invalid_staging_path", OperationValidation.Validate(new BackupTarget { Path = @"\\nas01\bk", StagingPath = @"D:\" }));
        Assert.DoesNotContain("s3cret", Share(credentials: true).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Backup_and_restore_requests_name_one_restore_point_and_restore_tests_are_isolated()
    {
        var point = Guid.NewGuid();
        var vm = Guid.NewGuid();
        Assert.Null(OperationValidation.Validate(new BackupVmRequest { VmId = vm, Target = Share(), Folder = PointFolder(point), PointId = point }));
        Assert.Equal("invalid_backup_folder", OperationValidation.Validate(new BackupVmRequest { VmId = vm, Target = Share(), Folder = PointFolder(Guid.NewGuid()), PointId = point }));
        Assert.Equal("invalid_backup_folder", OperationValidation.Validate(new BackupVmRequest { VmId = vm, Target = Share(), Folder = @"..\" + point.ToString("D"), PointId = point }));
        Assert.Equal("invalid_backup_folder", OperationValidation.Validate(new BackupVmRequest { VmId = vm, Target = Share(), Folder = @"a\b\" + point.ToString("D"), PointId = point }));
        Assert.Equal("invalid_checkpoint_type", OperationValidation.Validate(new BackupVmRequest { VmId = vm, Target = Share(), Folder = PointFolder(point), PointId = point, CheckpointType = "Recovery" }));

        RestoreBackupRequest Restore(string mode) => new() { Source = Share(), Folder = PointFolder(point), PointId = point, Mode = mode, NewName = "web01-restored" };
        Assert.Null(OperationValidation.Validate(Restore(RestoreModes.NewVm)));
        Assert.Null(OperationValidation.Validate(Restore(RestoreModes.NewVm) with { Location = @"D:\Restored", Network = RestoreNetworks.Switch, SwitchName = "vSwitch" }));
        Assert.Equal("invalid_switch_name", OperationValidation.Validate(Restore(RestoreModes.NewVm) with { Network = RestoreNetworks.Switch }));
        Assert.Equal("location_not_allowed", OperationValidation.Validate(Restore(RestoreModes.NewVm) with { Location = @"C:\Windows" }));
        // A restore test never reaches a network and is never left running.
        Assert.Equal("test_restore_is_isolated", OperationValidation.Validate(Restore(RestoreModes.Test)));
        Assert.Null(OperationValidation.Validate(Restore(RestoreModes.Test) with { Network = RestoreNetworks.Disconnected }));
        Assert.Equal("test_restore_is_isolated", OperationValidation.Validate(Restore(RestoreModes.Test) with { Network = RestoreNetworks.Disconnected, StartAfter = true }));
        // Replace keeps the original name and folders, and names the VM it replaces.
        Assert.Equal("invalid_vm", OperationValidation.Validate(Restore(RestoreModes.Replace) with { NewName = null }));
        Assert.Equal("replace_keeps_name_and_location", OperationValidation.Validate(Restore(RestoreModes.Replace) with { ReplaceVmId = vm }));
        Assert.Null(OperationValidation.Validate(Restore(RestoreModes.Replace) with { ReplaceVmId = vm, NewName = null }));

        var disk = new RestoreDiskRequest { Source = Share(), Folder = PointFolder(point), PointId = point, Disk = @"web01\Virtual Hard Disks\web01.vhdx", DestinationFolder = @"D:\Restored" };
        Assert.Null(OperationValidation.Validate(disk));
        Assert.Equal("invalid_disk", OperationValidation.Validate(disk with { Disk = @"..\..\Windows\system.vhdx" }));
        Assert.Equal("invalid_disk", OperationValidation.Validate(disk with { Disk = @"web01\Virtual Machines\x.vmcx" }));
        Assert.Equal("invalid_disk", OperationValidation.Validate(disk with { Disk = @"C:\x.vhdx" }));
        Assert.Equal("location_not_allowed", OperationValidation.Validate(disk with { DestinationFolder = @"C:\Program Files\x" }));
        Assert.Equal("staging_required", OperationValidation.Validate(new MountBackupDiskRequest
        {
            Source = new BackupTarget { Path = @"\\nas01\bk", UserName = "u", Password = "p" }, Folder = PointFolder(point), PointId = point, Disk = disk.Disk,
        }));
    }

    [Fact]
    public void Moves_and_new_folders_follow_the_folder_rules()
    {
        var vm = Guid.NewGuid();
        Assert.Null(OperationValidation.Validate(new MoveVmRequest { VmId = vm, DestinationHost = "hv02.contoso.local", DestinationPath = @"D:\VMs" }));
        Assert.Null(OperationValidation.Validate(new MoveVmRequest { VmId = vm, DestinationHost = "hv02", IncludeStorage = false }));
        Assert.Equal("destination_path_required", OperationValidation.Validate(new MoveVmRequest { VmId = vm, DestinationHost = "hv02" }));
        Assert.Equal("destination_path_only_with_storage", OperationValidation.Validate(new MoveVmRequest { VmId = vm, DestinationHost = "hv02", IncludeStorage = false, DestinationPath = @"D:\VMs" }));
        Assert.Equal("invalid_destination_host", OperationValidation.Validate(new MoveVmRequest { VmId = vm, DestinationHost = "hv02;calc", DestinationPath = @"D:\VMs" }));
        Assert.Equal("location_not_allowed", OperationValidation.Validate(new MoveVmStorageRequest { VmId = vm, DestinationPath = @"C:\Users\x" }));

        Assert.Null(OperationValidation.Validate(new NewFolderRequest(@"D:\VMs", "Project A")));
        Assert.Null(OperationValidation.Validate(new NewFolderRequest(@"D:\", "VMs")));
        Assert.Equal("location_not_allowed", OperationValidation.Validate(new NewFolderRequest(@"C:\", "Windows")));
        Assert.Equal("location_not_allowed", OperationValidation.Validate(new NewFolderRequest(@"C:\Windows", "x")));
        Assert.Equal("invalid_folder_name", OperationValidation.Validate(new NewFolderRequest(@"D:\VMs", @"a\b")));
        Assert.Equal("invalid_folder_name", OperationValidation.Validate(new NewFolderRequest(@"D:\VMs", "..")));
        Assert.Equal("invalid_folder_name", OperationValidation.Validate(new NewFolderRequest(@"D:\VMs", "$hidden")));
        Assert.Equal("invalid_folder_name", OperationValidation.Validate(new NewFolderRequest(@"D:\VMs", "CON")));
    }

    /// <summary>File access of a host whose allowed folders are set (no real file system needed for these checks).</summary>
    private sealed class RestrictedAccess(IReadOnlyList<string> folders, string? error = null) : HostFileAccess
    {
        public override bool IsLocal => true;
        public override IReadOnlyList<string> StorageFolders => folders;
        public override string? StorageError => error;
        public override string ToPhysical(string hostPath) => hostPath;
        public override string ToHost(string physicalPath) => physicalPath;
        public override IReadOnlyList<DriveEntry> FixedDrives() => [new(@"D:\", "Data", 1L << 40, 1L << 39), new(@"E:\", "Other", 1L << 40, 1L << 39)];
        public override bool IsFixedDrive(string root) => true;
        public override long? FreeSpace(string root) => 1L << 39;
    }

    [Fact]
    public void Allowed_storage_folders_restrict_every_path_the_portal_can_use_and_fail_closed()
    {
        Assert.True(StorageFolderRules.IsInside(@"D:\VMs\web01", [@"D:\VMs"]));
        Assert.True(StorageFolderRules.IsInside(@"d:\vms", [@"D:\VMs\"]));
        Assert.False(StorageFolderRules.IsInside(@"D:\VMs2", [@"D:\VMs"]));
        Assert.False(StorageFolderRules.IsInside(@"D:\VMs\..\Secret", [@"D:\VMs"]));
        Assert.True(StorageFolderRules.IsInside(@"E:\anything", []));
        Assert.True(StorageFolderRules.IsInside(@"E:\x", [@"E:\"]));
        Assert.Null(StorageFolderRules.Validate([@"D:\VMs", @"E:\"]));
        Assert.Equal("invalid_storage_folder", StorageFolderRules.Validate([@"C:\Windows"]));
        Assert.Equal("duplicate_storage_folder", StorageFolderRules.Validate([@"D:\VMs", @"d:\vms\"]));
        Assert.Equal("too_many_storage_folders", StorageFolderRules.Validate(Enumerable.Range(0, 17).Select(i => $@"D:\F{i}").ToList()));

        var fs = new RestrictedAccess([@"D:\VMs", @"D:\ISO"]);
        fs.CheckAllowed(@"D:\VMs\web01");
        var outside = Assert.Throws<HostPathException>(() => HostFolderBrowser.CheckFolder(@"E:\Data", mustExist: false, fs));
        Assert.StartsWith("location_not_allowed", outside.Message, StringComparison.Ordinal);
        Assert.True(fs.IsStorageRoot(@"d:\vms\"));
        Assert.False(fs.IsStorageRoot(@"D:\VMs\web01"));
        // The agent refuses operations outside the folders before PowerShell runs.
        Assert.StartsWith("invalid_location", PowerShellOperationRunner.Validate(HvOperation.MoveVmStorage,
            HvOperations.ToElement(new MoveVmStorageRequest { VmId = Guid.NewGuid(), DestinationPath = @"E:\Moved" }), fs));
        Assert.StartsWith("invalid_location", PowerShellOperationRunner.Validate(HvOperation.NewVm,
            HvOperations.ToElement(new NewVmRequest { Name = "web", Location = @"E:\VMs" }), fs));
        Assert.StartsWith("invalid_location", PowerShellOperationRunner.Validate(HvOperation.ListIsos, HvOperations.ToElement(new IsoListRequest([@"E:\ISO"])), fs));
        var point = Guid.NewGuid();
        Assert.StartsWith("invalid_location", PowerShellOperationRunner.Validate(HvOperation.BackupVm, HvOperations.ToElement(new BackupVmRequest
        {
            VmId = Guid.NewGuid(), Target = new BackupTarget { Path = @"E:\Backups" }, Folder = PointFolder(point), PointId = point,
        }), fs));
        // A share is not a host folder: the allowed folders do not apply to it, its staging folder does.
        Assert.Null(PowerShellOperationRunner.Validate(HvOperation.VerifyBackup,
            HvOperations.ToElement(new BackupPointRequest { Target = Share(), Folder = PointFolder(point), PointId = point }), fs));
        var outsideStaging = new BackupTarget { Path = @"\\nas01\backups\hv", UserName = @"NAS01\backup", Password = "s3cret!", StagingPath = @"E:\Staging" };
        Assert.StartsWith("invalid_location", PowerShellOperationRunner.Validate(HvOperation.VerifyBackup,
            HvOperations.ToElement(new BackupPointRequest { Target = outsideStaging, Folder = PointFolder(point), PointId = point }), fs));
        // The top level shows only the allowed folders that exist.
        Assert.Empty(HostFolderBrowser.TopLevel(new RestrictedAccess([@"D:\NoSuchFolder-" + Guid.NewGuid().ToString("N")])));
        Assert.Equal(2, HostFolderBrowser.TopLevel(new RestrictedAccess([])).Count);

        // A storage.json that cannot be read stops everything (never falls back to "all drives").
        var broken = new RestrictedAccess([], "storage.json is not valid");
        Assert.Throws<HostPathException>(() => broken.CheckAllowed(@"D:\VMs"));
        Assert.Throws<HostPathException>(() => HostFolderBrowser.TopLevel(broken));
    }

    [Fact]
    public void A_storage_file_with_invalid_folders_blocks_every_folder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hvp-storage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var none = LocalStorageFolders.Read(dir);
            Assert.Empty(none.Folders);
            Assert.Null(none.Error);
            File.WriteAllText(Path.Combine(dir, LocalStorageFolders.FileName), """{"folders":["C:\\Windows"]}""");
            var (folders, error) = LocalStorageFolders.Read(dir);
            Assert.Empty(folders);
            Assert.NotNull(error);
            File.WriteAllText(Path.Combine(dir, LocalStorageFolders.FileName), "not json");
            Assert.NotNull(LocalStorageFolders.Read(dir).Error);
            File.WriteAllText(Path.Combine(dir, LocalStorageFolders.FileName), """{"folders":["d:\\VMs\\","E:\\ISO"]}""");
            (folders, error) = LocalStorageFolders.Read(dir);
            Assert.Null(error);
            Assert.Equal([@"D:\VMs", @"E:\ISO"], folders);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Files_of_an_attached_backup_disk_are_reachable_only_inside_its_volumes()
    {
        var entry = new BackupMounts.Entry { MountId = Guid.NewGuid(), DiskPath = @"D:\Staging\x.vhdx", Volumes = [new MountedVolume(@"F:\", "Windows", "NTFS", 1)] };
        Assert.Equal(@"F:\", BackupMounts.RootOf(entry, @"F:\Users\x"));
        Assert.Equal(@"F:\", BackupMounts.RootOf(entry, @"f:\"));
        Assert.Throws<HostPathException>(() => BackupMounts.RootOf(entry, @"C:\Windows\system32\config\SAM"));
        Assert.Throws<HostPathException>(() => BackupMounts.RootOf(entry, @"G:\x"));
        Assert.Throws<HostPathException>(() => BackupFileService.List(new BackupFileListRequest(Guid.NewGuid(), @"F:\")));
        Assert.Equal("invalid_mount", OperationValidation.Validate(new BackupFileListRequest(Guid.Empty, @"F:\")));
        Assert.Equal("invalid_range", OperationValidation.Validate(new BackupFileReadRequest(Guid.NewGuid(), @"F:\a.txt", 0, FileTransfer.ChunkBytes + 1)));
    }

    [Fact]
    public void An_agent_update_request_is_a_version_a_sha256_and_a_bounded_size()
    {
        var ok = new AgentUpdateRequest { Version = "0.7.1", Sha256 = new string('A', 64), SizeBytes = 80_000_000 };
        Assert.Null(AgentUpdater.Validate(ok));
        Assert.Null(AgentUpdater.Validate(ok with { Version = "0.7.1.0" }));
        Assert.NotNull(AgentUpdater.Validate(null));
        Assert.NotNull(AgentUpdater.Validate(ok with { Version = "0.7.1; calc" }));
        Assert.NotNull(AgentUpdater.Validate(ok with { Version = @"..\..\x" }));
        Assert.NotNull(AgentUpdater.Validate(ok with { Sha256 = "abc" }));
        Assert.NotNull(AgentUpdater.Validate(ok with { SizeBytes = 0 }));
        Assert.NotNull(AgentUpdater.Validate(ok with { SizeBytes = AgentUpdater.MaxPackageBytes + 1 }));
    }

    [Fact]
    public void The_updater_installs_only_packages_signed_by_the_publisher_of_the_installed_agent()
    {
        var updater = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Installer", "Update-HvPortalAgent.ps1"));
        // Works on a private copy, compares the SHA-256, checks the catalog signer against the installed agent, the
        // catalog content, the new executable's signer and the version, then runs the package's own installer.
        foreach (var required in new[] { "Copy-Item -LiteralPath $package -Destination $zip", "Get-FileHash", "Test-FileCatalog", "$catalogSigner.Thumbprint -ne $installed.Thumbprint",
                     "$newSigner.Thumbprint -ne $installed.Thumbprint", "$newVersion -lt $oldVersion", "Set-PrivateAcl -Path $work", "Install-Agent.ps1" })
        {
            Assert.Contains(required, updater, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("Invoke-Expression", updater, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Start-Process", updater, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly string[] ExpectedStorageLists = [@"2:C:\HvpA|C:\HvpB", @"1:C:\HvpOnly", "0:"];

    [Fact]
    public void Install_agent_keeps_several_storage_folders_separate_in_windows_powershell()
    {
        // Regression: "$x = @(Test-StorageFolders ...)" wrapped the returned array, so two folders reached New-Item as
        // the single path "D:\VMs D:\ISO" ("The given path's format is not supported") in the middle of an upgrade.
        var dir = Path.Combine(AppContext.BaseDirectory, "Installer");
        var install = File.ReadAllText(Path.Combine(dir, "Install-Agent.ps1"));
        const string line = "$storageList = [string[]](Test-StorageFolders $StorageFolders)";
        Assert.Contains(line, install, StringComparison.Ordinal);
        Assert.DoesNotContain("@(Test-StorageFolders", install, StringComparison.Ordinal);

        var ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        Assert.True(File.Exists(ps), ps);
        // Test-StorageFolders only reads the drive type: nothing is created. Same line as Install-Agent.ps1.
        var script = ". '" + Path.Combine(dir, "Common.ps1").Replace("'", "''") + "'\n"
                     + "foreach ($case in @(@('C:\\HvpA', 'C:\\HvpB\\'), @('C:\\HvpOnly'), @(''))) {\n"
                     + "  [string[]]$StorageFolders = $case\n  " + line + "\n"
                     + "  Write-Output ('{0}:{1}' -f $storageList.Count, ($storageList -join '|'))\n}";
        var start = new ProcessStartInfo(ps, "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand "
                                             + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)))
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        Assert.Equal(ExpectedStorageLists, output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    [Fact]
    public void Operations_script_backups_never_leave_checkpoints_and_restores_never_destroy_the_original()
    {
        var script = Script();
        var backup = Branch(script, "BackupVm");
        Assert.Contains("Checkpoint-VM -VM $vm", backup, StringComparison.Ordinal);
        Assert.Contains("Export-VMSnapshot -VMSnapshot $checkpoint", backup, StringComparison.Ordinal);
        // The checkpoint is removed after the export and, on any failure, in the finally block.
        Assert.Contains("finally {\n                if ($checkpoint) { Remove-VMSnapshot", backup.Replace("\r", ""), StringComparison.Ordinal);
        // The checkpoint type of the VM is put back.
        Assert.Contains("Set-VM -VM (Get-VmById $a.vmId) -CheckpointType $configured", backup, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash", backup, StringComparison.Ordinal);
        // The manifest is written last; an incomplete folder of this run is removed.
        Assert.True(backup.IndexOf("hvportal-backup.json", StringComparison.Ordinal) > backup.IndexOf("Copy-PointFiles", StringComparison.Ordinal));
        foreach (var forbidden in new[] { "Remove-VM ", "Stop-VM", "Remove-VMHardDiskDrive", "Rename-Item" })
        {
            Assert.DoesNotContain(forbidden, backup, StringComparison.Ordinal);
        }
        var restore = Branch(script, "RestoreBackup");
        Assert.Contains("Rename-Item -LiteralPath $d -NewName ((Split-Path -Path $d -Leaf) + \".pre-restore-$stamp\")", restore, StringComparison.Ordinal);
        Assert.Contains("-GenerateNewId", restore, StringComparison.Ordinal);
        // A test restore turns off, removes and deletes the test VM in a finally block.
        Assert.Contains("Remove-VM -VM (Get-VM -Id $newVm.Id) -Force", restore, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-Item -LiteralPath $d", restore, StringComparison.Ordinal);
        var mount = Branch(script, "MountBackupDisk");
        Assert.Contains("Mount-VHD -Path $local -ReadOnly", mount, StringComparison.Ordinal);
        var dismount = Branch(script, "DismountBackupDisk");
        // Only staged copies made by the agent are deleted.
        Assert.Contains("-like 'hvportal-mount-*'", dismount, StringComparison.Ordinal);
        var remove = Branch(script, "RemoveBackup");
        Assert.Contains("Read-Manifest $pointPath $pointId", remove, StringComparison.Ordinal);
        // Credentials only reach a PowerShell drive of the operation; they are never written to a file.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(script, @"\$t\.password"));
        Assert.Contains("New-PSDrive -Name $name -PSProvider FileSystem -Root $path -Credential $credential -Scope Script", script, StringComparison.Ordinal);
        var move = Branch(script, "MoveVm");
        Assert.Contains("Move-VM @params", move, StringComparison.Ordinal);
        // Host migration settings are never changed by the portal (the error message only names what to do).
        Assert.DoesNotMatch(@"(^|[;{(])\s*Enable-VMMigration\b", move);
        Assert.DoesNotMatch(@"(^|[;{(])\s*Set-VMHost\b", script);
        Assert.Contains("Set-VMNetworkAdapterVlan -VMNetworkAdapter $nic -Access -VlanId", Branch(script, "NewVm"), StringComparison.Ordinal);
    }

    [Fact]
    public void Files_in_a_repository_are_not_written_with_set_content()
    {
        // 0.9.3 regression: on a drive of a share opened with credentials, Set-Content in Windows PowerShell 5.1 wrote to
        // \\server\share\server\share\server\share\... and the repository check failed ("Could not find a part of the
        // path"); the manifest of every backup to such a repository would have failed the same way.
        var script = Script();
        Assert.DoesNotMatch(@"\bSet-Content\s+-", script);
        Assert.DoesNotMatch(@"\b(Add-Content|Out-File)\b", script);
        Assert.Contains("Write-RepositoryText $probe 'HvPortal write test'", Branch(script, "GetBackupReadiness"), StringComparison.Ordinal);
        Assert.Contains("Write-RepositoryText (Join-Path $pointPath 'hvportal-backup.json')", Branch(script, "BackupVm"), StringComparison.Ordinal);
        Assert.Contains("[IO.File]::WriteAllText($full, $text, $encoding)", script, StringComparison.Ordinal);
    }
}
