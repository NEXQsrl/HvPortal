using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Security;
using HvPortal.Gateway.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HvPortal.Gateway.Tests.Integration;

/// <summary>Groups (tree, inheritance, anti-escalation), configuration/creation/checkpoint jobs, console tickets and WebSocket.</summary>
public sealed class OperationsTests : IAsyncLifetime
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

    private static async Task<HttpResponseMessage> PatchAsync(HttpClient c, string url, object body)
    {
        await c.CsrfAsync();
        return await c.PatchAsJsonAsync(url, body, GatewayFactory.Json);
    }

    private static async Task<HttpResponseMessage> PutAsync(HttpClient c, string url, object body)
    {
        await c.CsrfAsync();
        return await c.PutAsJsonAsync(url, body, GatewayFactory.Json);
    }

    private async Task<HttpClient> LoginAsync(string user)
    {
        var c = _app.CreateBrowser();
        await c.LoginAsync(user);
        return c;
    }

    private async Task<Guid> CreateGroupAsync(HttpClient c, string name, Guid? parentId = null)
    {
        var r = await c.PostJsonAsync("/api/v1/groups", new { name, parentId });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.JsonAsync()).GetProperty("id").GetGuid();
    }

    private Task<Guid> CustomRoleAsync(string name, params string[] permissions) => _app.WithDbAsync(async db =>
    {
        var role = new Role { Name = name };
        foreach (var p in permissions)
        {
            role.Permissions.Add(new RolePermission { RoleId = role.Id, Permission = p });
        }
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        return role.Id;
    });

    private Task AssignRoleAsync(Guid userId, Guid roleId, ScopeType scope, string scopeId, AssignmentEffect effect = AssignmentEffect.Allow) => _app.WithDbAsync(async db =>
    {
        db.RoleAssignments.Add(new RoleAssignment { PrincipalId = userId.ToString("D"), RoleId = roleId, ScopeType = scope, ScopeId = scopeId, Effect = effect, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    });

    private async Task<Job> JobAsync(HttpResponseMessage accepted)
    {
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var id = (await accepted.JsonAsync()).GetProperty("jobId").GetGuid();
        await _app.RunJobsAsync();
        return await _app.WithDbAsync(db => db.Jobs.AsNoTracking().FirstAsync(j => j.Id == id));
    }

    // ------------------------------------------------------------------ groups

    [Fact]
    public async Task Assignment_on_a_parent_group_applies_to_vms_in_subgroups()
    {
        var (_, vms) = await _app.CreateHostAsync("tree-host", ("web1", VmPowerState.Off), ("db1", VmPowerState.Off));
        await _app.CreateUserAsync("admin-tree", BuiltInRoles.PlatformAdmin);
        var admin = await LoginAsync("admin-tree");
        var prod = await CreateGroupAsync(admin, "Production");
        var web = await CreateGroupAsync(admin, "Web", prod);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostJsonAsync($"/api/v1/groups/{web}/members", new { vmIds = new[] { vms["web1"] } })).StatusCode);

        await _app.CreateUserAsync("op-tree", BuiltInRoles.VmOperator, ScopeType.Group, prod.ToString("D"));
        var op = await LoginAsync("op-tree");
        Assert.Equal(HttpStatusCode.OK, (await op.GetAsync($"/api/v1/vms/{vms["web1"]}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await op.GetAsync($"/api/v1/vms/{vms["db1"]}")).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await op.PostJsonAsync($"/api/v1/vms/{vms["web1"]}/power", new { operation = "Start" })).StatusCode);

        // The operator sees the path to its VM (Production > Web) but not unrelated groups.
        await CreateGroupAsync(admin, "Lab");
        var groups = (await (await op.GetAsync("/api/v1/groups")).JsonAsync()).GetProperty("groups").EnumerateArray().Select(g => g.GetProperty("name").GetString()).ToList();
        Assert.Contains("Production", groups);
        Assert.Contains("Web", groups);
        Assert.DoesNotContain("Lab", groups);

        // Moving the subgroup out of Production removes the inherited access.
        Assert.Equal(HttpStatusCode.NoContent, (await PatchAsync(admin, $"/api/v1/groups/{web}", new { moveToRoot = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await op.GetAsync($"/api/v1/vms/{vms["web1"]}")).StatusCode);
    }

    [Fact]
    public async Task Group_tree_rejects_cycles_duplicates_and_nonempty_deletes()
    {
        await _app.CreateUserAsync("admin-cyc", BuiltInRoles.PlatformAdmin);
        var admin = await LoginAsync("admin-cyc");
        var a = await CreateGroupAsync(admin, "A");
        var b = await CreateGroupAsync(admin, "B", a);
        var c = await CreateGroupAsync(admin, "C", b);
        Assert.Equal("invalid_parent", await (await PatchAsync(admin, $"/api/v1/groups/{a}", new { parentId = c })).CodeAsync());
        Assert.Equal("invalid_parent", await (await PatchAsync(admin, $"/api/v1/groups/{a}", new { parentId = a })).CodeAsync());
        Assert.Equal("group_exists", await (await admin.PostJsonAsync("/api/v1/groups", new { name = "b", parentId = a })).CodeAsync());
        Assert.Equal("invalid_group_name", await (await admin.PostJsonAsync("/api/v1/groups", new { name = "<x>" })).CodeAsync());
        await admin.CsrfAsync();
        Assert.Equal("group_not_empty", await (await admin.DeleteAsync($"/api/v1/groups/{b}")).CodeAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/groups/{c}")).StatusCode);
    }

    [Fact]
    public async Task Adding_a_vm_to_a_group_with_grants_requires_roles_manage_and_held_permissions()
    {
        var (_, vms) = await _app.CreateHostAsync("esc-host", ("secret", VmPowerState.Off));
        await _app.CreateUserAsync("admin-esc", BuiltInRoles.PlatformAdmin);
        var admin = await LoginAsync("admin-esc");
        var ops = await CreateGroupAsync(admin, "Ops");
        // An accomplice holds VM operator on the group: putting a VM in it grants them power on that VM.
        await _app.CreateUserAsync("accomplice", BuiltInRoles.VmOperator, ScopeType.Group, ops.ToString("D"));

        var organizerRole = await CustomRoleAsync("Group organizer", Permissions.GroupsManage, Permissions.InventoryRead);
        var organizer = await _app.CreateUserAsync("organizer");
        await AssignRoleAsync(organizer, organizerRole, ScopeType.Global, "");
        var org = await LoginAsync("organizer");
        var r = await org.PostJsonAsync($"/api/v1/groups/{ops}/members", new { vmIds = new[] { vms["secret"] } });
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("group_access_change_requires_roles_manage", await r.CodeAsync());
        Assert.False(await _app.WithDbAsync(db => db.LogicalGroupMembers.AnyAsync(m => m.GroupId == ops)));

        // Groups without assignments can be organized freely.
        var plain = await CreateGroupAsync(admin, "Plain");
        Assert.Equal(HttpStatusCode.OK, (await org.PostJsonAsync($"/api/v1/groups/{plain}/members", new { vmIds = new[] { vms["secret"] } })).StatusCode);
        // ...but moving that group under Ops would grant the same access: refused.
        Assert.Equal("group_access_change_requires_roles_manage", await (await PatchAsync(org, $"/api/v1/groups/{plain}", new { parentId = ops })).CodeAsync());

        // A platform admin holds roles.manage and every permission: allowed.
        Assert.Equal(HttpStatusCode.OK, (await admin.PostJsonAsync($"/api/v1/groups/{ops}/members", new { vmIds = new[] { vms["secret"] } })).StatusCode);
        var audit = await _app.WithDbAsync(db => db.AuditEntries.AsNoTracking().Where(e => e.Action == "groups.members.add").Select(e => e.Outcome).ToListAsync());
        Assert.Contains(AuditOutcome.Denied, audit);
        Assert.Contains(AuditOutcome.Success, audit);
    }

    [Fact]
    public async Task Removing_a_vm_from_a_group_with_a_deny_requires_roles_manage()
    {
        var (_, vms) = await _app.CreateHostAsync("deny-host", ("quarantined", VmPowerState.Off));
        await _app.CreateUserAsync("admin-deny", BuiltInRoles.PlatformAdmin);
        var admin = await LoginAsync("admin-deny");
        var q = await CreateGroupAsync(admin, "Quarantine");
        Assert.Equal(HttpStatusCode.OK, (await admin.PostJsonAsync($"/api/v1/groups/{q}/members", new { vmIds = new[] { vms["quarantined"] } })).StatusCode);
        var victim = await _app.CreateUserAsync("blocked-op", BuiltInRoles.VmOperator);
        await _app.AssignAsync(victim, BuiltInRoles.VmOperator, ScopeType.Group, q.ToString("D"), AssignmentEffect.Deny);

        var organizerRole = await CustomRoleAsync("Organizer 2", Permissions.GroupsManage, Permissions.InventoryRead);
        var organizer = await _app.CreateUserAsync("organizer2");
        await AssignRoleAsync(organizer, organizerRole, ScopeType.Global, "");
        var org = await LoginAsync("organizer2");
        Assert.Equal("group_access_change_requires_roles_manage",
            await (await org.PostJsonAsync($"/api/v1/groups/{q}/members/remove", new { vmIds = new[] { vms["quarantined"] } })).CodeAsync());
        Assert.True(await _app.WithDbAsync(db => db.LogicalGroupMembers.AnyAsync(m => m.GroupId == q)));
    }

    [Fact]
    public async Task Group_management_requires_groups_manage()
    {
        var (_, vms) = await _app.CreateHostAsync("gm-host", ("v", VmPowerState.Off));
        await _app.CreateUserAsync("op-gm", BuiltInRoles.VmOperator);
        var op = await LoginAsync("op-gm");
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostJsonAsync("/api/v1/groups", new { name = "X" })).StatusCode);
        var list = await (await op.GetAsync("/api/v1/groups")).JsonAsync();
        Assert.False(list.GetProperty("canManage").GetBoolean());
        Assert.NotNull(vms);
    }

    // ------------------------------------------------------------------ configuration

    [Fact]
    public async Task Config_change_needs_the_permission_of_each_section_and_off_for_cpu_count()
    {
        var (hostId, vms) = await _app.CreateHostAsync("cfg-host", ("app", VmPowerState.Off), ("live", VmPowerState.Running));
        var role = await CustomRoleAsync("Compute only", Permissions.InventoryRead, Permissions.VmConfigCompute);
        var user = await _app.CreateUserAsync("compute-user");
        await AssignRoleAsync(user, role, ScopeType.Host, hostId.ToString("D"));
        var c = await LoginAsync("compute-user");

        var cfg = await (await c.GetAsync($"/api/v1/vms/{vms["app"]}/config")).JsonAsync();
        Assert.Equal("app", cfg.GetProperty("config").GetProperty("name").GetString());
        Assert.True(cfg.GetProperty("can").GetProperty("compute").GetBoolean());
        Assert.False(cfg.GetProperty("can").GetProperty("network").GetBoolean());

        Assert.Equal(HttpStatusCode.Forbidden, (await PatchAsync(c, $"/api/v1/vms/{vms["app"]}/config", new { addNetworkAdapterSwitch = "vSwitch" })).StatusCode);
        Assert.Equal("requires_off", await (await PatchAsync(c, $"/api/v1/vms/{vms["live"]}/config", new { processorCount = 4 })).CodeAsync());
        Assert.Equal("invalid_processor_count", await (await PatchAsync(c, $"/api/v1/vms/{vms["app"]}/config", new { processorCount = 0 })).CodeAsync());
        Assert.Equal("no_changes", await (await PatchAsync(c, $"/api/v1/vms/{vms["app"]}/config", new { })).CodeAsync());

        // The client cannot choose the Hyper-V VM id nor the ISO roots: the server stamps them.
        var job = await JobAsync(await PatchAsync(c, $"/api/v1/vms/{vms["app"]}/config", new { processorCount = 4, vmId = Guid.NewGuid(), isoRoots = new[] { @"C:\" } }));
        Assert.Equal(JobStatus.Succeeded, job.Status);
        var sent = _app.Agent.OperationsReceived(HvOperation.SetVmConfig).Single();
        var change = HvOperations.FromElement<VmConfigChange>(sent.OperationArgs)!;
        var hyperVId = await _app.WithDbAsync(db => db.VirtualMachines.Where(v => v.Id == vms["app"]).Select(v => v.HyperVId).FirstAsync());
        Assert.Equal(hyperVId, change.VmId);
        Assert.Empty(change.IsoRoots);
        Assert.Equal(4, change.ProcessorCount);
    }

    [Fact]
    public async Task Config_is_reauthorized_when_the_job_runs()
    {
        var (_, vms) = await _app.CreateHostAsync("reauth-cfg", ("app", VmPowerState.Off));
        var role = await CustomRoleAsync("Compute 2", Permissions.InventoryRead, Permissions.VmConfigCompute);
        var user = await _app.CreateUserAsync("compute-revoked");
        await AssignRoleAsync(user, role, ScopeType.Vm, vms["app"].ToString("D"));
        var c = await LoginAsync("compute-revoked");
        // Compute settings only: the notes belong to inventory.annotate, which this role does not include.
        Assert.Equal(HttpStatusCode.Forbidden, (await PatchAsync(c, $"/api/v1/vms/{vms["app"]}/config", new { notes = "x" })).StatusCode);
        var r = await PatchAsync(c, $"/api/v1/vms/{vms["app"]}/config", new { processorWeight = 200 });
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        await _app.WithDbAsync(db => db.RoleAssignments.Where(a => a.PrincipalId == user.ToString("D")).ExecuteDeleteAsync());
        var id = (await r.JsonAsync()).GetProperty("jobId").GetGuid();
        await _app.RunJobsAsync();
        var job = await _app.WithDbAsync(db => db.Jobs.AsNoTracking().FirstAsync(j => j.Id == id));
        Assert.Equal("authorization_revoked", job.ErrorCode);
        Assert.Empty(_app.Agent.OperationsReceived(HvOperation.SetVmConfig));
    }

    [Fact]
    public async Task Iso_must_be_inside_the_host_library()
    {
        var (hostId, vms) = await _app.CreateHostAsync("iso-host", ("app", VmPowerState.Off));
        await _app.CreateUserAsync("infra-iso", BuiltInRoles.InfrastructureAdmin);
        var c = await LoginAsync("infra-iso");
        Assert.Equal("invalid_iso_library", await (await PutAsync(c, $"/api/v1/hosts/{hostId}/iso-library", new { paths = new[] { @"\\server\share" } })).CodeAsync());
        Assert.Equal("invalid_iso_library", await (await PutAsync(c, $"/api/v1/hosts/{hostId}/iso-library", new { paths = new[] { @"D:\ISO\..\Windows" } })).CodeAsync());
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(c, $"/api/v1/hosts/{hostId}/iso-library", new { paths = new[] { @"D:\ISO" } })).StatusCode);

        Assert.Equal("iso_outside_library", await (await PatchAsync(c, $"/api/v1/vms/{vms["app"]}/config", new { dvdIsoPath = @"E:\other\setup.iso" })).CodeAsync());
        Assert.Equal("iso_outside_library", await (await PatchAsync(c, $"/api/v1/vms/{vms["app"]}/config", new { dvdIsoPath = @"D:\ISO2\setup.iso" })).CodeAsync());
        var job = await JobAsync(await PatchAsync(c, $"/api/v1/vms/{vms["app"]}/config", new { dvdIsoPath = @"D:\ISO\win.iso" }));
        Assert.Equal(JobStatus.Succeeded, job.Status);
        var change = HvOperations.FromElement<VmConfigChange>(_app.Agent.OperationsReceived(HvOperation.SetVmConfig).Single().OperationArgs)!;
        Assert.Equal(new[] { @"D:\ISO" }, change.IsoRoots);

        var isos = await (await c.GetAsync($"/api/v1/hosts/{hostId}/isos")).JsonAsync();
        Assert.Equal(@"D:\ISO\win.iso", isos.GetProperty("files")[0].GetProperty("path").GetString());
    }

    // ------------------------------------------------------------------ creation

    [Fact]
    public async Task Create_vm_requires_vm_create_on_the_host_and_places_it_in_the_group()
    {
        var (hostId, _) = await _app.CreateHostAsync("create-host");
        var (otherHost, _) = await _app.CreateHostAsync("other-host");
        await _app.CreateUserAsync("admin-create", BuiltInRoles.PlatformAdmin);
        var admin = await LoginAsync("admin-create");
        var group = await CreateGroupAsync(admin, "New VMs");

        var creatorRole = await CustomRoleAsync("Creator", Permissions.InventoryRead, Permissions.VmCreate);
        var creator = await _app.CreateUserAsync("creator");
        await AssignRoleAsync(creator, creatorRole, ScopeType.Host, hostId.ToString("D"));
        var c = await LoginAsync("creator");
        var body = new { vm = new { name = "web-02", generation = 2, processorCount = 2, memoryStartupMb = 2048, diskSizeGb = 40, switchName = "vSwitch" } };
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostJsonAsync($"/api/v1/hosts/{otherHost}/vms", body)).StatusCode);
        Assert.Equal("invalid_vm_name", await (await c.PostJsonAsync($"/api/v1/hosts/{hostId}/vms", new { vm = new { name = "bad/name" } })).CodeAsync());
        Assert.Equal("unknown_switch", await (await c.PostJsonAsync($"/api/v1/hosts/{hostId}/vms", new { vm = new { name = "x1", switchName = "nope" } })).CodeAsync());
        Assert.Equal("gen1_no_secure_boot", await (await c.PostJsonAsync($"/api/v1/hosts/{hostId}/vms", new { vm = new { name = "x2", generation = 1, enableTpm = true } })).CodeAsync());
        // Without groups.manage the creator cannot place the VM in a group.
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostJsonAsync($"/api/v1/hosts/{hostId}/vms", new { body.vm, groupId = group })).StatusCode);

        var job = await JobAsync(await c.PostJsonAsync($"/api/v1/hosts/{hostId}/vms", body));
        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Contains("web-02", job.ResultJson);

        // Admin creates into a group: the VM is added once it appears in inventory.
        var job2 = await JobAsync(await admin.PostJsonAsync($"/api/v1/hosts/{hostId}/vms", new { vm = new { name = "web-03", diskSizeGb = 0 }, groupId = group }));
        Assert.Equal(JobStatus.Succeeded, job2.Status);
        var placed = await _app.WithDbAsync(db => db.LogicalGroupMembers.AsNoTracking().Where(m => m.GroupId == group)
            .Join(db.VirtualMachines, m => m.VmId, v => v.Id, (m, v) => v.Name).ToListAsync());
        Assert.Equal(new[] { "web-03" }, placed);
        Assert.Equal("vm_name_exists", await (await admin.PostJsonAsync($"/api/v1/hosts/{hostId}/vms", new { vm = new { name = "web-03" } })).CodeAsync());
    }

    [Fact]
    public async Task Folder_browser_needs_vm_create_for_folders_and_hosts_manage_for_isos()
    {
        var (hostId, _) = await _app.CreateHostAsync("browse-host");
        var creatorRole = await CustomRoleAsync("Creator-b", Permissions.InventoryRead, Permissions.VmCreate);
        var creator = await _app.CreateUserAsync("creator-b");
        await AssignRoleAsync(creator, creatorRole, ScopeType.Host, hostId.ToString("D"));
        await _app.CreateUserAsync("op-b", BuiltInRoles.VmOperator);
        await _app.CreateUserAsync("admin-b", BuiltInRoles.PlatformAdmin);
        var c = await LoginAsync("creator-b");

        var drives = await (await c.GetAsync($"/api/v1/hosts/{hostId}/folders")).JsonAsync();
        Assert.Contains(drives.GetProperty("drives").EnumerateArray(), d => d.GetProperty("path").GetString() == @"D:\");
        var d1 = await (await c.GetAsync($"/api/v1/hosts/{hostId}/folders?path={Uri.EscapeDataString(@"d:\")}")).JsonAsync();
        Assert.Contains(d1.GetProperty("folders").EnumerateArray(), f => f.GetProperty("name").GetString() == "Virtual Machines");
        // System folders are refused before reaching the agent; UNC and traversal are invalid.
        Assert.Equal("location_not_allowed", await (await c.GetAsync($"/api/v1/hosts/{hostId}/folders?path={Uri.EscapeDataString(@"C:\Windows\System32")}")).CodeAsync());
        Assert.Equal("invalid_path", await (await c.GetAsync($"/api/v1/hosts/{hostId}/folders?path={Uri.EscapeDataString(@"\\server\share")}")).CodeAsync());
        Assert.Equal("invalid_path", await (await c.GetAsync($"/api/v1/hosts/{hostId}/folders?path={Uri.EscapeDataString(@"D:\a\..\Windows")}")).CodeAsync());
        // ISO files only for hosts.manage.
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync($"/api/v1/hosts/{hostId}/folders?path={Uri.EscapeDataString(@"D:\ISO")}&isos=true")).StatusCode);
        var op = await LoginAsync("op-b");
        Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync($"/api/v1/hosts/{hostId}/folders")).StatusCode);
        var admin = await LoginAsync("admin-b");
        var isoDir = await (await admin.GetAsync($"/api/v1/hosts/{hostId}/folders?path={Uri.EscapeDataString(@"D:\ISO")}&isos=true")).JsonAsync();
        Assert.Equal(@"D:\ISO\win.iso", isoDir.GetProperty("isos")[0].GetProperty("path").GetString());

        // Agents older than protocol 3 never receive the command.
        _app.Agent.ProtocolVersions[hostId] = 2;
        var before = _app.Agent.OperationsReceived(HvOperation.BrowseFolders).Count;
        Assert.Equal("agent_outdated", await (await c.GetAsync($"/api/v1/hosts/{hostId}/folders")).CodeAsync());
        Assert.Equal(before, _app.Agent.OperationsReceived(HvOperation.BrowseFolders).Count);
    }

    [Fact]
    public async Task Create_vm_location_is_validated_and_needs_a_protocol_3_agent()
    {
        var (hostId, _) = await _app.CreateHostAsync("loc-host");
        await _app.CreateUserAsync("admin-loc", BuiltInRoles.PlatformAdmin);
        var c = await LoginAsync("admin-loc");
        Assert.Equal("location_not_allowed", await (await c.PostJsonAsync($"/api/v1/hosts/{hostId}/vms", new { vm = new { name = "l1", location = @"C:\Windows" } })).CodeAsync());
        Assert.Equal("location_not_allowed", await (await c.PostJsonAsync($"/api/v1/hosts/{hostId}/vms", new { vm = new { name = "l1", location = @"D:\$Recycle.Bin\x" } })).CodeAsync());
        Assert.Equal("invalid_location", await (await c.PostJsonAsync($"/api/v1/hosts/{hostId}/vms", new { vm = new { name = "l1", location = @"\\nas\vms" } })).CodeAsync());

        var job = await JobAsync(await c.PostJsonAsync($"/api/v1/hosts/{hostId}/vms", new { vm = new { name = "l2", location = @"d:\Virtual Machines\" } }));
        Assert.Equal(JobStatus.Succeeded, job.Status);
        var sent = HvOperations.FromElement<NewVmRequest>(_app.Agent.OperationsReceived(HvOperation.NewVm).Last().OperationArgs)!;
        Assert.Equal(@"D:\Virtual Machines", sent.Location);

        _app.Agent.ProtocolVersions[hostId] = 2;
        Assert.Equal("agent_outdated", await (await c.PostJsonAsync($"/api/v1/hosts/{hostId}/vms", new { vm = new { name = "l3", location = @"D:\VMs" } })).CodeAsync());
        // Without a location the old agent still creates VMs in the host default folders.
        Assert.Equal(JobStatus.Succeeded, (await JobAsync(await c.PostJsonAsync($"/api/v1/hosts/{hostId}/vms", new { vm = new { name = "l4" } }))).Status);
    }

    // ------------------------------------------------------------------ checkpoints

    [Fact]
    public async Task Checkpoint_apply_requires_off_state_confirmation_and_permission()
    {
        var (cpHost, vms) = await _app.CreateHostAsync("cp-host", ("live", VmPowerState.Running), ("cold", VmPowerState.Off));
        // Agents before protocol 7 do not turn a running VM off before applying (see FolderLimitsTests for protocol 7).
        _app.Agent.ProtocolVersions[cpHost] = 6;
        await _app.CreateUserAsync("op-cp", BuiltInRoles.VmOperator);
        await _app.CreateUserAsync("infra-cp", BuiltInRoles.InfrastructureAdmin);
        var op = await LoginAsync("op-cp");
        var cpId = Guid.NewGuid();
        // VM operators can create checkpoints but not apply them.
        Assert.Equal(JobStatus.Succeeded, (await JobAsync(await op.PostJsonAsync($"/api/v1/vms/{vms["live"]}/checkpoints", new { name = "before patch" }))).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostJsonAsync($"/api/v1/vms/{vms["cold"]}/checkpoints/{cpId}/apply", new { confirmName = "cold" })).StatusCode);

        var infra = await LoginAsync("infra-cp");
        Assert.Equal("invalid_state", await (await infra.PostJsonAsync($"/api/v1/vms/{vms["live"]}/checkpoints/{cpId}/apply", new { confirmName = "live" })).CodeAsync());
        Assert.Equal("confirmation_required", await (await infra.PostJsonAsync($"/api/v1/vms/{vms["cold"]}/checkpoints/{cpId}/apply", new { })).CodeAsync());
        Assert.Equal(JobStatus.Succeeded, (await JobAsync(await infra.PostJsonAsync($"/api/v1/vms/{vms["cold"]}/checkpoints/{cpId}/apply", new { confirmName = "cold" }))).Status);
        var sent = HvOperations.FromElement<CheckpointRequest>(_app.Agent.OperationsReceived(HvOperation.RestoreCheckpoint).Single().OperationArgs)!;
        Assert.Equal(cpId.ToString("D"), sent.CheckpointId);

        var list = await (await infra.GetAsync($"/api/v1/vms/{vms["cold"]}/checkpoints")).JsonAsync();
        Assert.Equal("Before update", list.GetProperty("checkpoints")[0].GetProperty("name").GetString());
        Assert.Contains("not backups", list.GetProperty("note").GetString());
    }

    // ------------------------------------------------------------------ console

    private static async Task SendAsync(WebSocket ws, object message) =>
        await ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message)), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<(WebSocketMessageType Type, byte[] Data, WebSocketCloseStatus? Close)> ReceiveAsync(WebSocket ws)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var buffer = new byte[1 << 16];
        using var ms = new MemoryStream();
        WebSocketReceiveResult r;
        do
        {
            r = await ws.ReceiveAsync(buffer, cts.Token);
            ms.Write(buffer, 0, r.Count);
        } while (!r.EndOfMessage);
        return (r.MessageType, ms.ToArray(), r.CloseStatus);
    }

    [Fact]
    public async Task Console_ticket_is_one_time_bound_to_the_session_and_streams_frames()
    {
        var (_, vms) = await _app.CreateHostAsync("con-host", ("desktop", VmPowerState.Running));
        await _app.CreateUserAsync("ro-con", BuiltInRoles.ReadOnly);
        await _app.CreateUserAsync("op-con", BuiltInRoles.VmOperator);
        var ro = await LoginAsync("ro-con");
        Assert.Equal(HttpStatusCode.Forbidden, (await ro.PostJsonAsync($"/api/v1/vms/{vms["desktop"]}/console/tickets")).StatusCode);

        var (c, cookies) = _app.CreateBrowserWithCookies();
        await c.LoginAsync("op-con");
        var ticket = (await (await c.PostJsonAsync($"/api/v1/vms/{vms["desktop"]}/console/tickets")).JsonAsync()).GetProperty("ticket").GetString();
        var wsClient = _app.Server.CreateWebSocketClient();
        wsClient.ConfigureRequest = r =>
        {
            r.Headers["Origin"] = "https://localhost";
            r.Headers["Cookie"] = cookies.GetCookieHeader(new Uri("https://localhost"));
        };
        var url = new Uri($"wss://localhost/api/v1/vms/{vms["desktop"]}/console/ws");
        using (var ws = await wsClient.ConnectAsync(url, CancellationToken.None))
        {
            await SendAsync(ws, new { t = "auth", ticket });
            var gotFrame = false;
            for (var i = 0; i < 5 && !gotFrame; i++)
            {
                var (type, data, _) = await ReceiveAsync(ws);
                gotFrame = type == WebSocketMessageType.Binary && data[0] == 1 && BitConverter.ToUInt16(data, 2) == 4;
            }
            Assert.True(gotFrame);
            await SendAsync(ws, new { t = "k", c = new[] { 0x1e, 0x9e } });
            await SendAsync(ws, new { t = "k", c = new[] { 999 } }); // invalid: ignored, never forwarded
            await SendAsync(ws, new { t = "cad" });
            for (var i = 0; i < 50 && _app.Agent.InputsReceived < 2; i++)
            {
                await Task.Delay(100);
            }
            Assert.Equal(2, _app.Agent.InputsReceived);
            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }

        // The same ticket cannot be used twice.
        using (var ws2 = await wsClient.ConnectAsync(url, CancellationToken.None))
        {
            await SendAsync(ws2, new { t = "auth", ticket });
            var (type, _, close) = await ReceiveAsync(ws2);
            Assert.Equal(WebSocketMessageType.Close, type);
            Assert.Equal(WebSocketCloseStatus.PolicyViolation, close);
        }

        // Another user's session cannot redeem a ticket.
        var (other, otherCookies) = _app.CreateBrowserWithCookies();
        await other.LoginAsync("ro-con");
        var ticket2 = (await (await c.PostJsonAsync($"/api/v1/vms/{vms["desktop"]}/console/tickets")).JsonAsync()).GetProperty("ticket").GetString();
        var otherClient = _app.Server.CreateWebSocketClient();
        otherClient.ConfigureRequest = r =>
        {
            r.Headers["Origin"] = "https://localhost";
            r.Headers["Cookie"] = otherCookies.GetCookieHeader(new Uri("https://localhost"));
        };
        using (var ws3 = await otherClient.ConnectAsync(url, CancellationToken.None))
        {
            await SendAsync(ws3, new { t = "auth", ticket = ticket2 });
            Assert.Equal(WebSocketCloseStatus.PolicyViolation, (await ReceiveAsync(ws3)).Close);
        }
        var audit = await _app.WithDbAsync(db => db.AuditEntries.AsNoTracking().Where(e => e.Action.StartsWith("vm.console.")).Select(e => e.Action + ":" + e.Outcome).ToListAsync());
        Assert.Contains("vm.console.open:Success", audit);
        Assert.Contains("vm.console.close:Success", audit);
        Assert.Contains("vm.console.open:Denied", audit);
    }

    [Fact]
    public async Task Console_websocket_rejects_foreign_origin()
    {
        var (_, vms) = await _app.CreateHostAsync("origin-host", ("desktop", VmPowerState.Running));
        await _app.CreateUserAsync("op-origin", BuiltInRoles.VmOperator);
        var (c, cookies) = _app.CreateBrowserWithCookies();
        await c.LoginAsync("op-origin");
        var wsClient = _app.Server.CreateWebSocketClient();
        wsClient.ConfigureRequest = r =>
        {
            r.Headers["Origin"] = "https://evil.example";
            r.Headers["Cookie"] = cookies.GetCookieHeader(new Uri("https://localhost"));
        };
        await Assert.ThrowsAnyAsync<Exception>(() => wsClient.ConnectAsync(new Uri($"wss://localhost/api/v1/vms/{vms["desktop"]}/console/ws"), CancellationToken.None));
    }

    [Fact]
    public void Console_socket_accepts_http2_extended_connect()
    {
        // Browsers open WebSockets over HTTP/2 with CONNECT (RFC 8441); a GET-only route made Edge/Chrome fail the handshake.
        var sources = _app.Services.GetServices<Microsoft.AspNetCore.Routing.EndpointDataSource>();
        var ep = sources.SelectMany(s => s.Endpoints).OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/api/v1/vms/{id:guid}/console/ws");
        var methods = ep.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()!.HttpMethods;
        Assert.Contains("GET", methods);
        Assert.Contains("CONNECT", methods);
    }

    // ------------------------------------------------------------------ shell

    [Fact]
    public async Task Spa_shell_gets_a_fresh_style_nonce_matching_the_csp()
    {
        var c = _app.CreateBrowser();
        var r1 = await c.GetAsync("/");
        var r2 = await c.GetAsync("/vms/whatever");
        var csp1 = r1.Headers.GetValues("Content-Security-Policy").Single();
        var csp2 = r2.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("script-src 'self';", csp1);
        Assert.DoesNotContain("unsafe", csp1);
        Assert.NotEqual(csp1, csp2);
        if (r1.StatusCode == HttpStatusCode.OK)
        {
            var html = await r1.Content.ReadAsStringAsync();
            Assert.DoesNotContain("__CSP_NONCE__", html);
            var nonce = csp1.Split("'nonce-")[1].Split('\'')[0];
            Assert.Contains($"content=\"{nonce}\"", html);
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/index.html")).StatusCode);
        }
    }
}
