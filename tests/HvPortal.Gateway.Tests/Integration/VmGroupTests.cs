using System.Net;
using System.Net.Http.Json;
using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Security;
using HvPortal.Gateway.Tests.Infrastructure;
using HvPortal.Gateway.VmGroups;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HvPortal.Gateway.Tests.Integration;

/// <summary>Portal groups mirrored to native Hyper-V VM groups (agent protocol 4) and import of existing native groups.</summary>
public sealed class VmGroupTests : IAsyncLifetime
{
    private GatewayFactory _app = null!;

    public async Task InitializeAsync()
    {
        _app = new GatewayFactory();
        _ = _app.Server;
        await _app.SetMfaPolicyAsync(MfaRequirement.None);
    }

    public Task DisposeAsync()
    {
        _app.Dispose();
        return Task.CompletedTask;
    }

    private VmGroupSyncService Sync => _app.Services.GetRequiredService<VmGroupSyncService>();

    private async Task<HttpClient> AdminAsync(string user)
    {
        await _app.CreateUserAsync(user, BuiltInRoles.PlatformAdmin);
        var c = _app.CreateBrowser();
        await c.LoginAsync(user);
        return c;
    }

    private static async Task<Guid> GroupAsync(HttpClient c, string name, Guid? parentId = null, params Guid[] vms)
    {
        var r = await c.PostJsonAsync("/api/v1/groups", new { name, parentId });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var id = (await r.JsonAsync()).GetProperty("id").GetGuid();
        if (vms.Length > 0)
        {
            Assert.Equal(HttpStatusCode.OK, (await c.PostJsonAsync($"/api/v1/groups/{id}/members", new { vmIds = vms })).StatusCode);
        }
        return id;
    }

    private Task<Guid> HyperVIdAsync(Guid vmId) => _app.WithDbAsync(db => db.VirtualMachines.Where(v => v.Id == vmId).Select(v => v.HyperVId).FirstAsync());

    private FakeAgentDispatcher.NativeGroup Native(Guid hostId, string name, string type) =>
        _app.Agent.NativeGroupsOf(hostId).Values.Single(g => g.Name == name && g.GroupType == type);

