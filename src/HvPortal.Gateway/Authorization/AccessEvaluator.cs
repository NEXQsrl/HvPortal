using HvPortal.Gateway.Data;
using Microsoft.EntityFrameworkCore;

namespace HvPortal.Gateway.Authorization;

public readonly record struct ResourceRef(ScopeType Type, string Id)
{
    public static readonly ResourceRef Global = new(ScopeType.Global, "");
    public static ResourceRef Vm(Guid id) => new(ScopeType.Vm, id.ToString("D"));
    public static ResourceRef Host(Guid id) => new(ScopeType.Host, id.ToString("D"));
    public static ResourceRef Group(Guid id) => new(ScopeType.Group, id.ToString("D"));
    public override string ToString() => Type == ScopeType.Global ? "global" : $"{Type.ToString().ToLowerInvariant()}:{Id}";
}

/// <summary>
/// RBAC evaluation. Rules (see docs/security-model.md#rbac):
///  1. Default deny.
///  2. The effective scopes of a resource are the resource itself plus its ancestors:
///     VM -> Host -> Global, and VM -> each logical Group containing it -> Global. Host -> Global.
///  3. An assignment applies if its scope is one of the effective scopes and its role contains the permission.
///  4. Any applicable Deny wins over any Allow, regardless of level.
///  5. Platform permissions (Permissions.Definition.Scopes == [Global]) are only granted by Global assignments.
///  6. Disabled users have no permissions.
/// The evaluator is scoped per request/job execution; it never caches across requests, so revocations apply
/// to the next request and to queued jobs at execution time.
/// </summary>
public sealed class AccessEvaluator
{
    private readonly PortalDbContext _db;
    private readonly Dictionary<Guid, List<EffectiveAssignment>> _cache = [];
    private Dictionary<Guid, Guid>? _vmHosts;
    private ILookup<Guid, Guid>? _vmGroups;

    public AccessEvaluator(PortalDbContext db)
    {
        _db = db;
    }

    private sealed record EffectiveAssignment(ScopeType ScopeType, string ScopeId, AssignmentEffect Effect, IReadOnlySet<string> Permissions);

    public async Task<bool> HasAsync(Guid userId, string permission, ResourceRef resource, CancellationToken ct = default)
    {
        var assignments = await LoadAsync(userId, ct);
        if (assignments.Count == 0)
        {
            return false;
        }
        var definition = Permissions.Find(permission);
        if (definition is null)
        {
            return false;
        }
        var globalOnly = definition.Scopes is [ScopeType.Global];
        var scopes = globalOnly ? [ResourceRef.Global] : await EffectiveScopesAsync(resource, ct);
        return Evaluate(assignments, permission, scopes);
    }

    /// <summary>VM ids (portal ids) on which the user holds <paramref name="permission"/>.</summary>
    public async Task<HashSet<Guid>> FilterVmsAsync(Guid userId, string permission, IEnumerable<Guid> vmIds, CancellationToken ct = default)
    {
        var result = new HashSet<Guid>();
        var assignments = await LoadAsync(userId, ct);
        if (assignments.Count == 0)
        {
            return result;
        }
        await EnsureTopologyAsync(ct);
        foreach (var id in vmIds)
        {
            if (Evaluate(assignments, permission, VmScopes(id)))
            {
                result.Add(id);
            }
        }
        return result;
    }

    /// <summary>True if the user can read the host itself or at least one VM on it.</summary>
    public async Task<bool> CanSeeHostAsync(Guid userId, Guid hostId, CancellationToken ct = default)
    {
        if (await HasAsync(userId, Permissions.InventoryRead, ResourceRef.Host(hostId), ct))
        {
            return true;
        }
        await EnsureTopologyAsync(ct);
        var vms = _vmHosts!.Where(kv => kv.Value == hostId).Select(kv => kv.Key);
        return (await FilterVmsAsync(userId, Permissions.InventoryRead, vms, ct)).Count > 0;
    }

