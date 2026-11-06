using System.Text.Json;
using System.Text.Json.Serialization;
using HvPortal.Contracts.Agent;
using HvPortal.Contracts.HyperV;

namespace HvPortal.Gateway.Quotas;

public sealed record HostNetwork(Guid HostId, string Name);

public sealed record HostStoragePath(Guid HostId, string Path);

/// <summary>
/// Limits of a VM folder (ADR-024), stored in LogicalGroup.PolicyJson. They apply to the folder and to everything
/// below it; a VM must satisfy the limits of every folder above it.
///  - Max*: quotas on the total of the subtree (null = no limit).
///  - Hosts: where users whose permissions come from the folder may create VMs and networks (null = nowhere: creation
///    then needs the permission on the host itself).
///  - Networks: virtual switches the folder's VMs may use (null = any switch of the host; empty = only the private
///    networks created for the folder).
///  - Storage: folders of a host that may contain the folder's VMs and that folder users may browse (null = no restriction).
/// </summary>
public sealed record GroupPolicy
{
    public const int MaxListItems = 256;

    public int? MaxVms { get; init; }
    public int? MaxProcessors { get; init; }
    public long? MaxMemoryMb { get; init; }
    public long? MaxDiskGb { get; init; }
    public int? MaxNetworks { get; init; }
    public IReadOnlyList<Guid>? Hosts { get; init; }
    public IReadOnlyList<HostNetwork>? Networks { get; init; }
    public IReadOnlyList<HostStoragePath>? Storage { get; init; }

    [JsonIgnore]
    public bool IsEmpty => MaxVms is null && MaxProcessors is null && MaxMemoryMb is null && MaxDiskGb is null && MaxNetworks is null
                           && Hosts is null && Networks is null && Storage is null;

    public static GroupPolicy? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            var p = JsonSerializer.Deserialize<GroupPolicy>(json, AgentProtocol.Json)?.Normalize();
            return p is null || p.IsEmpty ? null : p;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string Serialize() => JsonSerializer.Serialize(this, AgentProtocol.Json);

    /// <summary>Canonical form: distinct entries, normalized paths, empty host and storage lists dropped.</summary>
    public GroupPolicy Normalize() => this with
    {
        Hosts = Hosts is { Count: > 0 } ? Hosts.Distinct().ToList() : null,
        Networks = Networks?.Where(n => n is not null && n.Name is not null).Select(n => n with { Name = n.Name.Trim() }).Distinct().ToList(),
        Storage = Storage is { Count: > 0 }
            ? Storage.Where(s => s is not null && s.Path is not null).Select(s => s with { Path = SafeNormalize(s.Path) }).Distinct().ToList()
            : null,
    };

    private static string SafeNormalize(string path)
    {
        var trimmed = path.Trim();
        return HostPaths.IsValidLocalFolder(trimmed) ? HostPaths.Normalize(trimmed) : trimmed;
    }

    /// <summary>Returns null when valid, otherwise an error code. Host ids are checked by the caller against the database.</summary>
    public string? Validate()
    {
        if (MaxVms is < 0 or > 100_000) return "invalid_max_vms";
        if (MaxProcessors is < 0 or > 1_000_000) return "invalid_max_processors";
        if (MaxMemoryMb is < 0 or > 1L << 40) return "invalid_max_memory";
        if (MaxDiskGb is < 0 or > 100_000_000) return "invalid_max_disk";
        if (MaxNetworks is < 0 or > 10_000) return "invalid_max_networks";
        if (Hosts is { Count: > MaxListItems } || Networks is { Count: > MaxListItems } || Storage is { Count: > MaxListItems }) return "too_many_entries";
        if (Hosts?.Any(h => h == Guid.Empty) == true) return "invalid_host";
        foreach (var n in Networks ?? [])
        {
            if (n.HostId == Guid.Empty) return "invalid_host";
            if (!OperationValidation.IsValidSwitchName(n.Name)) return "invalid_switch_name";
        }
        foreach (var s in Storage ?? [])
        {
            if (s.HostId == Guid.Empty) return "invalid_host";
            if (HostPaths.ValidateLocation(s.Path) is { } bad) return bad;
        }
        return null;
    }
}

/// <summary>A folder that has limits, on the chain between a folder (or a VM) and the root.</summary>
public sealed record PolicyLevel(Guid GroupId, string Name, GroupPolicy Policy);

public sealed record GroupUsage(int Vms, int Processors, long MemoryMb, long DiskBytes, int DiskUnknownVms, int Networks);

/// <summary>Why an operation was refused by the limits of a folder. Code is stable (API error code).</summary>
public sealed record PolicyViolation(string Code, string Message);
