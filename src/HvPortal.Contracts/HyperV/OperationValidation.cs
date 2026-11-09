using System.Text.RegularExpressions;

namespace HvPortal.Contracts.HyperV;

/// <summary>
/// Validation shared by the gateway (API input) and the agent (defense in depth before invoking Hyper-V).
/// Returns null when valid, otherwise a short error code.
/// </summary>
public static partial class OperationValidation
{
    public static readonly string[] StartActions = ["Nothing", "StartIfRunning", "Start"];
    public static readonly string[] StopActions = ["TurnOff", "Save", "ShutDown"];
    public static readonly string[] CheckpointTypes = ["Disabled", "Production", "ProductionOnly", "Standard"];
    public static readonly string[] SecureBootTemplates = ["MicrosoftWindows", "MicrosoftUEFICertificateAuthority", "OpenSourceShieldedVM"];

    public const int MaxNotes = 4000;
    public const long MinMemoryMb = 32;
    public const long MaxMemoryMb = 16L * 1024 * 1024; // 16 TB
    public const int MaxDiskGb = 65536;

    [GeneratedRegex(@"^[\p{L}\p{N}][\p{L}\p{N} ._()\-]{0,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex VmNameRegex();

    [GeneratedRegex(@"^[^\x00-\x1f""*?<>|]{1,100}$", RegexOptions.CultureInvariant)]
    private static partial Regex SwitchNameRegex();

    [GeneratedRegex(@"^[^\x00-\x1f""*?<>|]{1,260}\.iso$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex IsoPathRegex();

    [GeneratedRegex(@"^[\p{L}\p{N}][\p{L}\p{N} ._()\-:]{0,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex CheckpointNameRegex();

    /// <summary>Same character set as portal group names (letters, digits, space . _ ( ) -).</summary>
    [GeneratedRegex(@"^[\p{L}\p{N}][\p{L}\p{N} ._()\-]{0,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex VmGroupNameRegex();

    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}:(vms|mgmt)$", RegexOptions.CultureInvariant)]
    private static partial Regex VmGroupKeyRegex();

    public const int MaxVmGroupItems = 2000;

    public static bool IsValidVmName(string? name) => name is not null && VmNameRegex().IsMatch(name);
    public static bool IsValidSwitchName(string? name) => name is not null && SwitchNameRegex().IsMatch(name);
    public static bool IsValidIsoPath(string? path) => path is not null && IsoPathRegex().IsMatch(path) && !path.Contains("..", StringComparison.Ordinal);
    public static bool IsValidCheckpointName(string? name) => name is not null && CheckpointNameRegex().IsMatch(name);
    public static bool IsValidVmGroupName(string? name) => name is not null && VmGroupNameRegex().IsMatch(name);

    /// <summary>New switches: letters, digits, space . _ ( ) - (1-64). Existing switches may have other names (IsValidSwitchName).</summary>
    [GeneratedRegex(@"^[\p{L}\p{N}][\p{L}\p{N} ._()\-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex NewSwitchNameRegex();

    public static bool IsValidNewSwitchName(string? name) => name is not null && NewSwitchNameRegex().IsMatch(name) && name.Trim() == name;

    public static string? Validate(NewSwitchRequest r)
    {
        if (!IsValidNewSwitchName(r.Name)) return "invalid_switch_name";
        if (!SwitchTypes.All.Contains(r.Type)) return "invalid_switch_type";
        if (r.Type == SwitchTypes.External)
        {
            if (r.NetAdapterName is not { Length: > 0 and <= 256 } || r.NetAdapterName.Any(char.IsControl)) return "invalid_net_adapter";
        }
        else if (r.NetAdapterName is not null || r.AllowManagementOs)
        {
            return "net_adapter_only_for_external";
        }
        if (r.Notes is { Length: > 1024 }) return "notes_too_long";
        return null;
    }

    public static string? Validate(SwitchRef r) =>
        !Guid.TryParse(r.SwitchId, out var id) || id == Guid.Empty ? "invalid_switch"
        : r.Name is not null && !IsValidSwitchName(r.Name) ? "invalid_switch_name" : null;

    public static string? Validate(VmConfigChange c)
    {
        if (c.VmId == Guid.Empty) return "invalid_vm";
        if (c.NewName is not null && !IsValidVmName(c.NewName)) return "invalid_vm_name";
        if (c.Notes is { Length: > MaxNotes }) return "notes_too_long";
        if (c.ProcessorCount is < 1 or > 1024) return "invalid_processor_count";
        if (c.ProcessorReserve is < 0 or > 100) return "invalid_processor_reserve";
        if (c.ProcessorLimit is < 0 or > 100) return "invalid_processor_limit";
        if (c.ProcessorWeight is < 1 or > 10000) return "invalid_processor_weight";
        if (InvalidMemory(c.MemoryStartupMb) || InvalidMemory(c.MemoryMinimumMb) || InvalidMemory(c.MemoryMaximumMb)) return "invalid_memory";
        if (c.MemoryMinimumMb is { } min && c.MemoryStartupMb is { } st && min > st) return "memory_minimum_above_startup";
        if (c.MemoryMaximumMb is { } max && c.MemoryStartupMb is { } st2 && max < st2) return "memory_maximum_below_startup";
        if (c.MemoryBuffer is < 5 or > 2000) return "invalid_memory_buffer";
        if (c.MemoryPriority is < 0 or > 100) return "invalid_memory_priority";
        if (c.AutomaticStartAction is not null && !StartActions.Contains(c.AutomaticStartAction)) return "invalid_start_action";
        if (c.AutomaticStartDelaySeconds is < 0 or > 86400) return "invalid_start_delay";
        if (c.AutomaticStopAction is not null && !StopActions.Contains(c.AutomaticStopAction)) return "invalid_stop_action";
        if (c.CheckpointType is not null && !CheckpointTypes.Contains(c.CheckpointType)) return "invalid_checkpoint_type";
        if (c.SecureBootTemplate is not null && !SecureBootTemplates.Contains(c.SecureBootTemplate)) return "invalid_secure_boot_template";
        if (c.DvdIsoPath is { Length: > 0 } iso && !IsValidIsoPath(iso)) return "invalid_iso_path";
        foreach (var n in c.NetworkAdapterChanges ?? [])
        {
            if (string.IsNullOrWhiteSpace(n.AdapterId) || n.AdapterId.Length > 200) return "invalid_adapter";
            if (n.SwitchName is { Length: > 0 } sw && !IsValidSwitchName(sw)) return "invalid_switch_name";
            if (n.VlanId is < 0 or > 4094) return "invalid_vlan";
        }
        if (c.AddNetworkAdapterSwitch is { Length: > 0 } add && !IsValidSwitchName(add)) return "invalid_switch_name";
        if (!IsValidVlanId(c.AddNetworkAdapterVlanId) || (c.AddNetworkAdapterVlanId is not null && c.AddNetworkAdapterSwitch is not { Length: > 0 })) return "invalid_vlan";
        if (c.NewDisk is { } nd && (nd.SizeGb < 1 || nd.SizeGb > MaxDiskGb)) return "invalid_disk_size";
        if (c.NewDisk?.Folder is { } folder && HostPaths.ValidateLocation(folder) is { } badFolder) return badFolder;
        if (c.ExpandDisk is { } ed && (string.IsNullOrWhiteSpace(ed.Path) || ed.Path.Length > 1024 || ed.NewSizeGb < 1 || ed.NewSizeGb > MaxDiskGb)) return "invalid_disk_size";
        if (c.IsoRoots.Count > 16 || c.IsoRoots.Any(r => r.Length > 260)) return "invalid_iso_roots";
        return null;
    }

    public static string? Validate(NewVmRequest r)
    {
        if (!IsValidVmName(r.Name)) return "invalid_vm_name";
        if (r.Generation is not (1 or 2)) return "invalid_generation";
        if (r.ProcessorCount is < 1 or > 1024) return "invalid_processor_count";
        if (InvalidMemory(r.MemoryStartupMb) || InvalidMemory(r.MemoryMinimumMb) || InvalidMemory(r.MemoryMaximumMb)) return "invalid_memory";
        if (r.DynamicMemory && r.MemoryMinimumMb is { } min && min > r.MemoryStartupMb) return "memory_minimum_above_startup";
        if (r.DynamicMemory && r.MemoryMaximumMb is { } max && max < r.MemoryStartupMb) return "memory_maximum_below_startup";
        if (r.DiskSizeGb is < 0 or > MaxDiskGb) return "invalid_disk_size";
        if (r.SwitchName is { Length: > 0 } sw && !IsValidSwitchName(sw)) return "invalid_switch_name";
        if (!IsValidVlanId(r.VlanId) || (r.VlanId is not null && r.SwitchName is not { Length: > 0 })) return "invalid_vlan";
        if (r.IsoPath is { Length: > 0 } iso && !IsValidIsoPath(iso)) return "invalid_iso_path";
        if (r.SecureBootTemplate is { } t && t != "Off" && !SecureBootTemplates.Contains(t)) return "invalid_secure_boot_template";
        if (r.Generation == 1 && (r.EnableTpm || (r.SecureBootTemplate is { } t1 && t1 != "Off"))) return "gen1_no_secure_boot";
        if (r.CheckpointType is not null && !CheckpointTypes.Contains(r.CheckpointType)) return "invalid_checkpoint_type";
        if (r.AutomaticStartAction is not null && !StartActions.Contains(r.AutomaticStartAction)) return "invalid_start_action";
        if (r.Notes is { Length: > MaxNotes }) return "notes_too_long";
        if (HostPaths.ValidateLocation(r.Location) is { } loc) return loc;
        // Protocol 11: a highly available VM must have its files where every node of the cluster reaches them.
        if (r.HighlyAvailable && !ClusterNames.IsSharedVolumePath(r.Location)) return "ha_requires_shared_volume";
        if (r.IsoRoots.Count > 16 || r.IsoRoots.Any(x => x.Length > 260)) return "invalid_iso_roots";
        if (r.TemplateDisks.Count > 0)
        {
            if (r.TemplateDisks.Count > MaxTemplateDisks || InvalidTemplateRoots(r.TemplateRoots)) return "invalid_template";
            if (r.TemplateDisks.Any(d => !IsTemplateDiskPath(d) || !IsInside(d, r.TemplateRoots))) return "invalid_template";
            if (r.TemplateExtraDiskGb is < 0 or > MaxDiskGb * MaxTemplateDisks) return "invalid_template";
        }
        else if (r.TemplateRoots.Count > 0 || r.TemplateExtraDiskGb != 0)
        {
            return "invalid_template";
        }
        return null;
    }

    public const int MaxTemplateDisks = 16;
    public const int MaxTemplateRoots = 4;

    /// <summary>A virtual disk file (.vhdx / .vhd) with a valid local path.</summary>
    public static bool IsTemplateDiskPath(string? path) =>
        HostPaths.IsValidFilePath(path) && !HostPaths.IsBlocked(path!)
        && (path!.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".vhd", StringComparison.OrdinalIgnoreCase));

    private static bool InvalidTemplateRoots(IReadOnlyList<string> roots) =>
        roots.Count is 0 or > MaxTemplateRoots || roots.Any(r => !HostPaths.IsValidLocalFolder(r) || HostPaths.IsBlocked(r) || r.Contains("..", StringComparison.Ordinal));

    /// <summary>Text containment (the agent checks the real file system as well: no junctions or symbolic links).</summary>
    private static bool IsInside(string path, IReadOnlyList<string> roots) =>
        !path.Contains("..", StringComparison.Ordinal)
        && roots.Any(r => path.Length > r.TrimEnd('\\').Length + 1 && path.StartsWith(r.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));

    /// <summary>Template folder = &lt;root&gt;\&lt;guid&gt;: exactly one level below a template library folder, named by the template id.</summary>
    public static bool IsTemplateFolder(string? folder, IReadOnlyList<string> roots)
    {
        if (!HostPaths.IsValidLocalFolder(folder) || HostPaths.IsBlocked(folder!) || folder!.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }
        var trimmed = folder.TrimEnd('\\');
        var cut = trimmed.LastIndexOf('\\');
        return cut > 2 && Guid.TryParseExact(trimmed[(cut + 1)..], "D", out _)
            && roots.Any(r => string.Equals(r.TrimEnd('\\'), trimmed[..cut], StringComparison.OrdinalIgnoreCase));
    }

    public static string? Validate(NewTemplateRequest r) =>
        r.VmId == Guid.Empty ? "invalid_vm"
        : InvalidTemplateRoots(r.Roots) || !IsTemplateFolder(r.Folder, r.Roots) ? "invalid_template_folder" : null;

    public static string? Validate(TemplateFolderRequest r) =>
        InvalidTemplateRoots(r.Roots) || !IsTemplateFolder(r.Folder, r.Roots) ? "invalid_template_folder" : null;

    public static string? Validate(VmGroupSyncRequest r)
    {
        if (r.Items.Count > MaxVmGroupItems || r.Managed.Count > MaxVmGroupItems) return "too_many_groups";
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var i in r.Items)
        {
            if (!VmGroupKeyRegex().IsMatch(i.Key) || !keys.Add(i.Key)) return "invalid_group_key";
            if (!VmGroupNameRegex().IsMatch(i.Name)) return "invalid_group_name";
            var vmType = i.GroupType == VmGroupTypes.Vm;
            if (!vmType && i.GroupType != VmGroupTypes.Management) return "invalid_group_type";
            if (i.Key.EndsWith(":vms", StringComparison.Ordinal) != vmType) return "invalid_group_type";
            if (vmType ? i.MemberKeys.Count > 0 : i.VmIds.Count > 0) return "invalid_group_members";
            if (i.VmIds.Count > 4096 || i.MemberKeys.Count > MaxVmGroupItems || i.VmIds.Contains(Guid.Empty)) return "invalid_group_members";
        }
        var vmKeys = r.Items.Where(i => i.GroupType == VmGroupTypes.Vm).Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
        if (r.Items.Any(i => i.MemberKeys.Any(k => !vmKeys.Contains(k)))) return "invalid_group_members";
        return null;
    }

    public static string? Validate(FileReadRequest r) =>
        !HostPaths.IsValidFilePath(r.Path) ? "invalid_path"
        : HostPaths.IsBlocked(r.Path) ? "location_not_allowed"
        : r.Offset < 0 || r.Length is < 1 or > FileTransfer.ChunkBytes ? "invalid_range" : null;

    public static string? Validate(FileWriteBeginRequest r) =>
        !HostPaths.IsValidLocalFolder(r.Folder) ? "invalid_path"
        : HostPaths.IsBlocked(r.Folder) ? "location_not_allowed"
        : !HostPaths.IsValidFileName(r.Name) ? "invalid_file_name"
        : r.SizeBytes is < 0 or > FileTransfer.MaxUploadBytes ? "invalid_size" : null;

    public static string? Validate(FolderBrowseRequest r) =>
        r.Path is null || HostPaths.IsValidLocalFolder(r.Path) ? null : "invalid_path";

    private static bool InvalidMemory(long? mb) => mb is { } v && (v < MinMemoryMb || v > MaxMemoryMb || v % 2 != 0);

    /// <summary>
    /// Fields Hyper-V lets you change only while the VM is off: used to explain/deny early. Rules that depend on the
    /// current configuration (startup memory with dynamic memory, IDE disks, VHD files) are enforced by the UI and by
    /// Hyper-V itself (the job fails with its error). <paramref name="generation"/> is the VM generation when known.
    /// </summary>
    public static IReadOnlyList<string> FieldsRequiringOff(VmConfigChange c, int? generation = null)
    {
        var list = new List<string>();
        if (c.ProcessorCount is not null) list.Add("processorCount");
        if (c.DynamicMemoryEnabled is not null) list.Add("dynamicMemoryEnabled");
        if (c.SecureBootEnabled is not null || c.SecureBootTemplate is not null) list.Add("secureBoot");
        if (c.AutomaticStopAction is not null) list.Add("automaticStopAction");
        if (generation == 1)
        {
            // Generation 1: network adapters and IDE disks cannot be hot-added.
            if (c.AddNetworkAdapterSwitch is not null) list.Add("addNetworkAdapter");
            if (c.NewDisk is not null) list.Add("newDisk");
        }
        return list;
    }
}