    [Fact]
    public async Task Portal_groups_are_mirrored_with_native_hierarchy_and_follow_changes()
    {
        var (hostId, vms) = await _app.CreateHostAsync("mirror-host", ("web", VmPowerState.Running), ("ubuntu", VmPowerState.Off), ("other", VmPowerState.Off));
        var c = await AdminAsync("admin-mirror");
        var linux = await GroupAsync(c, "Linux", null, vms["web"]);
        var ubuntu = await GroupAsync(c, "Ubuntu", linux, vms["ubuntu"]);
        await GroupAsync(c, "Empty");
        // A native group created outside the portal is never touched.
        _app.Agent.NativeGroupsOf(hostId)[Guid.NewGuid()] = new FakeAgentDispatcher.NativeGroup { Name = "Veeam", GroupType = VmGroupTypes.Vm };

        var first = await Sync.SyncHostAsync(hostId, force: false, CancellationToken.None);
        Assert.Equal("ok", first.Status);
        var web = await HyperVIdAsync(vms["web"]);
        var ub = await HyperVIdAsync(vms["ubuntu"]);
        Assert.Equal([web], Native(hostId, "Linux", VmGroupTypes.Vm).VmIds);
        Assert.Equal([ub], Native(hostId, "Ubuntu", VmGroupTypes.Vm).VmIds);
        var mgmt = Native(hostId, "Linux", VmGroupTypes.Management);
        Assert.Equal(2, mgmt.GroupIds.Count);
        Assert.DoesNotContain(_app.Agent.NativeGroupsOf(hostId).Values, g => g.Name == "Empty");
        Assert.Equal(3, await _app.WithDbAsync(db => db.HostVmGroupLinks.CountAsync(l => l.HostId == hostId)));

        // Same plan: nothing is sent.
        var sent = _app.Agent.OperationsReceived(HvOperation.SyncVmGroups).Count;
        Assert.Equal("unchanged", (await Sync.SyncHostAsync(hostId, force: false, CancellationToken.None)).Status);
        Assert.Equal(sent, _app.Agent.OperationsReceived(HvOperation.SyncVmGroups).Count);

        // Rename keeps the native id; removing the only VM of Linux removes its VM collection (the management one stays).
        var ubuntuNativeId = _app.Agent.NativeGroupsOf(hostId).Single(kv => kv.Value.Name == "Ubuntu").Key;
        await c.CsrfAsync();
        Assert.True((await c.PatchAsJsonAsync($"/api/v1/groups/{ubuntu}", new { name = "Ubuntu 24" }, GatewayFactory.Json)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.PostJsonAsync($"/api/v1/groups/{linux}/members/remove", new { vmIds = new[] { vms["web"] } })).StatusCode);
        Assert.Equal("ok", (await Sync.SyncHostAsync(hostId, force: false, CancellationToken.None)).Status);
        Assert.Equal("Ubuntu 24", _app.Agent.NativeGroupsOf(hostId)[ubuntuNativeId].Name);
        Assert.DoesNotContain(_app.Agent.NativeGroupsOf(hostId).Values, g => g.Name == "Linux" && g.GroupType == VmGroupTypes.Vm);
        Assert.Equal([ubuntuNativeId], Native(hostId, "Linux", VmGroupTypes.Management).GroupIds);

        // Group deleted in the portal -> native groups removed; the foreign group survives.
        Assert.Equal(HttpStatusCode.OK, (await c.PostJsonAsync($"/api/v1/groups/{ubuntu}/members/remove", new { vmIds = new[] { vms["ubuntu"] } })).StatusCode);
        await c.CsrfAsync();
        Assert.True((await c.DeleteAsync($"/api/v1/groups/{ubuntu}")).IsSuccessStatusCode);
        await Sync.SyncHostAsync(hostId, force: false, CancellationToken.None);
        Assert.Equal(["Veeam"], _app.Agent.NativeGroupsOf(hostId).Values.Select(g => g.Name));
        Assert.Equal(0, await _app.WithDbAsync(db => db.HostVmGroupLinks.CountAsync(l => l.HostId == hostId)));
    }

    [Fact]
    public async Task Hosts_with_an_older_agent_are_skipped_and_the_api_says_so()
    {
        var (hostId, vms) = await _app.CreateHostAsync("old-host", ("a", VmPowerState.Off));
        var c = await AdminAsync("admin-old");
        await GroupAsync(c, "G", null, vms["a"]);
        _app.Agent.ProtocolVersions[hostId] = 3;
        Assert.Equal("agent_outdated", (await Sync.SyncHostAsync(hostId, force: true, CancellationToken.None)).Status);
        Assert.Empty(_app.Agent.OperationsReceived(HvOperation.SyncVmGroups));
        Assert.Equal("agent_outdated", await (await c.PostJsonAsync($"/api/v1/hosts/{hostId}/vm-groups/sync")).CodeAsync());
        var list = await (await c.GetAsync($"/api/v1/hosts/{hostId}/vm-groups")).JsonAsync();
        Assert.False(list.GetProperty("supported").GetBoolean());
    }

