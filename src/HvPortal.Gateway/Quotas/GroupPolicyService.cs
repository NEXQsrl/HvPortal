using System.Text.Json;
using HvPortal.Contracts.Agent;
using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Agents;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Jobs;
using Microsoft.EntityFrameworkCore;

namespace HvPortal.Gateway.Quotas;

/// <summary>
/// Evaluates the limits of VM folders (ADR-024): which hosts, networks and storage a folder may use and how much of its
/// quotas is in use. Used by the API when an operation is requested and again by the job runner when it is executed.
/// Usage of a folder = its VMs and those of every folder below it, plus the VM/network creations already queued for it.
/// Scoped per request / job execution (no caching across requests).
/// </summary>
public sealed class GroupPolicyService
{
    private const long Gb = 1L << 30;
    private static readonly JobStatus[] ActiveStatuses = [JobStatus.Queued, JobStatus.Running, JobStatus.Reconciling];

    private readonly PortalDbContext _db;
    private readonly AccessEvaluator _access;
    private readonly IAgentDispatcher _agents;
    private readonly Dictionary<Guid, List<HostDiskInfo>?> _diskListings = [];
    private GroupTree? _tree;
    private Dictionary<Guid, LogicalGroup>? _groups;

    public GroupPolicyService(PortalDbContext db, AccessEvaluator access, IAgentDispatcher agents)
    {
        _db = db;
        _access = access;
        _agents = agents;
    }

    private async Task<(GroupTree Tree, Dictionary<Guid, LogicalGroup> Groups)> LoadAsync(CancellationToken ct)
    {
        _groups ??= await _db.LogicalGroups.AsNoTracking().ToDictionaryAsync(g => g.Id, ct);
        _tree ??= await GroupTree.LoadAsync(_db, ct);
        return (_tree, _groups);
    }

    private static List<PolicyLevel> Levels(IEnumerable<Guid> groupIds, Dictionary<Guid, LogicalGroup> groups)
    {
        var list = new List<PolicyLevel>();
        foreach (var id in groupIds)
        {
            if (groups.TryGetValue(id, out var g) && GroupPolicy.Parse(g.PolicyJson) is { } policy)
            {
                list.Add(new PolicyLevel(g.Id, g.Name, policy));
            }
        }
        return list;
    }

    /// <summary>Limits of the folder and of its ancestors, nearest first.</summary>
    public async Task<IReadOnlyList<PolicyLevel>> ChainAsync(Guid groupId, CancellationToken ct)
    {
        var (tree, groups) = await LoadAsync(ct);
        return Levels(tree.Chain(groupId), groups);
    }

    /// <summary>Hosts opened to the folder: intersection of the host lists along the chain; null = none (not delegated).</summary>
    public static HashSet<Guid>? DelegatedHosts(IReadOnlyList<PolicyLevel> chain)
    {
        HashSet<Guid>? set = null;
        foreach (var level in chain)
        {
            if (level.Policy.Hosts is { Count: > 0 } hosts)
            {
                if (set is null)
                {
                    set = [.. hosts];
                }
                else
                {
                    set.IntersectWith(hosts);
                }
            }
        }
        return set;
    }

    /// <summary>The user holds the permission on the folder and the folder's limits open that host to its users.</summary>
    public async Task<bool> HoldsViaGroupAsync(Guid userId, string permission, Guid groupId, Guid hostId, CancellationToken ct)
    {
        var (_, groups) = await LoadAsync(ct);
        if (!groups.ContainsKey(groupId) || !await _access.HasAsync(userId, permission, ResourceRef.Group(groupId), ct))
        {
            return false;
        }
        return DelegatedHosts(await ChainAsync(groupId, ct))?.Contains(hostId) == true;
    }

    public async Task<bool> HoldsOnHostOrGroupAsync(Guid userId, string permission, Guid hostId, Guid? groupId, CancellationToken ct) =>
        await _access.HasAsync(userId, permission, ResourceRef.Host(hostId), ct)
        || (groupId is { } g && await HoldsViaGroupAsync(userId, permission, g, hostId, ct));

    // ------------------------------------------------------------------ networks and storage