    public async Task<bool> IsPrivilegedAsync(Guid userId, CancellationToken ct = default)
    {
        foreach (var p in Permissions.PrivilegedNames)
        {
            if (await HasAsync(userId, p, ResourceRef.Global, ct))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Global permissions the user holds (UI hint only; never a substitute for a server-side check).</summary>
    public async Task<IReadOnlyList<string>> GlobalPermissionsAsync(Guid userId, CancellationToken ct = default)
    {
        var list = new List<string>();
        foreach (var p in Permissions.Names)
        {
            if (await HasAsync(userId, p, ResourceRef.Global, ct))
            {
                list.Add(p);
            }
        }
        return list;
    }

    /// <summary>True when the user holds every permission of <paramref name="permissions"/> on <paramref name="scope"/>.</summary>
    public async Task<bool> HoldsAllAsync(Guid userId, IEnumerable<string> permissions, ResourceRef scope, CancellationToken ct = default)
    {
        foreach (var p in permissions)
        {
            if (!await HasAsync(userId, p, scope, ct))
            {
                return false;
            }
        }
        return true;
    }

    public async Task<IReadOnlyList<ResourceRef>> EffectiveScopesAsync(ResourceRef resource, CancellationToken ct)
    {
        switch (resource.Type)
        {
            case ScopeType.Global:
                return [ResourceRef.Global];
            case ScopeType.Vm when Guid.TryParse(resource.Id, out var vmId):
                await EnsureTopologyAsync(ct);
                return VmScopes(vmId);
            default:
                // Host, Group, Network, BackupRepository, Template: the resource itself and Global.
                // Cluster -> Host inheritance arrives with cluster support (Milestone 3).
                return [resource, ResourceRef.Global];
        }
    }

    private List<ResourceRef> VmScopes(Guid vmId)
    {
        var scopes = new List<ResourceRef> { ResourceRef.Vm(vmId), ResourceRef.Global };
        if (_vmHosts!.TryGetValue(vmId, out var hostId))
        {
            scopes.Add(ResourceRef.Host(hostId));
        }
        foreach (var g in _vmGroups![vmId])
        {
            scopes.Add(ResourceRef.Group(g));
        }
        return scopes;
    }

    private static bool Evaluate(List<EffectiveAssignment> assignments, string permission, IReadOnlyList<ResourceRef> scopes)
    {
        var allowed = false;
        foreach (var a in assignments)
        {
            if (!a.Permissions.Contains(permission))
            {
                continue;
            }
            var applies = false;
            foreach (var s in scopes)
            {
                if (s.Type == a.ScopeType && string.Equals(s.Id, a.ScopeId, StringComparison.OrdinalIgnoreCase))
                {
                    applies = true;
                    break;
                }
            }
            if (!applies)
            {
                continue;
            }
            if (a.Effect == AssignmentEffect.Deny)
            {
                return false;
            }
            allowed = true;
        }
        return allowed;
    }

    private async Task<List<EffectiveAssignment>> LoadAsync(Guid userId, CancellationToken ct)
    {
        if (_cache.TryGetValue(userId, out var cached))
        {
            return cached;
        }
        var enabled = await _db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => (bool?)u.IsEnabled).FirstOrDefaultAsync(ct);
        if (enabled != true)
        {
            return _cache[userId] = [];
        }
        var principal = userId.ToString("D");
        var rows = await _db.RoleAssignments.AsNoTracking()
            .Where(a => a.PrincipalType == "user" && a.PrincipalId == principal)
            .Select(a => new { a.ScopeType, a.ScopeId, a.Effect, a.RoleId })
            .ToListAsync(ct);
        var roleIds = rows.Select(r => r.RoleId).Distinct().ToList();
        var perms = (await _db.RolePermissions.AsNoTracking().Where(p => roleIds.Contains(p.RoleId)).ToListAsync(ct))
            .GroupBy(p => p.RoleId)
            .ToDictionary(g => g.Key, g => (IReadOnlySet<string>)g.Select(x => x.Permission).ToHashSet(StringComparer.Ordinal));
        var list = rows.Select(r => new EffectiveAssignment(r.ScopeType, r.ScopeId, r.Effect,
            perms.TryGetValue(r.RoleId, out var p) ? p : new HashSet<string>())).ToList();
        return _cache[userId] = list;
    }

    private async Task EnsureTopologyAsync(CancellationToken ct)
    {
        if (_vmHosts is not null)
        {
            return;
        }
        _vmHosts = await _db.VirtualMachines.AsNoTracking().ToDictionaryAsync(v => v.Id, v => v.HostId, ct);
        _vmGroups = (await _db.LogicalGroupMembers.AsNoTracking().ToListAsync(ct)).ToLookup(m => m.VmId, m => m.GroupId);
    }
}