    [Fact]
    public async Task Import_creates_portal_groups_links_them_and_never_merges_into_groups_with_assignments()
    {
        var (hostId, vms) = await _app.CreateHostAsync("import-host", ("db1", VmPowerState.Running), ("app1", VmPowerState.Running), ("test1", VmPowerState.Off));
        var c = await AdminAsync("admin-imp");
        // Existing portal group "Test" with a role assignment: importing must not put VMs into it (access would change).
        var test = await GroupAsync(c, "Test");
        var op = await _app.CreateUserAsync("op-imp", BuiltInRoles.VmOperator, ScopeType.Group, test.ToString("D"));
        var native = _app.Agent.NativeGroupsOf(hostId);
        var prodVms = new FakeAgentDispatcher.NativeGroup { Name = "Prod", GroupType = VmGroupTypes.Vm };
        prodVms.VmIds.Add(await HyperVIdAsync(vms["app1"]));
        var dbVms = new FakeAgentDispatcher.NativeGroup { Name = "DB", GroupType = VmGroupTypes.Vm };
        dbVms.VmIds.Add(await HyperVIdAsync(vms["db1"]));
        var testVms = new FakeAgentDispatcher.NativeGroup { Name = "Test", GroupType = VmGroupTypes.Vm };
        testVms.VmIds.Add(await HyperVIdAsync(vms["test1"]));
        Guid pId = Guid.NewGuid(), dId = Guid.NewGuid(), tId = Guid.NewGuid(), mId = Guid.NewGuid();
        native[pId] = prodVms;
        native[dId] = dbVms;
        native[tId] = testVms;
        var mgmt = new FakeAgentDispatcher.NativeGroup { Name = "Prod", GroupType = VmGroupTypes.Management };
        mgmt.GroupIds.UnionWith([pId, dId]);
        native[mId] = mgmt;

        // Permissions: hosts.manage + groups.manage.
        var opClient = _app.CreateBrowser();
        await opClient.LoginAsync("op-imp");
        Assert.Equal(HttpStatusCode.Forbidden, (await opClient.PostJsonAsync($"/api/v1/hosts/{hostId}/vm-groups/import")).StatusCode);

        var r = await c.PostJsonAsync($"/api/v1/hosts/{hostId}/vm-groups/import");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var groups = await _app.WithDbAsync(db => db.LogicalGroups.AsNoTracking().ToListAsync());
        var prod = groups.Single(g => g.Name == "Prod" && g.ParentId == null);
        var dbGroup = groups.Single(g => g.Name == "DB");
        Assert.Equal(prod.Id, dbGroup.ParentId);
        var imported = groups.Single(g => g.Name == "Test (import-host)");
        var members = await _app.WithDbAsync(db => db.LogicalGroupMembers.AsNoTracking().ToListAsync());
        Assert.Contains(members, m => m.GroupId == prod.Id && m.VmId == vms["app1"]);
        Assert.Contains(members, m => m.GroupId == dbGroup.Id && m.VmId == vms["db1"]);
        Assert.Contains(members, m => m.GroupId == imported.Id && m.VmId == vms["test1"]);
        Assert.DoesNotContain(members, m => m.GroupId == test);
        Assert.Equal(4, await _app.WithDbAsync(db => db.HostVmGroupLinks.CountAsync(l => l.HostId == hostId)));

        // The mirroring took over the same native groups: nothing created, nothing removed.
        Assert.Equal(4, native.Count);
        Assert.True(native.ContainsKey(mId) && native[mId].GroupIds.SetEquals([pId, dId]));
        // Importing again does nothing new.
        await c.PostJsonAsync($"/api/v1/hosts/{hostId}/vm-groups/import");
        Assert.Equal(groups.Count, await _app.WithDbAsync(db => db.LogicalGroups.CountAsync()));

        var list = await (await c.GetAsync($"/api/v1/hosts/{hostId}/vm-groups")).JsonAsync();
        Assert.All(list.GetProperty("groups").EnumerateArray(), g => Assert.True(g.GetProperty("managed").GetBoolean()));
        Assert.True(await _app.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "hosts.vmgroups.import")));
        _ = op;
    }

    private static FakeAgentDispatcher.NativeGroup AddNative(GatewayFactory app, Guid hostId, Guid id, string name, string type, IEnumerable<Guid>? vmIds = null, IEnumerable<Guid>? groupIds = null)
    {
        var g = new FakeAgentDispatcher.NativeGroup { Name = name, GroupType = type };
        g.VmIds.UnionWith(vmIds ?? []);
        g.GroupIds.UnionWith(groupIds ?? []);
        app.Agent.NativeGroupsOf(hostId)[id] = g;
        return g;
    }

    private static string Snapshot(GatewayFactory app, Guid hostId) =>
        string.Join("\n", app.Agent.NativeGroupsOf(hostId).OrderBy(kv => kv.Key).Select(kv =>
            $"{kv.Key}|{kv.Value.Name}|{kv.Value.GroupType}|{string.Join(',', kv.Value.VmIds.Order())}|{string.Join(',', kv.Value.GroupIds.Order())}"));

    /// <summary>ADR-027: a host that already has VM groups shows them as portal groups without anyone pressing "Import".</summary>
    [Fact]
    public async Task Groups_already_on_a_host_are_imported_by_themselves_once_and_hyper_v_is_left_as_it_is()
    {
        var (hostId, vms) = await _app.CreateHostAsync("auto-host", ("app1", VmPowerState.Running), ("db1", VmPowerState.Running), ("lab1", VmPowerState.Off), ("lone1", VmPowerState.Off));
        Guid app1 = await HyperVIdAsync(vms["app1"]), db1 = await HyperVIdAsync(vms["db1"]), lab1 = await HyperVIdAsync(vms["lab1"]), lone1 = await HyperVIdAsync(vms["lone1"]);
        Guid prodVms = Guid.NewGuid(), dbVms = Guid.NewGuid(), prod = Guid.NewGuid(), lab = Guid.NewGuid(), veeam = Guid.NewGuid(), full = Guid.NewGuid(), emptyChild = Guid.NewGuid(), mixed = Guid.NewGuid();
        // Taken: a management collection whose groups all have VMs (the shape the mirroring itself writes), a VM collection.
        AddNative(_app, hostId, prodVms, "Prod", VmGroupTypes.Vm, [app1]);
        AddNative(_app, hostId, dbVms, "DB", VmGroupTypes.Vm, [db1]);
        AddNative(_app, hostId, prod, "Prod", VmGroupTypes.Management, groupIds: [prodVms, dbVms]);
        AddNative(_app, hostId, lab, "Lab", VmGroupTypes.Vm, [lab1]);
        // Left alone: importing them would make the mirroring delete or re-write something in Hyper-V.
        AddNative(_app, hostId, veeam, "Veeam", VmGroupTypes.Vm);
        AddNative(_app, hostId, full, "Full", VmGroupTypes.Vm, [lone1]);
        AddNative(_app, hostId, emptyChild, "EmptyChild", VmGroupTypes.Vm);
        AddNative(_app, hostId, mixed, "Mixed", VmGroupTypes.Management, groupIds: [full, emptyChild]);
        var before = Snapshot(_app, hostId);

        // Nobody is signed in: the mirroring pass that follows the first inventory does it.
        Assert.Equal("ok", (await Sync.SyncHostAsync(hostId, force: false, CancellationToken.None)).Status);

        var groups = await _app.WithDbAsync(db => db.LogicalGroups.AsNoTracking().ToListAsync());
        var members = await _app.WithDbAsync(db => db.LogicalGroupMembers.AsNoTracking().ToListAsync());
        var prodGroup = groups.Single(g => g.Name == "Prod");
        Assert.Null(prodGroup.ParentId);
        Assert.Equal(prodGroup.Id, groups.Single(g => g.Name == "DB").ParentId);
        Assert.Null(groups.Single(g => g.Name == "Lab").ParentId);
        Assert.Equal(["DB", "Lab", "Prod"], groups.Select(g => g.Name).Order());
        Assert.Contains(members, m => m.GroupId == prodGroup.Id && m.VmId == vms["app1"]);
        Assert.Contains(members, m => m.GroupId == groups.Single(g => g.Name == "DB").Id && m.VmId == vms["db1"]);
        Assert.Contains(members, m => m.GroupId == groups.Single(g => g.Name == "Lab").Id && m.VmId == vms["lab1"]);
        Assert.Equal(3, members.Count);
        // Nothing was created, renamed, emptied or removed on the host; the imported groups are now owned by the portal.
        Assert.Equal(before, Snapshot(_app, hostId));
        Assert.True((await _app.WithDbAsync(db => db.HostVmGroupLinks.Where(l => l.HostId == hostId).Select(l => l.NativeId).ToListAsync())).ToHashSet().SetEquals([prodVms, dbVms, prod, lab]));
        Assert.True(await _app.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "hosts.vmgroups.import" && a.ActorName == "system")));
        Assert.NotNull(await _app.WithDbAsync(db => db.Hosts.Where(h => h.Id == hostId).Select(h => h.VmGroupsAdoptedAt).FirstAsync()));

        // Once per host: a group made in Hyper-V afterwards is not taken (it stays available to "Import").
        var later = Guid.NewGuid();
        AddNative(_app, hostId, later, "Later", VmGroupTypes.Vm, [lone1]);
        await Sync.SyncHostAsync(hostId, force: true, CancellationToken.None);
        Assert.DoesNotContain(await _app.WithDbAsync(db => db.LogicalGroups.Select(g => g.Name).ToListAsync()), n => n == "Later");
        Assert.Equal([lone1], _app.Agent.NativeGroupsOf(hostId)[later].VmIds);

        var c = await AdminAsync("admin-auto");
        var list = await (await c.GetAsync($"/api/v1/hosts/{hostId}/vm-groups")).JsonAsync();
        Assert.True(list.GetProperty("status").GetProperty("autoImport").GetBoolean());
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null, list.GetProperty("status").GetProperty("adoptedAt").ValueKind);
        var unmanaged = list.GetProperty("groups").EnumerateArray().Where(g => !g.GetProperty("managed").GetBoolean()).Select(g => g.GetProperty("name").GetString()).Order();
        Assert.Equal(["EmptyChild", "Full", "Later", "Mixed", "Veeam"], unmanaged);
        // The import on request still takes the rest.
        Assert.Equal(HttpStatusCode.OK, (await c.PostJsonAsync($"/api/v1/hosts/{hostId}/vm-groups/import")).StatusCode);
        Assert.Contains(await _app.WithDbAsync(db => db.LogicalGroups.Select(g => g.Name).ToListAsync()), n => n == "Later");
    }

    /// <summary>The automatic import gives nobody access: a portal group that carries role assignments is never filled.</summary>
    [Fact]
    public async Task Automatic_import_never_puts_vms_into_a_group_that_grants_access()
    {
        var c = await AdminAsync("admin-auto2");
        var tenant = await GroupAsync(c, "Tenant");
        await _app.CreateUserAsync("op-auto", BuiltInRoles.VmOperator, ScopeType.Group, tenant.ToString("D"));
        var (hostId, vms) = await _app.CreateHostAsync("auto-host2", ("secret", VmPowerState.Running));
        var nativeId = Guid.NewGuid();
        AddNative(_app, hostId, nativeId, "Tenant", VmGroupTypes.Vm, [await HyperVIdAsync(vms["secret"])]);

        await Sync.SyncHostAsync(hostId, force: false, CancellationToken.None);

        var groups = await _app.WithDbAsync(db => db.LogicalGroups.AsNoTracking().ToListAsync());
        var imported = groups.Single(g => g.Name == "Tenant (auto-host2)");
        var members = await _app.WithDbAsync(db => db.LogicalGroupMembers.AsNoTracking().ToListAsync());
        Assert.Equal([(imported.Id, vms["secret"])], members.Select(m => (m.GroupId, m.VmId)));
        Assert.False(await _app.WithDbAsync(db => db.RoleAssignments.AnyAsync(a => a.ScopeType == ScopeType.Group && a.ScopeId == imported.Id.ToString("D"))));
        var op = _app.CreateBrowser();
        await op.LoginAsync("op-auto");
        Assert.Equal(HttpStatusCode.NotFound, (await op.GetAsync($"/api/v1/vms/{vms["secret"]}")).StatusCode);
        // The VM kept its native group (renamed like its portal group), nothing was removed.
        Assert.Equal("Tenant (auto-host2)", _app.Agent.NativeGroupsOf(hostId)[nativeId].Name);
        Assert.Single(_app.Agent.NativeGroupsOf(hostId)[nativeId].VmIds);
    }

    [Fact]
    public async Task Automatic_import_waits_for_vms_the_inventory_has_not_reported_and_can_be_switched_off()
    {
        var (hostId, vms) = await _app.CreateHostAsync("wait-host", ("a", VmPowerState.Off));
        var a = await HyperVIdAsync(vms["a"]);
        var nativeId = Guid.NewGuid();
        // The group names a VM the portal does not know yet: importing now would lose it from the group.
        var pending = AddNative(_app, hostId, nativeId, "Pending", VmGroupTypes.Vm, [a, Guid.NewGuid()]);
        var before = Snapshot(_app, hostId);
        await Sync.SyncHostAsync(hostId, force: false, CancellationToken.None);
        Assert.Equal(0, await _app.WithDbAsync(db => db.LogicalGroups.CountAsync()));
        Assert.Null(await _app.WithDbAsync(db => db.Hosts.Where(h => h.Id == hostId).Select(h => h.VmGroupsAdoptedAt).FirstAsync()));
        Assert.Equal(before, Snapshot(_app, hostId));
        // Attempts are spaced (each one starts PowerShell on the host): a pass right after does not ask again.
        var listed = _app.Agent.OperationsReceived(HvOperation.ListVmGroups).Count;
        await Sync.SyncHostAsync(hostId, force: false, CancellationToken.None);
        Assert.Equal(listed, _app.Agent.OperationsReceived(HvOperation.ListVmGroups).Count);

        // Inventory complete: the next pass imports.
        pending.VmIds.Clear();
        pending.VmIds.Add(a);
        await Sync.SyncHostAsync(hostId, force: true, CancellationToken.None);
        var group = await _app.WithDbAsync(db => db.LogicalGroups.AsNoTracking().SingleAsync());
        Assert.Equal("Pending", group.Name);
        Assert.True(await _app.WithDbAsync(db => db.LogicalGroupMembers.AnyAsync(m => m.GroupId == group.Id && m.VmId == vms["a"])));
        Assert.NotNull(await _app.WithDbAsync(db => db.Hosts.Where(h => h.Id == hostId).Select(h => h.VmGroupsAdoptedAt).FirstAsync()));

        // Switched off (Gateway:VmGroups:AutoImport=false): existing groups are imported only on request.
        using var off = new GatewayFactory { ExtraSettings = { ["Gateway:VmGroups:AutoImport"] = "false" } };
        _ = off.Server;
        var (hostOff, vmsOff) = await off.CreateHostAsync("off-host", ("b", VmPowerState.Off));
        var hv = await off.WithDbAsync(db => db.VirtualMachines.Where(v => v.Id == vmsOff["b"]).Select(v => v.HyperVId).FirstAsync());
        AddNative(off, hostOff, Guid.NewGuid(), "Manual", VmGroupTypes.Vm, [hv]);
        await off.Services.GetRequiredService<VmGroupSyncService>().SyncHostAsync(hostOff, force: true, CancellationToken.None);
        Assert.Equal(0, await off.WithDbAsync(db => db.LogicalGroups.CountAsync()));
        Assert.Empty(off.Agent.OperationsReceived(HvOperation.ListVmGroups));
    }

    [Fact]
    public void Automatic_import_takes_only_groups_whose_import_changes_nothing_in_hyper_v()
    {
        Guid vm1 = Guid.NewGuid(), vm2 = Guid.NewGuid(), unknown = Guid.NewGuid();
        NativeVmGroup Vm(string name, params Guid[] vmIds) => new(Guid.NewGuid(), name, VmGroupTypes.Vm, vmIds, []);
        NativeVmGroup Mgmt(string name, params NativeVmGroup[] children) => new(Guid.NewGuid(), name, VmGroupTypes.Management, [], children.Select(c => c.Id).ToList());
        var known = new HashSet<Guid> { vm1, vm2 };

        // A VM collection in two management collections: importing would keep it under one and drop it from the other.
        var shared = Vm("Shared", vm1);
        NativeVmGroup m1 = Mgmt("M1", shared), m2 = Mgmt("M2", shared);
        var twice = VmGroupImporter.Adoptable([shared, m1, m2], new HashSet<Guid>(), known);
        Assert.Empty(twice.Groups);
        Assert.Equal(3, twice.NotImported);
        Assert.False(twice.WaitingForInventory);

        // An empty management collection, an already owned group, a VM collection added to an owned management collection.
        var empty = Mgmt("EmptyMgmt");
        var owned = Vm("Owned", vm1);
        var intruder = Vm("Intruder", vm2);
        var ownedMgmt = Mgmt("OwnedMgmt", owned, intruder);
        var linked = new HashSet<Guid> { owned.Id, ownedMgmt.Id };
        var result = VmGroupImporter.Adoptable([empty, owned, intruder, ownedMgmt], linked, known);
        Assert.Empty(result.Groups);
        Assert.Equal(2, result.NotImported);

        // Unknown VM: the group and the management collection around it wait; an unrelated group is taken now.
        var partial = Vm("Partial", vm1, unknown);
        var sibling = Vm("Sibling", vm2);
        var parent = Mgmt("Parent", partial, sibling);
        var alone = Vm("Alone", vm2);
        var waiting = VmGroupImporter.Adoptable([partial, sibling, parent, alone], new HashSet<Guid>(), known);
        Assert.Equal([alone.Id], waiting.Groups.Select(g => g.Id));
        Assert.True(waiting.WaitingForInventory);
        // Once the VM is known everything is taken together.
        var complete = VmGroupImporter.Adoptable([partial, sibling, parent, alone], new HashSet<Guid>(), new HashSet<Guid> { vm1, vm2, unknown });
        Assert.Equal(4, complete.Groups.Count);
        Assert.Equal(0, complete.NotImported);
    }

    [Fact]
    public void Planner_flattens_deep_trees_into_each_management_collection()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), cc = Guid.NewGuid(), vm1 = Guid.NewGuid(), vm2 = Guid.NewGuid();
        var plan = VmGroupPlanner.Plan(
            [new(a, null, "A"), new(b, a, "B"), new(cc, b, "C")],
            [(cc, vm1), (a, vm2)],
            new Dictionary<Guid, Guid> { [vm1] = Guid.NewGuid(), [vm2] = Guid.NewGuid() },
            []);
        Assert.Null(OperationValidation.Validate(plan));
        var mgmtA = plan.Items.Single(i => i.Key == VmGroupPlanner.Key(a, "mgmt"));
        Assert.True(mgmtA.MemberKeys.ToHashSet().SetEquals([VmGroupPlanner.Key(a, "vms"), VmGroupPlanner.Key(cc, "vms")]));
        Assert.Equal([VmGroupPlanner.Key(cc, "vms")], plan.Items.Single(i => i.Key == VmGroupPlanner.Key(b, "mgmt")).MemberKeys);
        Assert.DoesNotContain(plan.Items, i => i.Key == VmGroupPlanner.Key(b, "vms"));
    }
}
