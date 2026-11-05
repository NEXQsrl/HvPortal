using HvPortal.Gateway.Data;
using Microsoft.EntityFrameworkCore;

namespace HvPortal.Gateway.Authorization;

/// <summary>
/// Snapshot of the logical group tree and memberships, used to validate tree changes (cycles, depth) and to compute
/// how a change alters the group scopes that apply to each VM (anti-escalation, see <see cref="CheckAccessChangeAsync"/>).
/// </summary>
public sealed class GroupTree
{
    private readonly Dictionary<Guid, Guid?> _parents;
    private readonly Dictionary<Guid, HashSet<Guid>> _vmGroups;

    private GroupTree(Dictionary<Guid, Guid?> parents, Dictionary<Guid, HashSet<Guid>> vmGroups)
    {
        _parents = parents;
        _vmGroups = vmGroups;
    }

    public static async Task<GroupTree> LoadAsync(PortalDbContext db, CancellationToken ct)
    {
        var parents = await db.LogicalGroups.AsNoTracking().ToDictionaryAsync(g => g.Id, g => g.ParentId, ct);
        var members = await db.LogicalGroupMembers.AsNoTracking().ToListAsync(ct);
        var vmGroups = members.GroupBy(m => m.VmId).ToDictionary(g => g.Key, g => g.Select(x => x.GroupId).ToHashSet());
        return new GroupTree(parents, vmGroups);
    }

    public bool Exists(Guid groupId) => _parents.ContainsKey(groupId);

    public IReadOnlyCollection<Guid> GroupsOf(Guid vmId) => _vmGroups.TryGetValue(vmId, out var s) ? s : [];

    public IEnumerable<Guid> MembersOf(IReadOnlySet<Guid> groups) =>
        _vmGroups.Where(kv => kv.Value.Overlaps(groups)).Select(kv => kv.Key);

    /// <summary>The group and its ancestors, nearest first (cycle-safe).</summary>
    public List<Guid> Chain(Guid groupId, IReadOnlyDictionary<Guid, Guid?>? overrides = null)
    {
        var list = new List<Guid>();
        Guid? current = groupId;
        while (current is { } g && list.Count <= AccessEvaluator.MaxGroupDepth && !list.Contains(g))
        {
            list.Add(g);
            current = overrides is not null && overrides.TryGetValue(g, out var o) ? o : _parents.GetValueOrDefault(g);
        }
        return list;
    }

    public HashSet<Guid> ChainSet(IEnumerable<Guid> groups, IReadOnlyDictionary<Guid, Guid?>? overrides = null)
    {
        var set = new HashSet<Guid>();
        foreach (var g in groups)
        {
            set.UnionWith(Chain(g, overrides));
        }
        return set;
    }

    /// <summary>The group and every group below it.</summary>
    public HashSet<Guid> Subtree(Guid groupId)
    {
        var result = new HashSet<Guid> { groupId };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var (id, parent) in _parents)
            {
                if (parent is { } p && result.Contains(p) && result.Add(id))
                {
                    changed = true;
                }
            }
        }
        return result;
    }

    /// <summary>Number of levels from the root to the group (root group = 1).</summary>
    public int Depth(Guid groupId) => Chain(groupId).Count;

    /// <summary>Height of the subtree rooted at the group (leaf = 1).</summary>
    public int Height(Guid groupId)
    {
        var children = _parents.Where(kv => kv.Value == groupId).Select(kv => kv.Key).ToList();
        return 1 + (children.Count == 0 ? 0 : children.Max(Height));
    }

    /// <summary>
    /// A membership or tree change alters which group-scoped assignments apply to each VM:
    /// Allow assignments on newly inherited groups start granting access; Deny assignments on groups no longer
    /// inherited stop restricting it. Either is equivalent to creating or removing a role assignment, so:
    /// when any such assignment exists, the actor needs roles.manage and must already hold every permission
    /// of those roles on each affected resource. Returns null when allowed, otherwise an error code.
    /// </summary>
    public static async Task<string?> CheckAccessChangeAsync(PortalDbContext db, AccessEvaluator access, Guid actor,
        IReadOnlyList<(ResourceRef Resource, HashSet<Guid> Gained, HashSet<Guid> Lost)> changes, CancellationToken ct)
    {
        var gained = changes.SelectMany(c => c.Gained).Distinct().Select(g => g.ToString("D")).ToList();
        var lost = changes.SelectMany(c => c.Lost).Distinct().Select(g => g.ToString("D")).ToList();
        if (gained.Count == 0 && lost.Count == 0)
        {
            return null;
        }
        var assignments = await db.RoleAssignments.AsNoTracking()
            .Where(a => a.ScopeType == ScopeType.Group
                        && ((a.Effect == AssignmentEffect.Allow && gained.Contains(a.ScopeId)) || (a.Effect == AssignmentEffect.Deny && lost.Contains(a.ScopeId))))
            .Select(a => new { a.ScopeId, a.Effect, a.RoleId })
            .ToListAsync(ct);
        if (assignments.Count == 0)
        {
            return null;
        }
        if (!await access.HasAsync(actor, Permissions.RolesManage, ResourceRef.Global, ct))
        {
            return "group_access_change_requires_roles_manage";
        }
        var roleIds = assignments.Select(a => a.RoleId).Distinct().ToList();
        var rolePerms = (await db.RolePermissions.AsNoTracking().Where(p => roleIds.Contains(p.RoleId)).ToListAsync(ct))
            .GroupBy(p => p.RoleId).ToDictionary(g => g.Key, g => g.Select(x => x.Permission).ToList());
        foreach (var c in changes)
        {
            var gainedIds = c.Gained.Select(g => g.ToString("D")).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var lostIds = c.Lost.Select(g => g.ToString("D")).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var perms = assignments
                .Where(a => (a.Effect == AssignmentEffect.Allow && gainedIds.Contains(a.ScopeId)) || (a.Effect == AssignmentEffect.Deny && lostIds.Contains(a.ScopeId)))
                .SelectMany(a => rolePerms.GetValueOrDefault(a.RoleId) ?? [])
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (perms.Count > 0 && !await access.HoldsAllAsync(actor, perms, c.Resource, ct))
            {
                return "permission_amplification";
            }
        }
        return null;
    }
}
