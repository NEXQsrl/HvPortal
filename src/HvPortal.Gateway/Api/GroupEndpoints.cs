using HvPortal.Gateway.Audit;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Realtime;
using HvPortal.Gateway.Security;
using Microsoft.EntityFrameworkCore;

namespace HvPortal.Gateway.Api;

/// <summary>
/// Portal VM groups (folders). Groups nest; a VM can belong to several groups. Role assignments on a group apply
/// to the VMs of the group and of every group below it. Because membership and tree changes alter who can access a VM,
/// they go through <see cref="GroupTree.CheckAccessChangeAsync"/> (anti-escalation).
/// Native Hyper-V VM groups (VMGroup) are a different, host-local feature and are not modified here.
/// </summary>
public static class GroupEndpoints
{
    public const int MaxGroups = 2000;
    public const int MaxMembersPerRequest = 500;

    public sealed record CreateGroupRequest(string Name, string? Description, Guid? ParentId);
    public sealed record UpdateGroupRequest(string? Name, string? Description, Guid? ParentId, bool? MoveToRoot);
    public sealed record MembersRequest(Guid[] VmIds);

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/groups").WithTags("Groups");
        g.MapGet("/", ListAsync);
        g.MapPost("/", CreateAsync);
        g.MapMethods("/{id:guid}", ["PATCH"], UpdateAsync);
        g.MapDelete("/{id:guid}", DeleteAsync);
        g.MapPost("/{id:guid}/members", AddMembersAsync);
        g.MapPost("/{id:guid}/members/remove", RemoveMembersAsync);
    }

    private static async Task<IResult> ListAsync(HttpContext http, PortalDbContext db, AccessEvaluator access)
    {
        var uid = http.User.UserId();
        var ct = http.RequestAborted;
        var groups = await db.LogicalGroups.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct);
        var members = await db.LogicalGroupMembers.AsNoTracking().ToListAsync(ct);
        var visibleVms = await access.FilterVmsAsync(uid, Permissions.InventoryRead, members.Select(m => m.VmId).Distinct(), ct);
        var seeAll = await access.HasAsync(uid, Permissions.GroupsManage, ResourceRef.Global, ct)
                     || await access.HasAsync(uid, Permissions.InventoryRead, ResourceRef.Global, ct);
        var visible = new HashSet<Guid>();
        if (seeAll)
        {
            visible.UnionWith(groups.Select(x => x.Id));
        }
        else
        {
            var tree = await GroupTree.LoadAsync(db, ct);
            foreach (var grp in groups)
            {
                // A group is shown when the user can read it (assignment on it or an ancestor) or sees a VM inside it;
                // its ancestors are shown too so the tree path is complete.
                var readable = await access.HasAsync(uid, Permissions.InventoryRead, ResourceRef.Group(grp.Id), ct)
                               || members.Any(m => m.GroupId == grp.Id && visibleVms.Contains(m.VmId));
                if (readable)
                {
                    visible.UnionWith(tree.Chain(grp.Id));
                    if (await access.HasAsync(uid, Permissions.InventoryRead, ResourceRef.Group(grp.Id), ct))
                    {
                        visible.UnionWith(tree.Subtree(grp.Id));
                    }
                }
            }
        }
        var canManage = await access.HasAsync(uid, Permissions.GroupsManage, ResourceRef.Global, ct);
        return Results.Ok(new
        {
            canManage,
            groups = groups.Where(x => visible.Contains(x.Id)).Select(x => new
            {
                x.Id,
                parentId = x.ParentId is { } p && visible.Contains(p) ? x.ParentId : null,
                x.Name,
                x.Description,
                x.CreatedAt,
                vmIds = members.Where(m => m.GroupId == x.Id && visibleVms.Contains(m.VmId)).Select(m => m.VmId),
            }),
        });
    }

    private static async Task<IResult?> ValidateNameAsync(PortalDbContext db, string name, Guid? parentId, Guid? self, CancellationToken ct)
    {
        if (!ApiSupport.DisplayNameRegex().IsMatch(name))
        {
            return ApiSupport.BadRequest("invalid_group_name", "1-64 characters: letters, digits, space, . _ ( ) -");
        }
        var siblings = await db.LogicalGroups.AsNoTracking().Where(x => x.ParentId == parentId && x.Id != self).Select(x => x.Name).ToListAsync(ct);
        return siblings.Contains(name, StringComparer.OrdinalIgnoreCase) ? ApiSupport.Conflict("group_exists", "A group with this name already exists here.") : null;
    }

    private static async Task<IResult> CreateAsync(CreateGroupRequest req, HttpContext http, PortalDbContext db, TimeProvider time, IRealtimeNotifier notifier)
    {
        if (await http.GuardAsync(Permissions.GroupsManage, ResourceRef.Global, "groups.create") is { } denied)
        {
            return denied;
        }
        var ct = http.RequestAborted;
        var name = req.Name?.Trim() ?? "";
        if (await ValidateNameAsync(db, name, req.ParentId, null, ct) is { } invalid)
        {
            return invalid;
        }
        var (description, ok) = InventoryManagementEndpoints.CleanDescription(req.Description);
        if (!ok)
        {
            return ApiSupport.BadRequest("description_too_long");
        }
        if (await db.LogicalGroups.CountAsync(ct) >= MaxGroups)
        {
            return ApiSupport.Conflict("too_many_groups");
        }
        if (req.ParentId is { } parentId)
        {
            var tree = await GroupTree.LoadAsync(db, ct);
            if (!tree.Exists(parentId))
            {
                return ApiSupport.BadRequest("unknown_parent");
            }
            if (tree.Depth(parentId) + 1 > AccessEvaluator.MaxGroupDepth)
            {
                return ApiSupport.BadRequest("too_deep", $"At most {AccessEvaluator.MaxGroupDepth} levels.");
            }
        }
        // A new group is empty: creating it changes nobody's access.
        var group = new LogicalGroup { Name = name, Description = description, ParentId = req.ParentId, CreatedAt = time.GetUtcNow() };
        db.LogicalGroups.Add(group);
        await db.SaveChangesAsync(ct);
        await http.WriteAuditAsync("groups.create", resourceType: "group", resourceId: group.Id.ToString("D"), details: new { group.Name, group.ParentId });
        await notifier.GroupsChangedAsync();
        return Results.Ok(new { group.Id });
    }

    private static async Task<IResult> UpdateAsync(Guid id, UpdateGroupRequest req, HttpContext http, PortalDbContext db, AccessEvaluator access, IRealtimeNotifier notifier)
    {
        if (await http.GuardAsync(Permissions.GroupsManage, ResourceRef.Global, "groups.update") is { } denied)
        {
            return denied;
        }
        var ct = http.RequestAborted;
        var group = await db.LogicalGroups.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (group is null)
        {
            return ApiSupport.NotFound();
        }
        var before = new { group.Name, group.Description, group.ParentId };
        var newParent = req.MoveToRoot == true ? null : req.ParentId ?? group.ParentId;
        var moving = newParent != group.ParentId;
        var newName = req.Name?.Trim() ?? group.Name;
        if ((req.Name is not null || moving) && await ValidateNameAsync(db, newName, newParent, id, ct) is { } invalid)
        {
            return invalid;
        }
        if (moving)
        {
            var tree = await GroupTree.LoadAsync(db, ct);
            var subtree = tree.Subtree(id);
            if (newParent is { } p && (!tree.Exists(p) || subtree.Contains(p)))
            {
                return ApiSupport.BadRequest("invalid_parent", "A group cannot be moved into itself or one of its subgroups.");
            }
            if ((newParent is { } np ? tree.Depth(np) : 0) + tree.Height(id) > AccessEvaluator.MaxGroupDepth)
            {
                return ApiSupport.BadRequest("too_deep", $"At most {AccessEvaluator.MaxGroupDepth} levels.");
            }
            var overrides = new Dictionary<Guid, Guid?> { [id] = newParent };
            var changes = new List<(ResourceRef, HashSet<Guid>, HashSet<Guid>)>();
            foreach (var vm in tree.MembersOf(subtree))
            {
                var groups = tree.GroupsOf(vm);
                var beforeSet = tree.ChainSet(groups);
                var afterSet = tree.ChainSet(groups, overrides);
                changes.Add((ResourceRef.Vm(vm), [.. afterSet.Except(beforeSet)], [.. beforeSet.Except(afterSet)]));
            }
            // Role assignments made directly on groups of the moved subtree start inheriting nothing new for users,
            // but the subtree's VMs gain/lose the ancestors' assignments: checked above per VM.
            if (await GroupTree.CheckAccessChangeAsync(db, access, http.User.UserId(), changes, ct) is { } error)
            {
                await http.WriteAuditAsync("groups.move", AuditOutcome.Denied, "group", id.ToString("D"), new { reason = error, newParent });
                return ApiSupport.Forbidden(error, "Moving this group changes who can access its VMs through role assignments you cannot grant or remove.");
            }
            group.ParentId = newParent;
        }
        group.Name = newName;
        if (req.Description is not null)
        {
            var (description, ok) = InventoryManagementEndpoints.CleanDescription(req.Description);
            if (!ok)
            {
                return ApiSupport.BadRequest("description_too_long");
            }
            group.Description = description;
        }
        await db.SaveChangesAsync(ct);
        await http.WriteAuditAsync(moving ? "groups.move" : "groups.update", resourceType: "group", resourceId: id.ToString("D"),
            details: new { before, after = new { group.Name, group.Description, group.ParentId } });
        await notifier.GroupsChangedAsync();
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteAsync(Guid id, HttpContext http, PortalDbContext db, AccessEvaluator access, IRealtimeNotifier notifier)
    {
        if (await http.GuardAsync(Permissions.GroupsManage, ResourceRef.Global, "groups.delete") is { } denied)
        {
            return denied;
        }
        var ct = http.RequestAborted;
        var group = await db.LogicalGroups.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (group is null)
        {
            return ApiSupport.NotFound();
        }
        if (await db.LogicalGroups.AnyAsync(x => x.ParentId == id, ct))
        {
            return ApiSupport.Conflict("group_not_empty", "Move or delete the subgroups first.");
        }
        var scope = id.ToString("D");
        if (await db.RoleAssignments.AnyAsync(a => a.ScopeType == ScopeType.Group && a.ScopeId == scope, ct))
        {
            return ApiSupport.Conflict("group_has_assignments", "Remove the role assignments on this group first.");
        }
        var tree = await GroupTree.LoadAsync(db, ct);
        var memberIds = tree.MembersOf(new HashSet<Guid> { id }).ToList();
        if (await RemovalCheckAsync(db, access, http.User.UserId(), tree, id, memberIds, ct) is { } error)
        {
            await http.WriteAuditAsync("groups.delete", AuditOutcome.Denied, "group", scope, new { reason = error });
            return ApiSupport.Forbidden(error, "Deleting this group would lift deny assignments inherited by its VMs.");
        }
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LogicalGroupMembers.Where(m => m.GroupId == id).ExecuteDeleteAsync(ct);
        await db.LogicalGroups.Where(x => x.Id == id).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
        await http.WriteAuditAsync("groups.delete", resourceType: "group", resourceId: scope, details: new { group.Name, membersRemoved = memberIds.Count });
        await notifier.GroupsChangedAsync();
        return Results.NoContent();
    }

    private static async Task<string?> RemovalCheckAsync(PortalDbContext db, AccessEvaluator access, Guid actor, GroupTree tree, Guid groupId,
        IEnumerable<Guid> vmIds, CancellationToken ct)
    {
        var changes = new List<(ResourceRef, HashSet<Guid>, HashSet<Guid>)>();
        foreach (var vm in vmIds)
        {
            var groups = tree.GroupsOf(vm);
            var beforeSet = tree.ChainSet(groups);
            var afterSet = tree.ChainSet(groups.Where(g => g != groupId));
            changes.Add((ResourceRef.Vm(vm), [], [.. beforeSet.Except(afterSet)]));
        }
        return await GroupTree.CheckAccessChangeAsync(db, access, actor, changes, ct);
    }

    private static async Task<(List<Guid>? Ids, IResult? Error)> ResolveVmsAsync(MembersRequest req, HttpContext http, PortalDbContext db, AccessEvaluator access)
    {
        if (req.VmIds is not { Length: > 0 and <= MaxMembersPerRequest })
        {
            return (null, ApiSupport.BadRequest("invalid_vm_list", $"1-{MaxMembersPerRequest} VMs per request."));
        }
        var ids = req.VmIds.Distinct().ToList();
        var existing = await db.VirtualMachines.AsNoTracking().Where(v => ids.Contains(v.Id)).Select(v => v.Id).ToListAsync(http.RequestAborted);
        var visible = await access.FilterVmsAsync(http.User.UserId(), Permissions.InventoryRead, existing, http.RequestAborted);
        // Unknown and invisible VMs are reported the same way.
        return visible.Count != ids.Count ? (null, ApiSupport.BadRequest("unknown_vm")) : (ids, null);
    }

    private static async Task<IResult> AddMembersAsync(Guid id, MembersRequest req, HttpContext http, PortalDbContext db, AccessEvaluator access, IRealtimeNotifier notifier)
    {
        if (await http.GuardAsync(Permissions.GroupsManage, ResourceRef.Global, "groups.members.add") is { } denied)
        {
            return denied;
        }
        var ct = http.RequestAborted;
        if (!await db.LogicalGroups.AnyAsync(x => x.Id == id, ct))
        {
            return ApiSupport.NotFound();
        }
        var (ids, error) = await ResolveVmsAsync(req, http, db, access);
        if (error is not null)
        {
            return error;
        }
        var tree = await GroupTree.LoadAsync(db, ct);
        var toAdd = ids!.Where(v => !tree.GroupsOf(v).Contains(id)).ToList();
        var changes = new List<(ResourceRef, HashSet<Guid>, HashSet<Guid>)>();
        foreach (var vm in toAdd)
        {
            var groups = tree.GroupsOf(vm);
            var beforeSet = tree.ChainSet(groups);
            var afterSet = tree.ChainSet(groups.Append(id));
            changes.Add((ResourceRef.Vm(vm), [.. afterSet.Except(beforeSet)], []));
        }
        if (await GroupTree.CheckAccessChangeAsync(db, access, http.User.UserId(), changes, ct) is { } denial)
        {
            await http.WriteAuditAsync("groups.members.add", AuditOutcome.Denied, "group", id.ToString("D"), new { reason = denial, vms = toAdd });
            return ApiSupport.Forbidden(denial, "Adding these VMs would grant access through role assignments on the group that you cannot grant.");
        }
        foreach (var vm in toAdd)
        {
            db.LogicalGroupMembers.Add(new LogicalGroupMember { GroupId = id, VmId = vm });
        }
        await db.SaveChangesAsync(ct);
        await http.WriteAuditAsync("groups.members.add", resourceType: "group", resourceId: id.ToString("D"), details: new { vms = toAdd });
        await notifier.GroupsChangedAsync();
        return Results.Ok(new { added = toAdd.Count });
    }

    private static async Task<IResult> RemoveMembersAsync(Guid id, MembersRequest req, HttpContext http, PortalDbContext db, AccessEvaluator access, IRealtimeNotifier notifier)
    {
        if (await http.GuardAsync(Permissions.GroupsManage, ResourceRef.Global, "groups.members.remove") is { } denied)
        {
            return denied;
        }
        var ct = http.RequestAborted;
        if (!await db.LogicalGroups.AnyAsync(x => x.Id == id, ct))
        {
            return ApiSupport.NotFound();
        }
        var (ids, error) = await ResolveVmsAsync(req, http, db, access);
        if (error is not null)
        {
            return error;
        }
        var tree = await GroupTree.LoadAsync(db, ct);
        var toRemove = ids!.Where(v => tree.GroupsOf(v).Contains(id)).ToList();
        if (await RemovalCheckAsync(db, access, http.User.UserId(), tree, id, toRemove, ct) is { } denial)
        {
            await http.WriteAuditAsync("groups.members.remove", AuditOutcome.Denied, "group", id.ToString("D"), new { reason = denial, vms = toRemove });
            return ApiSupport.Forbidden(denial, "Removing these VMs would lift deny assignments that you cannot remove.");
        }
        await db.LogicalGroupMembers.Where(m => m.GroupId == id && toRemove.Contains(m.VmId)).ExecuteDeleteAsync(ct);
        await http.WriteAuditAsync("groups.members.remove", resourceType: "group", resourceId: id.ToString("D"), details: new { vms = toRemove });
        await notifier.GroupsChangedAsync();
        return Results.Ok(new { removed = toRemove.Count });
    }

    /// <summary>Validates placing a VM that is about to be created into a group (checked against the host scope).</summary>
    internal static async Task<string?> CheckNewVmPlacementAsync(PortalDbContext db, AccessEvaluator access, Guid actor, Guid groupId, Guid hostId, CancellationToken ct)
    {
        var tree = await GroupTree.LoadAsync(db, ct);
        if (!tree.Exists(groupId))
        {
            return "unknown_group";
        }
        if (!await access.HasAsync(actor, Permissions.GroupsManage, ResourceRef.Global, ct))
        {
            return "forbidden";
        }
        return await GroupTree.CheckAccessChangeAsync(db, access, actor,
            [(ResourceRef.Host(hostId), tree.ChainSet([groupId]), [])], ct);
    }
}
