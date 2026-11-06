using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Jobs;

namespace HvPortal.Gateway.Tests.Unit;

public sealed class OperationRulesTests
{
    [Theory]
    [InlineData("web-01", true)]
    [InlineData("SQL Server (prod)", true)]
    [InlineData("Città 1", true)]
    [InlineData("", false)]
    [InlineData(" leading", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData("name;rm", false)]
    [InlineData("$(evil)", false)]
    public void Vm_names_are_restricted(string name, bool valid) => Assert.Equal(valid, OperationValidation.IsValidVmName(name));

    [Theory]
    [InlineData(@"D:\ISO\win.iso", true)]
    [InlineData(@"D:\ISO\sub\linux.ISO", true)]
    [InlineData(@"D:\ISO\..\Windows\x.iso", false)]
    [InlineData(@"D:\ISO\file.vhdx", false)]
    [InlineData("D:\\ISO\\a\"b.iso", false)]
    public void Iso_paths_are_restricted(string path, bool valid) => Assert.Equal(valid, OperationValidation.IsValidIsoPath(path));

    [Fact]
    public void Config_validation_rejects_inconsistent_values()
    {
        var vm = Guid.NewGuid();
        Assert.Null(OperationValidation.Validate(new VmConfigChange { VmId = vm, ProcessorCount = 4, MemoryStartupMb = 4096 }));
        Assert.Equal("invalid_vm", OperationValidation.Validate(new VmConfigChange { ProcessorCount = 4 }));
        Assert.Equal("invalid_memory", OperationValidation.Validate(new VmConfigChange { VmId = vm, MemoryStartupMb = 1001 }));
        Assert.Equal("memory_minimum_above_startup", OperationValidation.Validate(new VmConfigChange { VmId = vm, MemoryStartupMb = 2048, MemoryMinimumMb = 4096 }));
        Assert.Equal("invalid_vlan", OperationValidation.Validate(new VmConfigChange { VmId = vm, NetworkAdapterChanges = [new NetworkAdapterChange("nic", null, 5000)] }));
        Assert.Equal("invalid_start_action", OperationValidation.Validate(new VmConfigChange { VmId = vm, AutomaticStartAction = "Always" }));
        Assert.Equal("invalid_disk_size", OperationValidation.Validate(new VmConfigChange { VmId = vm, NewDisk = new NewDiskSpec(0, false) }));
        Assert.Equal(new[] { "processorCount", "dynamicMemoryEnabled", "secureBoot" },
            OperationValidation.FieldsRequiringOff(new VmConfigChange { VmId = vm, ProcessorCount = 2, DynamicMemoryEnabled = true, SecureBootEnabled = false }));
        // Hot-changeable on any generation; automatic stop action never; hot-add of NIC/disk only on Generation 2.
        var mixed = new VmConfigChange
        {
            VmId = vm, Notes = "x", ProcessorLimit = 50, MemoryBuffer = 30, AutomaticStopAction = "ShutDown",
            AddNetworkAdapterSwitch = "LAN", NewDisk = new NewDiskSpec(10, false),
        };
        Assert.Equal(new[] { "automaticStopAction" }, OperationValidation.FieldsRequiringOff(mixed, 2));
        Assert.Equal(new[] { "automaticStopAction", "addNetworkAdapter", "newDisk" }, OperationValidation.FieldsRequiringOff(mixed, 1));
    }

    [Fact]
    public void New_vm_validation()
    {
        Assert.Null(OperationValidation.Validate(new NewVmRequest { Name = "app-01" }));
        Assert.Equal("invalid_generation", OperationValidation.Validate(new NewVmRequest { Name = "a", Generation = 3 }));
        Assert.Equal("gen1_no_secure_boot", OperationValidation.Validate(new NewVmRequest { Name = "a", Generation = 1, SecureBootTemplate = "MicrosoftWindows" }));
        Assert.Null(OperationValidation.Validate(new NewVmRequest { Name = "a", Generation = 1, SecureBootTemplate = "Off" }));
        Assert.Equal("invalid_secure_boot_template", OperationValidation.Validate(new NewVmRequest { Name = "a", SecureBootTemplate = "Custom" }));
        Assert.Equal("invalid_switch_name", OperationValidation.Validate(new NewVmRequest { Name = "a", SwitchName = "bad|name" }));
    }

    [Theory]
    [InlineData(@"D:\ISO", true)]
    [InlineData(@"E:\Library\Images\", true)]
    [InlineData(@"\\server\share", false)]
    [InlineData(@"D:\ISO\..\Windows", false)]
    [InlineData(@"ISO", false)]
    [InlineData(@"D:", false)]
    [InlineData(@"\\?\D:\ISO", false)]
    [InlineData(@"D:\a:b", false)]
    public void Iso_library_folders_are_local_absolute_paths(string path, bool valid) => Assert.Equal(valid, OperationPolicy.IsValidLibraryPath(path));

    [Fact]
    public void Iso_containment_is_by_folder_not_by_prefix()
    {
        string[] roots = [@"D:\ISO"];
        Assert.True(OperationPolicy.IsInsideLibrary(@"D:\ISO\win.iso", roots));
        Assert.True(OperationPolicy.IsInsideLibrary(@"d:\iso\sub\win.iso", roots));
        Assert.False(OperationPolicy.IsInsideLibrary(@"D:\ISO2\win.iso", roots));
        Assert.False(OperationPolicy.IsInsideLibrary(@"D:\ISO\..\win.iso", roots));
        Assert.False(OperationPolicy.IsInsideLibrary(@"D:\ISO\win.iso", []));
    }

    [Fact]
    public void Config_sections_map_to_permissions()
    {
        var vm = Guid.NewGuid();
        Assert.Equal(new[] { Permissions.VmConfigCompute }, OperationPolicy.PermissionsFor(new VmConfigChange { VmId = vm, Notes = "x" }));
        Assert.Equal(new[] { Permissions.VmConfigStorage }, OperationPolicy.PermissionsFor(new VmConfigChange { VmId = vm, DvdIsoPath = "" }));
        Assert.Equal(new[] { Permissions.VmConfigNetwork }, OperationPolicy.PermissionsFor(new VmConfigChange { VmId = vm, AddNetworkAdapterSwitch = "" }));
        Assert.Equal(new[] { Permissions.VmConfigCompute, Permissions.VmConfigStorage, Permissions.VmConfigNetwork },
            OperationPolicy.PermissionsFor(new VmConfigChange { VmId = vm, ProcessorCount = 2, NewDisk = new NewDiskSpec(10, false), NetworkAdapterChanges = [new NetworkAdapterChange("n", "s", null)] }));
        Assert.Empty(OperationPolicy.PermissionsFor(new VmConfigChange { VmId = vm }));
    }

    [Fact]
    public void Job_arguments_are_restamped_at_execution()
    {
        var host = new ManagedHost { DisplayName = "h", IsoLibraryPathsJson = "[\"D:\\\\ISO\"]" };
        var vm = new VirtualMachine { HostId = host.Id, HyperVId = Guid.NewGuid(), Name = "v" };
        var forged = new VmConfigChange { VmId = Guid.NewGuid(), DvdIsoPath = @"D:\ISO\a.iso", IsoRoots = [@"C:\"] };
        var args = JobRunner.PrepareArguments(HvOperation.SetVmConfig, HvOperations.ToElement(forged), vm, host);
        var change = HvOperations.FromElement<VmConfigChange>(args)!;
        Assert.Equal(vm.HyperVId, change.VmId);
        Assert.Equal(new[] { @"D:\ISO" }, change.IsoRoots);

        // Library changed after the job was queued: the ISO is no longer allowed.
        host.IsoLibraryPathsJson = "[\"E:\\\\Other\"]";
        Assert.Null(JobRunner.PrepareArguments(HvOperation.SetVmConfig, HvOperations.ToElement(forged), vm, host));
        // Operations that need a VM are not built without one.
        Assert.Null(JobRunner.PrepareArguments(HvOperation.NewCheckpoint, HvOperations.ToElement(new CheckpointRequest(Guid.NewGuid(), "x", null, false)), null, host));
    }

    [Fact]
    public void Lock_keys_serialize_vm_operations_per_vm_and_creation_per_host()
    {
        var id = Guid.NewGuid();
        Assert.StartsWith("vm:", JobRunner.LockKey(new Job { Type = "vm.config", Operation = "Update", RequiredPermission = "x", TargetType = ScopeType.Vm, TargetId = id }));
        Assert.StartsWith("host-create:", JobRunner.LockKey(new Job { Type = "vm.create", Operation = "New", RequiredPermission = "x", TargetType = ScopeType.Host, TargetId = id }));
    }
}