    /// <summary>
    /// Switch names the VMs of a folder chain may use on a host; null = no restriction. Private networks created for a
    /// folder of the chain are always allowed.
    /// </summary>
    public async Task<HashSet<string>?> AllowedSwitchesAsync(IReadOnlyList<PolicyLevel> levels, IReadOnlyCollection<Guid> chainGroups, Guid hostId, CancellationToken ct)
    {
        HashSet<string>? set = null;
        foreach (var level in levels)
        {
            if (level.Policy.Networks is { } networks)
            {
                var names = networks.Where(n => n.HostId == hostId).Select(n => n.Name).ToHashSet(StringComparer.Ordinal);
                if (set is null)
                {
                    set = names;
                }
                else
                {
                    set.IntersectWith(names);
                }
            }
        }
        if (set is null)
        {
            return null;
        }
        var owned = await _db.GroupNetworks.AsNoTracking().Where(n => n.HostId == hostId && chainGroups.Contains(n.GroupId)).Select(n => n.SwitchName).ToListAsync(ct);
        set.UnionWith(owned);
        return set;
    }

    /// <summary>One list of roots per folder of the chain that restricts storage: a path must be inside a root of every list.</summary>
    public static List<List<string>> StorageRules(IReadOnlyList<PolicyLevel> levels, Guid hostId) =>
        levels.Where(l => l.Policy.Storage is { Count: > 0 })
            .Select(l => l.Policy.Storage!.Where(s => s.HostId == hostId).Select(s => s.Path).ToList()).ToList();

