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

    public static bool IsValidVmName(string? name) => name is not null && VmNameRegex().IsMatch(name);
    public static bool IsValidSwitchName(string? name) => name is not null && SwitchNameRegex().IsMatch(name);
    public static bool IsValidIsoPath(string? path) => path is not null && IsoPathRegex().IsMatch(path) && !path.Contains("..", StringComparison.Ordinal);
    public static bool IsValidCheckpointName(string? name) => name is not null && CheckpointNameRegex().IsMatch(name);

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
        if (c.NewDisk is { } nd && (nd.SizeGb < 1 || nd.SizeGb > MaxDiskGb)) return "invalid_disk_size";
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
        if (r.IsoPath is { Length: > 0 } iso && !IsValidIsoPath(iso)) return "invalid_iso_path";
        if (r.SecureBootTemplate is { } t && t != "Off" && !SecureBootTemplates.Contains(t)) return "invalid_secure_boot_template";
        if (r.Generation == 1 && (r.EnableTpm || (r.SecureBootTemplate is { } t1 && t1 != "Off"))) return "gen1_no_secure_boot";
        if (r.CheckpointType is not null && !CheckpointTypes.Contains(r.CheckpointType)) return "invalid_checkpoint_type";
        if (r.AutomaticStartAction is not null && !StartActions.Contains(r.AutomaticStartAction)) return "invalid_start_action";
        if (r.Notes is { Length: > MaxNotes }) return "notes_too_long";
        if (r.IsoRoots.Count > 16 || r.IsoRoots.Any(x => x.Length > 260)) return "invalid_iso_roots";
        return null;
    }

    private static bool InvalidMemory(long? mb) => mb is { } v && (v < MinMemoryMb || v > MaxMemoryMb || v % 2 != 0);

    /// <summary>Fields Hyper-V lets you change only while the VM is off (or saved for some): used to explain/deny early.</summary>
    public static IReadOnlyList<string> FieldsRequiringOff(VmConfigChange c)
    {
        var list = new List<string>();
        if (c.ProcessorCount is not null) list.Add("processorCount");
        if (c.DynamicMemoryEnabled is not null) list.Add("dynamicMemoryEnabled");
        if (c.SecureBootEnabled is not null || c.SecureBootTemplate is not null) list.Add("secureBoot");
        return list;
    }
}
