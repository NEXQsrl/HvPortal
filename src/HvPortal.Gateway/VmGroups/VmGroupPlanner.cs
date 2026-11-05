using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HvPortal.Contracts.HyperV;

namespace HvPortal.Gateway.VmGroups;

/// <summary>
/// Desired native Hyper-V VM groups of one host, derived from the portal group tree (ADR-017).
/// Hyper-V has two documented levels: VMCollectionType (contains VMs) and ManagementCollectionType (contains VM
/// collections). Mapping:
///  - a portal group with VMs on the host -> VM collection "&lt;name&gt;" with those VMs (key "&lt;id&gt;:vms");
///  - a portal group with VM collections below it -> management collection "&lt;name&gt;" (key "&lt;id&gt;:mgmt") containing
///    its own VM collection and the VM collections of all its descendants (deeper levels are flattened, because a
///    management collection cannot contain management collections).
/// Groups with no VM on the host are not created on that host.
/// </summary>
public static class VmGroupPlanner
{
    public const string RoleVms = "vms";
    public const string RoleMgmt = "mgmt";

    public sealed record GroupNode(Guid Id, Guid? ParentId, string Name);
    public sealed record Link(Guid GroupId, string Role, Guid NativeId);

    public static string Key(Guid groupId, string role) => $"{groupId:D}:{role}";

    /// <param name="hostVmIds">Portal VM id -> Hyper-V id, for the (non-missing) VMs of the host.</param>
    public static VmGroupSyncRequest Plan(IReadOnlyCollection<GroupNode> groups, IReadOnlyCollection<(Guid GroupId, Guid VmId)> members,
        IReadOnlyDictionary<Guid, Guid> hostVmIds, IReadOnlyCollection<Link> links)
    {
        var children = groups.ToLookup(g => g.ParentId);
        var direct = members.Where(m => hostVmIds.ContainsKey(m.VmId))
            .GroupBy(m => m.GroupId)
            .ToDictionary(g => g.Key, g => g.Select(m => hostVmIds[m.VmId]).Distinct().Order().ToList());
        var linkByKey = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var l in links)
        {
            linkByKey.TryAdd(Key(l.GroupId, l.Role), l.NativeId);
        }

        var items = new List<VmGroupSyncItem>();
        foreach (var g in groups.OrderBy(g => g.Name, StringComparer.Ordinal).ThenBy(g => g.Id))
        {
            if (!OperationValidation.IsValidVmGroupName(g.Name))
            {
                continue; // portal names use the same character set; defensive only
            }
            var hasVms = direct.ContainsKey(g.Id);
            if (hasVms)
            {
                var key = Key(g.Id, RoleVms);
                items.Add(new VmGroupSyncItem(key, g.Name, VmGroupTypes.Vm, linkByKey.TryGetValue(key, out var id) ? id : null, direct[g.Id], []));
            }
            var below = new List<string>();
            var stack = new Stack<Guid>(children[g.Id].Select(c => c.Id));
            var seen = new HashSet<Guid> { g.Id };
            while (stack.Count > 0)
            {
                var d = stack.Pop();
                if (!seen.Add(d))
                {
                    continue;
                }
                if (direct.ContainsKey(d))
                {
                    below.Add(Key(d, RoleVms));
                }
                foreach (var c in children[d])
                {
                    stack.Push(c.Id);
                }
            }
            if (below.Count > 0)
            {
                var key = Key(g.Id, RoleMgmt);
                var memberKeys = (hasVms ? new[] { Key(g.Id, RoleVms) } : []).Concat(below.Order(StringComparer.Ordinal)).ToList();
                items.Add(new VmGroupSyncItem(key, g.Name, VmGroupTypes.Management, linkByKey.TryGetValue(key, out var id) ? id : null, [], memberKeys));
            }
        }
        // Items whose member collection was dropped (invalid name) must not reference it.
        var keys = items.Where(i => i.GroupType == VmGroupTypes.Vm).Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
        items = items.Select(i => i with { MemberKeys = i.MemberKeys.Where(keys.Contains).ToList() })
            .Where(i => i.GroupType == VmGroupTypes.Vm || i.MemberKeys.Count > 0).ToList();
        return new VmGroupSyncRequest(items, links.Select(l => l.NativeId).Distinct().Order().ToList());
    }

    public static string Hash(VmGroupSyncRequest plan) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(plan)))).ToLowerInvariant();
}