    public static bool IsInside(string path, string root)
    {
        var r = root.TrimEnd('\\');
        return path.Equals(r, StringComparison.OrdinalIgnoreCase) || path.Equals(root, StringComparison.OrdinalIgnoreCase)
               || path.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase);
    }

    public static bool StorageAllows(List<List<string>> rules, string path) => rules.All(roots => roots.Any(r => IsInside(path, r)));

    /// <summary>Roots a folder user may browse on a host: the roots of the nearest restricting folder that satisfy every other one.</summary>
    public static List<string> BrowsableRoots(IReadOnlyList<PolicyLevel> chain, Guid hostId)
    {
        var rules = StorageRules(chain, hostId);
        return rules.Count == 0 ? [] : rules[0].Where(r => StorageAllows(rules, r)).ToList();
    }

    /// <summary>What a folder may use on a host: the switches (all of them when not restricted) and its storage roots (empty = not restricted).</summary>
    public sealed record FolderHostView(IReadOnlyList<string> Switches, bool NetworksRestricted, IReadOnlyList<string> StorageRoots);

    public async Task<FolderHostView> HostViewAsync(Guid groupId, Guid hostId, IReadOnlyList<string> hostSwitches, CancellationToken ct)
    {
        var (tree, groups) = await LoadAsync(ct);
        var chainGroups = tree.Chain(groupId);
        var levels = Levels(chainGroups, groups);
        var allowed = levels.Count == 0 ? null : await AllowedSwitchesAsync(levels, chainGroups, hostId, ct);
        return new FolderHostView(allowed is null ? hostSwitches : hostSwitches.Where(allowed.Contains).ToList(), allowed is not null, BrowsableRoots(levels, hostId));
    }

    // ------------------------------------------------------------------ usage

    /// <summary>Memory a VM can take from the host: the dynamic maximum when known, otherwise the startup memory.</summary>
    public static long MemoryFootprintMb(VirtualMachine vm) =>
        vm.DynamicMemoryEnabled == true ? Math.Max(vm.MemoryStartupMb ?? 0, vm.MemoryMaximumMb ?? vm.MemoryStartupMb ?? 0) : vm.MemoryStartupMb ?? 0;

    private async Task<List<HostDiskInfo>?> DisksOfHostAsync(Guid hostId, CancellationToken ct)
    {
        if (_diskListings.TryGetValue(hostId, out var known))
        {
            return known;
        }
        List<HostDiskInfo>? list = null;
        if (_agents.IsConnected(hostId))
        {
            try
            {
                var r = await _agents.SendAsync(hostId, new AgentCommand
                {
                    CommandId = Guid.NewGuid(),
                    Kind = AgentCommandKind.HyperVOperation,
                    Operation = HvOperation.ListHostDisks,
                    OperationArgs = HvOperations.ToElement(new { }),
                    TimeoutSeconds = 120,
                }, null, TimeSpan.FromSeconds(135), ct);
                if (r.Operation is { Ok: true, Result: { } result })
                {
                    list = result.Deserialize<List<HostDiskInfo>>(AgentProtocol.Json);
                }
            }
            catch (Exception ex) when (ex is HostNotConnectedException or AgentChannelLostException or TimeoutException or JsonException)
            {
                list = null;
            }
        }
        if (list is not null)
        {
            await SaveDiskTotalsAsync(hostId, list, ct);
        }
        return _diskListings[hostId] = list;
    }

    /// <summary>Remembers the provisioned disk size of each VM of the host (used when the host cannot be asked).</summary>
    public async Task SaveDiskTotalsAsync(Guid hostId, IReadOnlyList<HostDiskInfo> disks, CancellationToken ct)
    {
        var totals = disks.Where(d => d.VmId is not null).GroupBy(d => d.VmId!.Value).ToDictionary(g => g.Key, g => g.Sum(d => d.SizeBytes ?? 0));
        var current = await _db.VirtualMachines.AsNoTracking().Where(v => v.HostId == hostId && !v.IsMissing)
            .Select(v => new { v.Id, v.HyperVId, v.DiskProvisionedBytes }).ToListAsync(ct);
        foreach (var vm in current)
        {
            long? total = totals.GetValueOrDefault(vm.HyperVId);
            if (vm.DiskProvisionedBytes != total)
            {
                await _db.VirtualMachines.Where(v => v.Id == vm.Id).ExecuteUpdateAsync(u => u.SetProperty(v => v.DiskProvisionedBytes, total), ct);
            }
        }
    }

    private sealed record Reservation(Guid GroupId, int Vms, int Processors, long MemoryMb, long DiskBytes, int Networks);

    /// <summary>
    /// Creations already accepted for a folder but not finished: they count against the quotas. For a job that is being
    /// executed (<paramref name="forJobId"/>) only the creations requested before it count, so that of two requests the
    /// earlier one keeps its place.
    /// </summary>
    private async Task<List<Reservation>> ReservationsAsync(Guid? forJobId, CancellationToken ct)
    {
        var active = await _db.Jobs.AsNoTracking()
            .Where(j => (j.Type == JobService.CreateJobType || j.Type == JobService.NetworkJobType) && ActiveStatuses.Contains(j.Status))
            .Select(j => new { j.Id, j.CreatedAt, j.Type, j.Operation, j.ParametersJson }).ToListAsync(ct);
        var self = forJobId is { } id ? await _db.Jobs.AsNoTracking().Where(j => j.Id == id).Select(j => new { j.Id, j.CreatedAt }).FirstOrDefaultAsync(ct) : null;
        var jobs = active.Where(j => j.Id != forJobId
            && (self is null || j.CreatedAt < self.CreatedAt || (j.CreatedAt == self.CreatedAt && j.Id.CompareTo(self.Id) < 0)));
        var list = new List<Reservation>();
        foreach (var j in jobs)
        {
            try
            {
                if (j.ParametersJson is null || JsonSerializer.Deserialize<JobService.OperationParameters>(j.ParametersJson, AgentProtocol.Json) is not { GroupId: { } groupId } p)
                {
                    continue;
                }
                if (j.Type == JobService.CreateJobType && HvOperations.FromElement<NewVmRequest>(p.Args) is { } spec)
                {
                    list.Add(new Reservation(groupId, 1, spec.ProcessorCount, NewVmFootprintMb(spec), (spec.DiskSizeGb + spec.TemplateExtraDiskGb) * Gb, 0));
                }
                else if (j.Type == JobService.NetworkJobType && j.Operation == "Create")
                {
                    list.Add(new Reservation(groupId, 0, 0, 0, 0, 1));
                }
            }
            catch (JsonException)
            {
                // Unreadable parameters: the job fails on its own when it runs.
            }
        }
        return list;
    }

    private static long NewVmFootprintMb(NewVmRequest spec) =>
        spec.DynamicMemory ? Math.Max(spec.MemoryStartupMb, spec.MemoryMaximumMb ?? spec.MemoryStartupMb) : spec.MemoryStartupMb;

    /// <summary>
    /// Usage of the folder subtree. With <paramref name="liveDisks"/> the disk sizes are read from the connected hosts;
    /// otherwise (or when a host cannot be reached) the last known sizes are used and VMs never read are counted as unknown.
    /// </summary>
    public async Task<GroupUsage> UsageAsync(Guid groupId, bool liveDisks, Guid? excludeJobId, CancellationToken ct)
    {
        var (tree, _) = await LoadAsync(ct);
        var subtree = tree.Subtree(groupId);
        var vmIds = tree.MembersOf(subtree).ToList();
        var vms = await _db.VirtualMachines.AsNoTracking().Where(v => vmIds.Contains(v.Id) && !v.IsMissing).ToListAsync(ct);
        long disk = 0;
        var unknown = 0;
        foreach (var vm in vms)
        {
            var listing = liveDisks ? await DisksOfHostAsync(vm.HostId, ct) : null;
            if (listing is not null)
            {
                disk += listing.Where(d => d.VmId == vm.HyperVId).Sum(d => d.SizeBytes ?? 0);
            }
            else if (vm.DiskProvisionedBytes is { } cached)
            {
                disk += cached;
            }
            else
            {
                unknown++;
            }
        }
        var networks = await _db.GroupNetworks.AsNoTracking().CountAsync(n => subtree.Contains(n.GroupId), ct);
        var reserved = (await ReservationsAsync(excludeJobId, ct)).Where(r => subtree.Contains(r.GroupId)).ToList();
        return new GroupUsage(
            vms.Count + reserved.Sum(r => r.Vms),
            vms.Sum(v => v.ProcessorCount ?? 0) + reserved.Sum(r => r.Processors),
            vms.Sum(MemoryFootprintMb) + reserved.Sum(r => r.MemoryMb),
            disk + reserved.Sum(r => r.DiskBytes),
            unknown,
            networks + reserved.Sum(r => r.Networks));
    }

    private async Task<PolicyViolation?> CheckQuotasAsync(IReadOnlyList<PolicyLevel> levels, int vms, int processors, long memoryMb, long diskBytes, int networks,
        Guid? excludeJobId, CancellationToken ct)
    {
        foreach (var level in levels)
        {
            var p = level.Policy;
            var relevant = (vms > 0 && p.MaxVms is not null) || (processors > 0 && p.MaxProcessors is not null) || (memoryMb > 0 && p.MaxMemoryMb is not null)
                           || (diskBytes > 0 && p.MaxDiskGb is not null) || (networks > 0 && p.MaxNetworks is not null);
            if (!relevant)
            {
                continue;
            }
            var checkDisk = diskBytes > 0 && p.MaxDiskGb is not null;
            var usage = await UsageAsync(level.GroupId, checkDisk, excludeJobId, ct);
            if (vms > 0 && p.MaxVms is { } maxVms && usage.Vms + vms > maxVms)
            {
                return Exceeded(level, "virtual machines", usage.Vms, vms, maxVms, "");
            }
            if (processors > 0 && p.MaxProcessors is { } maxCpu && usage.Processors + processors > maxCpu)
            {
                return Exceeded(level, "virtual processors", usage.Processors, processors, maxCpu, "");
            }
            if (memoryMb > 0 && p.MaxMemoryMb is { } maxMem && usage.MemoryMb + memoryMb > maxMem)
            {
                return Exceeded(level, "memory", usage.MemoryMb, memoryMb, maxMem, " MB");
            }
            if (checkDisk)
            {
                if (usage.DiskUnknownVms > 0)
                {
                    return new PolicyViolation("quota_usage_unknown",
                        $"The disk space used by folder '{level.Name}' cannot be verified: the size of {usage.DiskUnknownVms} VM(s) is unknown because their host is not reachable.");
                }
                var usedGb = (usage.DiskBytes + Gb - 1) / Gb;
                var requestedGb = (diskBytes + Gb - 1) / Gb;
                if (usage.DiskBytes + diskBytes > p.MaxDiskGb!.Value * Gb)
                {
                    return Exceeded(level, "virtual disk space", usedGb, requestedGb, p.MaxDiskGb.Value, " GB");
                }
            }
            if (networks > 0 && p.MaxNetworks is { } maxNet && usage.Networks + networks > maxNet)
            {
                return Exceeded(level, "networks", usage.Networks, networks, maxNet, "");
            }
        }
        return null;
    }

    private static PolicyViolation Exceeded(PolicyLevel level, string what, long used, long requested, long limit, string unit) =>
        new("quota_exceeded", $"Folder '{level.Name}' is limited to {limit}{unit} of {what}: {used}{unit} in use, {requested}{unit} more requested.");

    // ------------------------------------------------------------------ checks

    /// <summary>
    /// A VM about to be created in a folder: network and storage must be allowed, quotas must have room. Returns the
    /// request to execute (the location is set when the folder restricts storage and none was chosen).
    /// </summary>
    public async Task<(PolicyViolation? Violation, NewVmRequest Spec)> CheckNewVmAsync(Guid groupId, Guid hostId, NewVmRequest spec, Guid? excludeJobId, CancellationToken ct)
    {
        var (tree, groups) = await LoadAsync(ct);
        var chainGroups = tree.Chain(groupId);
        var levels = Levels(chainGroups, groups);
        if (levels.Count == 0)
        {
            return (null, spec);
        }
        if (spec.SwitchName is { Length: > 0 } sw && await AllowedSwitchesAsync(levels, chainGroups, hostId, ct) is { } allowed && !allowed.Contains(sw))
        {
            return (new PolicyViolation("network_not_allowed", $"The network '{sw}' is not among the networks allowed for this folder."), spec);
        }
        var rules = StorageRules(levels, hostId);
        if (rules.Count > 0)
        {
            var location = spec.Location ?? rules[0].FirstOrDefault(r => StorageAllows(rules, r));
            if (location is null || !StorageAllows(rules, location))
            {
                return (new PolicyViolation("storage_not_allowed", spec.Location is null
                    ? "This folder has no storage path on this host: VMs cannot be created here."
                    : "The location is outside the storage paths allowed for this folder."), spec);
            }
            spec = spec with { Location = location };
        }
        if (spec.DynamicMemory && spec.MemoryMaximumMb is null && levels.Any(l => l.Policy.MaxMemoryMb is not null))
        {
            return (new PolicyViolation("quota_memory_maximum_required",
                "This folder has a memory limit: with dynamic memory set the maximum memory of the VM (it counts against the limit)."), spec);
        }
        var violation = await CheckQuotasAsync(levels, 1, spec.ProcessorCount, NewVmFootprintMb(spec), (spec.DiskSizeGb + spec.TemplateExtraDiskGb) * Gb, 0, excludeJobId, ct);
        return (violation, spec);
    }

    /// <summary>
    /// A configuration change of a VM that lives in folders with limits: new network connections must be allowed, growth
    /// (vCPU, memory, disks) must fit the quotas, and a new disk goes to the VM's own folder when storage is restricted.
    /// A folder chosen by the client is never accepted: NewDisk.Folder is always set here.
    /// </summary>
    public async Task<(PolicyViolation? Violation, VmConfigChange Change)> CheckConfigAsync(VirtualMachine vm, VmConfigChange change, CancellationToken ct)
    {
        if (change.NewDisk is { Folder: not null } withFolder)
        {
            change = change with { NewDisk = withFolder with { Folder = null } };
        }
        var (tree, groups) = await LoadAsync(ct);
        var chainGroups = tree.ChainSet(tree.GroupsOf(vm.Id));
        var levels = Levels(chainGroups, groups);
        if (levels.Count == 0)
        {
            return (null, change);
        }
        var switches = (change.NetworkAdapterChanges ?? []).Select(n => n.SwitchName).Append(change.AddNetworkAdapterSwitch)
            .Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).Distinct(StringComparer.Ordinal).ToList();
        if (switches.Count > 0 && await AllowedSwitchesAsync(levels, chainGroups, vm.HostId, ct) is { } allowed && switches.FirstOrDefault(s => !allowed.Contains(s)) is { } refused)
        {
            return (new PolicyViolation("network_not_allowed", $"The network '{refused}' is not among the networks allowed for the folder of this VM."), change);
        }
        var rules = StorageRules(levels, vm.HostId);
        if (change.NewDisk is { } newDisk && rules.Count > 0)
        {
            var home = vm.ConfigurationPath is { Length: > 0 } cp && HostPaths.IsValidLocalFolder(cp) ? HostPaths.Normalize(cp) : null;
            if (home is null || !StorageAllows(rules, home))
            {
                return (new PolicyViolation("storage_not_allowed",
                    "The VM is not stored inside the storage paths allowed for its folder: a new disk cannot be added from the portal."), change);
            }
            change = change with { NewDisk = newDisk with { Folder = home + @"\Virtual Hard Disks" } };
        }

        var processors = change.ProcessorCount is { } cpu ? Math.Max(0, cpu - (vm.ProcessorCount ?? 0)) : 0;
        long memory = 0;
        if (change.MemoryStartupMb is not null || change.DynamicMemoryEnabled is not null || change.MemoryMaximumMb is not null)
        {
            var dynamic = change.DynamicMemoryEnabled ?? vm.DynamicMemoryEnabled ?? false;
            var startup = change.MemoryStartupMb ?? vm.MemoryStartupMb ?? 0;
            var maximum = change.MemoryMaximumMb ?? vm.MemoryMaximumMb;
            if (dynamic && maximum is null && levels.Any(l => l.Policy.MaxMemoryMb is not null))
            {
                return (new PolicyViolation("quota_memory_maximum_required",
                    "The folder of this VM has a memory limit: with dynamic memory set the maximum memory of the VM (it counts against the limit)."), change);
            }
            var after = dynamic ? Math.Max(startup, maximum ?? startup) : startup;
            memory = Math.Max(0, after - MemoryFootprintMb(vm));
        }
        long disk = change.NewDisk is { } nd ? nd.SizeGb * Gb : 0;
        if (change.ExpandDisk is { } expand)
        {
            var current = levels.Any(l => l.Policy.MaxDiskGb is not null)
                ? (await DisksOfHostAsync(vm.HostId, ct))?.FirstOrDefault(d => d.VmId == vm.HyperVId && string.Equals(d.Path, expand.Path, StringComparison.OrdinalIgnoreCase))?.SizeBytes
                : null;
            disk += Math.Max(0, expand.NewSizeGb * Gb - (current ?? 0));
        }
        if (processors == 0 && memory == 0 && disk == 0)
        {
            return (null, change);
        }
        return (await CheckQuotasAsync(levels, 0, processors, memory, disk, 0, null, ct), change);
    }

    /// <summary>
    /// A VM moved to another host and/or folder (protocol 9): the folders it belongs to must allow the destination host
    /// (when they list hosts) and the destination folder (when they restrict storage on that host).
    /// </summary>
    public async Task<PolicyViolation?> CheckMoveAsync(VirtualMachine vm, Guid destinationHostId, string? destinationPath, CancellationToken ct)
    {
        var (tree, groups) = await LoadAsync(ct);
        var levels = Levels(tree.ChainSet(tree.GroupsOf(vm.Id)), groups);
        if (levels.Count == 0)
        {
            return null;
        }
        if (destinationHostId != vm.HostId && levels.Any(l => l.Policy.Hosts is { Count: > 0 }) && DelegatedHosts(levels)?.Contains(destinationHostId) != true)
        {
            return new PolicyViolation("host_not_allowed", "The folder of this VM does not allow the destination host.");
        }
        var rules = StorageRules(levels, destinationHostId);
        if (rules.Count > 0)
        {
            var target = destinationPath ?? (vm.ConfigurationPath is { Length: > 0 } cp && HostPaths.IsValidLocalFolder(cp) ? HostPaths.Normalize(cp) : null);
            if (target is null || !StorageAllows(rules, target))
            {
                return new PolicyViolation("storage_not_allowed", "The destination folder is not among the storage paths allowed for the folder of this VM.");
            }
        }
        return null;
    }

    /// <summary>A private network about to be created for a folder.</summary>
    public async Task<PolicyViolation?> CheckNewSwitchAsync(Guid groupId, Guid? excludeJobId, CancellationToken ct) =>
        await CheckQuotasAsync(await ChainAsync(groupId, ct), 0, 0, 0, 0, 1, excludeJobId, ct);
}
