using System.Text.RegularExpressions;
using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Data;
using Microsoft.EntityFrameworkCore;

namespace HvPortal.Gateway.VmGroups;

/// <summary>
/// Imports the native VM groups of a host that the portal does not own yet (ADR-017):
///  - management collection M -> root portal group "M"; each VM collection inside it becomes a sub-group (a VM collection
///    with the same name as M contributes its VMs to "M" itself, which is how the mirroring represents direct VMs);
///  - remaining VM collections -> root portal groups.
/// The native groups are then linked (owned by the portal) and the mirroring takes over.
/// An existing portal group with the same name and parent is reused only when no role assignment applies to it (or to
/// its ancestors); otherwise a new group "&lt;name&gt; (&lt;host&gt;)" is created. Imported groups carry no role assignments, so
/// importing never changes who can access a VM.
/// </summary>
public static partial class VmGroupImporter
{
    public sealed record ImportResult(int GroupsCreated, int GroupsReused, int VmsAdded, int Linked, IReadOnlyList<string> Warnings);

    [GeneratedRegex(@"[^\p{L}\p{N} ._()\-]", RegexOptions.CultureInvariant)]
    private static partial Regex InvalidNameChars();

    public static string PortalName(string nativeName)
    {
        var s = InvalidNameChars().Replace(nativeName.Trim(), "_");
        s = s.TrimStart(' ', '.', '_', '(', ')', '-');
        if (s.Length == 0)
        {
            s = "VM group";
        }
        return s.Length > 64 ? s[..64].TrimEnd() : s;
    }

    public static async Task<ImportResult> ImportAsync(PortalDbContext db, ManagedHost host, IReadOnlyList<NativeVmGroup> native, TimeProvider time, CancellationToken ct)
    {
        var warnings = new List<string>();
        var linked = await db.HostVmGroupLinks.Where(l => l.HostId == host.Id).Select(l => l.NativeId).ToListAsync(ct);
        var linkedSet = linked.ToHashSet();
        var unlinked = native.Where(n => !linkedSet.Contains(n.Id)).ToList();
        var vmByHyperV = (await db.VirtualMachines.Where(v => v.HostId == host.Id && !v.IsMissing).Select(v => new { v.Id, v.HyperVId }).ToListAsync(ct))
            .GroupBy(v => v.HyperVId).ToDictionary(g => g.Key, g => g.First().Id);
        var groups = await db.LogicalGroups.ToListAsync(ct);
        var groupAssignments = (await db.RoleAssignments.Where(a => a.ScopeType == ScopeType.Group).Select(a => a.ScopeId).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingMembers = (await db.LogicalGroupMembers.ToListAsync(ct)).Select(m => (m.GroupId, m.VmId)).ToHashSet();
        var reservedLinks = (await db.HostVmGroupLinks.Where(l => l.HostId == host.Id).Select(l => new { l.GroupId, l.Role }).ToListAsync(ct))
            .Select(l => (l.GroupId, l.Role)).ToHashSet();
        int created = 0, reused = 0, vmsAdded = 0, links = 0;

        bool HasAssignments(LogicalGroup g)
        {
            for (LogicalGroup? cur = g; cur is not null; cur = cur.ParentId is { } p ? groups.FirstOrDefault(x => x.Id == p) : null)
            {
                if (groupAssignments.Contains(cur.Id.ToString("D")))
                {
                    return true;
                }
            }
            return false;
        }

        LogicalGroup Target(string nativeName, Guid? parentId, string role)
        {
            var name = PortalName(nativeName);
            var same = groups.FirstOrDefault(g => g.ParentId == parentId && string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
            if (same is not null && !HasAssignments(same) && !reservedLinks.Contains((same.Id, role)))
            {
                reused++;
                return same;
            }
            if (same is not null)
            {
                var suffix = $" ({host.DisplayName})";
                name = (name.Length + suffix.Length > 64 ? name[..(64 - suffix.Length)] : name) + suffix;
                for (var i = 2; groups.Any(g => g.ParentId == parentId && string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)); i++)
                {
                    name = PortalName($"{nativeName} {i}");
                }
            }
            var g = new LogicalGroup { Name = name, ParentId = parentId, CreatedAt = time.GetUtcNow(), Description = $"Imported from Hyper-V VM group '{nativeName}' on {host.DisplayName}." };
            db.LogicalGroups.Add(g);
            groups.Add(g);
            created++;
            return g;
        }

        void AddVms(LogicalGroup g, NativeVmGroup n)
        {
            foreach (var hv in n.VmIds)
            {
                if (vmByHyperV.TryGetValue(hv, out var vmId) && existingMembers.Add((g.Id, vmId)))
                {
                    db.LogicalGroupMembers.Add(new LogicalGroupMember { GroupId = g.Id, VmId = vmId });
                    vmsAdded++;
                }
            }
        }

        void Link(LogicalGroup g, string role, NativeVmGroup n)
        {
            reservedLinks.Add((g.Id, role));
            db.HostVmGroupLinks.Add(new HostVmGroupLink { HostId = host.Id, GroupId = g.Id, Role = role, NativeId = n.Id, CreatedAt = time.GetUtcNow() });
            linkedSet.Add(n.Id);
            links++;
        }

        var byId = unlinked.ToDictionary(n => n.Id);
        foreach (var m in unlinked.Where(n => n.GroupType == VmGroupTypes.Management).OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
        {
            var parent = Target(m.Name, null, VmGroupPlanner.RoleMgmt);
            Link(parent, VmGroupPlanner.RoleMgmt, m);
            foreach (var childId in m.GroupIds)
            {
                if (!byId.TryGetValue(childId, out var c) || c.GroupType != VmGroupTypes.Vm)
                {
                    continue;
                }
                if (linkedSet.Contains(c.Id))
                {
                    warnings.Add($"'{c.Name}' is in more than one management group: kept under the first one.");
                    continue;
                }
                var target = string.Equals(PortalName(c.Name), parent.Name, StringComparison.OrdinalIgnoreCase) && !reservedLinks.Contains((parent.Id, VmGroupPlanner.RoleVms))
                    ? parent
                    : Target(c.Name, parent.Id, VmGroupPlanner.RoleVms);
                AddVms(target, c);
                Link(target, VmGroupPlanner.RoleVms, c);
            }
        }
        foreach (var c in unlinked.Where(n => n.GroupType == VmGroupTypes.Vm && !linkedSet.Contains(n.Id)).OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
        {
            var g = Target(c.Name, null, VmGroupPlanner.RoleVms);
            AddVms(g, c);
            Link(g, VmGroupPlanner.RoleVms, c);
        }
        var unknownVms = unlinked.SelectMany(n => n.VmIds).Distinct().Count(hv => !vmByHyperV.ContainsKey(hv));
        if (unknownVms > 0)
        {
            warnings.Add($"{unknownVms} VM(s) of the native groups are not in the portal inventory and were skipped.");
        }
        await db.SaveChangesAsync(ct);
        return new ImportResult(created, reused, vmsAdded, links, warnings);
    }
}
