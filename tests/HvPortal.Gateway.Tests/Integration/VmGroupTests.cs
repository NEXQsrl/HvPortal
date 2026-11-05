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
