using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Security;
using HvPortal.Gateway.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HvPortal.Gateway.Tests.Integration;

/// <summary>
/// Limits of VM folders (ADR-024): quotas, hosts, networks and storage; folder users create VMs, private networks and
/// reach files only inside those limits; virtual switches; user deletion; checkpoint apply on a running VM.
/// </summary>
public sealed class FolderLimitsTests : IAsyncLifetime
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

    private async Task<HttpClient> LoginAsync(string user)
    {
        var c = _app.CreateBrowser();
        await c.LoginAsync(user);
        return c;
    }

    private static async Task<HttpResponseMessage> PutAsync(HttpClient c, string url, object body)
    {
        await c.CsrfAsync();
        return await c.PutAsJsonAsync(url, body, GatewayFactory.Json);
    }

    private static async Task<HttpResponseMessage> PatchAsync(HttpClient c, string url, object body)
    {
        await c.CsrfAsync();
        return await c.PatchAsJsonAsync(url, body, GatewayFactory.Json);
    }

    private static async Task<Guid> CreateGroupAsync(HttpClient c, string name, Guid? parentId = null)
    {
        var r = await c.PostJsonAsync("/api/v1/groups", new { name, parentId });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.JsonAsync()).GetProperty("id").GetGuid();
    }

    private async Task<Job> JobAsync(HttpResponseMessage accepted)
    {
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var id = (await accepted.JsonAsync()).GetProperty("jobId").GetGuid();
        await _app.RunJobsAsync();
        return await _app.WithDbAsync(db => db.Jobs.AsNoTracking().FirstAsync(j => j.Id == id));
    }

    /// <summary>The switches a host reports in its inventory (the gateway refuses switch names it does not know).</summary>
    private Task SetHostSwitchesAsync(Guid hostId, params string[] names) => _app.WithDbAsync(async db =>
    {
        var host = await db.Hosts.FirstAsync(h => h.Id == hostId);
        host.HostFactsJson = JsonSerializer.Serialize(new HostFacts { ComputerName = "HOST", AgentVersion = "test", VirtualSwitches = names });
        await db.SaveChangesAsync();
        return 0;
    });

    private Task<int> AuditCountAsync(string action, AuditOutcome outcome) =>
        _app.WithDbAsync(db => db.AuditEntries.CountAsync(a => a.Action == action && a.Outcome == outcome));

    /// <summary>Two hosts, a folder limited to the first one and a folder administrator ("tenant") of that folder.</summary>
    private async Task<(Guid H1, Guid H2, Guid Folder, HttpClient Admin, HttpClient Tenant)> TenantAsync(object policy)
    {
        var (h1, _) = await _app.CreateHostAsync("fl-host1");
        var (h2, _) = await _app.CreateHostAsync("fl-host2");
        await _app.CreateUserAsync("fl-admin", BuiltInRoles.PlatformAdmin);
        var admin = await LoginAsync("fl-admin");
        var folder = await CreateGroupAsync(admin, "Tenant A");
        var json = JsonSerializer.Serialize(policy, GatewayFactory.Json).Replace("\"H1\"", $"\"{h1}\"", StringComparison.Ordinal);
        await admin.CsrfAsync();
        var put = await admin.PutAsync($"/api/v1/groups/{folder}/policy", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        await _app.CreateUserAsync("tenant", BuiltInRoles.FolderAdmin, ScopeType.Group, folder.ToString("D"));
        return (h1, h2, folder, admin, await LoginAsync("tenant"));
    }

    // ------------------------------------------------------------------ limits: who sets them, validation

    [Fact]
    public async Task Folder_limits_need_groups_and_hosts_manage_and_are_validated()
    {
        var (h1, _) = await _app.CreateHostAsync("pol-host");
        await _app.CreateUserAsync("pol-admin", BuiltInRoles.PlatformAdmin);
        var admin = await LoginAsync("pol-admin");
        var folder = await CreateGroupAsync(admin, "Customer");
        var other = await CreateGroupAsync(admin, "Other customer");
        await _app.CreateUserAsync("pol-owner", BuiltInRoles.FolderAdmin, ScopeType.Group, folder.ToString("D"));
        await _app.CreateUserAsync("pol-hostadmin", BuiltInRoles.InfrastructureAdmin, ScopeType.Host, h1.ToString("D"));
        await _app.CreateUserAsync("pol-ro", BuiltInRoles.ReadOnly);

        // The folder's own administrator reads the limits but cannot change them; so do host-scoped admins and readers.
        var owner = await LoginAsync("pol-owner");
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/v1/groups/{folder}/policy")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/v1/groups/{other}/policy")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(owner, $"/api/v1/groups/{folder}/policy", new { maxVms = 1000 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(await LoginAsync("pol-hostadmin"), $"/api/v1/groups/{folder}/policy", new { maxVms = 1000 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(await LoginAsync("pol-ro"), $"/api/v1/groups/{folder}/policy", new { maxVms = 1000 })).StatusCode);
        Assert.Equal(3, await AuditCountAsync("groups.policy.update", AuditOutcome.Denied));
        Assert.Null(await _app.WithDbAsync(db => db.LogicalGroups.Where(g => g.Id == folder).Select(g => g.PolicyJson).FirstAsync()));

        Assert.Equal("invalid_max_vms", await (await PutAsync(admin, $"/api/v1/groups/{folder}/policy", new { maxVms = -1 })).CodeAsync());
        Assert.Equal("unknown_host", await (await PutAsync(admin, $"/api/v1/groups/{folder}/policy", new { hosts = new[] { Guid.NewGuid() } })).CodeAsync());
        Assert.Equal("location_not_allowed", await (await PutAsync(admin, $"/api/v1/groups/{folder}/policy",
            new { storage = new[] { new { hostId = h1, path = @"C:\Windows\Temp" } } })).CodeAsync());
        Assert.Equal("invalid_location", await (await PutAsync(admin, $"/api/v1/groups/{folder}/policy",
            new { storage = new[] { new { hostId = h1, path = @"\\nas\share" } } })).CodeAsync());

        var ok = await PutAsync(admin, $"/api/v1/groups/{folder}/policy", new
        {
            maxVms = 3, maxProcessors = 8, maxMemoryMb = 16384, maxDiskGb = 200, maxNetworks = 1, hosts = new[] { h1 },
            networks = new[] { new { hostId = h1, name = "vSwitch" } }, storage = new[] { new { hostId = h1, path = @"d:\tenants\a\" } },
        });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var view = await (await owner.GetAsync($"/api/v1/groups/{folder}/policy")).JsonAsync();
        Assert.False(view.GetProperty("canEdit").GetBoolean());
        Assert.True(view.GetProperty("canCreateVm").GetBoolean());
        Assert.Equal(3, view.GetProperty("policy").GetProperty("maxVms").GetInt32());
        Assert.Equal(@"D:\tenants\a", view.GetProperty("policy").GetProperty("storage")[0].GetProperty("path").GetString());
        var hostView = view.GetProperty("hosts")[0];
        Assert.Equal(h1, hostView.GetProperty("id").GetGuid());
        Assert.Equal("vSwitch", hostView.GetProperty("switches")[0].GetString());
        Assert.Equal(0, view.GetProperty("levels")[0].GetProperty("usage").GetProperty("vms").GetInt32());
        Assert.True((await (await admin.GetAsync($"/api/v1/groups/{folder}/policy")).JsonAsync()).GetProperty("canEdit").GetBoolean());
        Assert.Equal(1, await AuditCountAsync("groups.policy.update", AuditOutcome.Success));

        // An empty policy removes the limits.
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(admin, $"/api/v1/groups/{folder}/policy", new { })).StatusCode);
        Assert.Null(await _app.WithDbAsync(db => db.LogicalGroups.Where(g => g.Id == folder).Select(g => g.PolicyJson).FirstAsync()));
    }

    // ------------------------------------------------------------------ VM creation and configuration inside a folder

    [Fact]
    public async Task Folder_user_creates_and_changes_vms_only_within_the_folder_limits()
    {
        var (h1, h2, folder, admin, tenant) = await TenantAsync(new
        {
            maxVms = 2, maxProcessors = 4, maxMemoryMb = 8192, maxDiskGb = 100, hosts = new[] { "H1" },
            networks = new[] { new { hostId = "H1", name = "vSwitch" } }, storage = new[] { new { hostId = "H1", path = @"D:\Tenants\A" } },
        });
        await SetHostSwitchesAsync(h1, "vSwitch", "Backbone");
        var vm = new { name = "t-web1", processorCount = 2, memoryStartupMb = 4096, diskSizeGb = 60, switchName = "vSwitch" };

        // Not on the host itself, not on a host the folder is not enabled on, not into somebody else's folder.
        Assert.Equal(HttpStatusCode.Forbidden, (await tenant.PostJsonAsync($"/api/v1/hosts/{h1}/vms", new { vm })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tenant.PostJsonAsync($"/api/v1/hosts/{h2}/vms", new { vm, groupId = folder })).StatusCode);
        var foreign = await CreateGroupAsync(admin, "Tenant B");
        Assert.Equal(HttpStatusCode.Forbidden, (await tenant.PostJsonAsync($"/api/v1/hosts/{h1}/vms", new { vm, groupId = foreign })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tenant.GetAsync($"/api/v1/hosts/{h1}/defaults")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tenant.GetAsync($"/api/v1/hosts/{h2}/defaults?groupId={folder}")).StatusCode);

        // What the wizard is offered inside the folder: only the allowed network and the folder's storage.
        var defaults = await (await tenant.GetAsync($"/api/v1/hosts/{h1}/defaults?groupId={folder}")).JsonAsync();
        Assert.Equal(new[] { "vSwitch" }, defaults.GetProperty("switches").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal(@"D:\Tenants\A", defaults.GetProperty("storageRoots")[0].GetString());

        // Network, storage and memory rules of the folder.
        Assert.Equal("network_not_allowed", await (await tenant.PostJsonAsync($"/api/v1/hosts/{h1}/vms", new { vm = new { vm.name, switchName = "Backbone" }, groupId = folder })).CodeAsync());
        Assert.Equal("storage_not_allowed", await (await tenant.PostJsonAsync($"/api/v1/hosts/{h1}/vms",
            new { vm = new { vm.name, location = @"D:\Other" }, groupId = folder })).CodeAsync());
        Assert.Equal("quota_memory_maximum_required", await (await tenant.PostJsonAsync($"/api/v1/hosts/{h1}/vms",
            new { vm = new { vm.name, dynamicMemory = true, memoryStartupMb = 2048 }, groupId = folder })).CodeAsync());
        Assert.Equal("quota_exceeded", await (await tenant.PostJsonAsync($"/api/v1/hosts/{h1}/vms",
            new { vm = new { vm.name, dynamicMemory = true, memoryStartupMb = 2048, memoryMaximumMb = 16384 }, groupId = folder })).CodeAsync());
        Assert.Equal("quota_exceeded", await (await tenant.PostJsonAsync($"/api/v1/hosts/{h1}/vms", new { vm = new { vm.name, processorCount = 8 }, groupId = folder })).CodeAsync());
        Assert.True(await AuditCountAsync("vm.create.request", AuditOutcome.Denied) >= 5);

        // First VM: created in the folder's storage (the location is set by the gateway) and placed in the folder
        // without groups.manage.
        var job = await JobAsync(await tenant.PostJsonAsync($"/api/v1/hosts/{h1}/vms", new { vm, groupId = folder }));
        Assert.Equal(JobStatus.Succeeded, job.Status);
        var sent = HvOperations.FromElement<NewVmRequest>(_app.Agent.OperationsReceived(HvOperation.NewVm).Last().OperationArgs)!;
        Assert.Equal(@"D:\Tenants\A", sent.Location);
        var members = await _app.WithDbAsync(db => db.LogicalGroupMembers.AsNoTracking().Where(m => m.GroupId == folder)
            .Join(db.VirtualMachines, m => m.VmId, v => v.Id, (m, v) => v).ToListAsync());
        var first = Assert.Single(members);
        Assert.Equal("t-web1", first.Name);
        // The folder role now applies to the new VM.
        Assert.Equal(HttpStatusCode.OK, (await tenant.GetAsync($"/api/v1/vms/{first.Id}")).StatusCode);

        // Quotas: vCPU (2 used of 4), disk (60 used of 100), then the number of VMs.
        Assert.Equal("quota_exceeded", await (await tenant.PostJsonAsync($"/api/v1/hosts/{h1}/vms", new { vm = new { name = "t-web2", processorCount = 4, memoryStartupMb = 2048, diskSizeGb = 10 }, groupId = folder })).CodeAsync());
        Assert.Equal("quota_exceeded", await (await tenant.PostJsonAsync($"/api/v1/hosts/{h1}/vms", new { vm = new { name = "t-web2", processorCount = 2, memoryStartupMb = 2048, diskSizeGb = 60 }, groupId = folder })).CodeAsync());
        var job2 = await JobAsync(await tenant.PostJsonAsync($"/api/v1/hosts/{h1}/vms", new { vm = new { name = "t-web2", processorCount = 2, memoryStartupMb = 2048, diskSizeGb = 40 }, groupId = folder }));
        Assert.Equal(JobStatus.Succeeded, job2.Status);
        Assert.Equal("quota_exceeded", await (await tenant.PostJsonAsync($"/api/v1/hosts/{h1}/vms", new { vm = new { name = "t-web3", processorCount = 1, memoryStartupMb = 512, diskSizeGb = 0 }, groupId = folder })).CodeAsync());

        var usage = (await (await tenant.GetAsync($"/api/v1/groups/{folder}/policy?live=true")).JsonAsync()).GetProperty("levels")[0].GetProperty("usage");
        Assert.Equal(2, usage.GetProperty("vms").GetInt32());
        Assert.Equal(4, usage.GetProperty("processors").GetInt32());
        Assert.Equal(6144, usage.GetProperty("memoryMb").GetInt64());
        Assert.Equal(100, usage.GetProperty("diskGb").GetInt64());

        // Changes of an existing VM count too: more vCPU, a new disk, a network outside the list.
        // (The inventory taken after the creations replaced the host facts: report both switches again.)
        await SetHostSwitchesAsync(h1, "vSwitch", "Backbone");
        Assert.Equal("quota_exceeded", await (await PatchAsync(tenant, $"/api/v1/vms/{first.Id}/config", new { processorCount = 4 })).CodeAsync());
        Assert.Equal("quota_exceeded", await (await PatchAsync(tenant, $"/api/v1/vms/{first.Id}/config", new { memoryStartupMb = 8192 })).CodeAsync());
        Assert.Equal("quota_exceeded", await (await PatchAsync(tenant, $"/api/v1/vms/{first.Id}/config", new { newDisk = new { sizeGb = 10, @fixed = false } })).CodeAsync());
        Assert.Equal("network_not_allowed", await (await PatchAsync(tenant, $"/api/v1/vms/{first.Id}/config", new { addNetworkAdapterSwitch = "Backbone" })).CodeAsync());
        // Lowering is always possible.
        Assert.Equal(HttpStatusCode.Accepted, (await PatchAsync(tenant, $"/api/v1/vms/{first.Id}/config", new { processorCount = 1 })).StatusCode);
        await _app.RunJobsAsync();

        // With more room the new disk is accepted and goes to the VM's own folder inside the allowed storage; a folder
        // sent by the client is ignored.
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(admin, $"/api/v1/groups/{folder}/policy", new
        {
            maxVms = 2, maxDiskGb = 200, hosts = new[] { h1 }, storage = new[] { new { hostId = h1, path = @"D:\Tenants\A" } },
        })).StatusCode);
        var diskJob = await JobAsync(await PatchAsync(tenant, $"/api/v1/vms/{first.Id}/config", new { newDisk = new { sizeGb = 10, @fixed = false, folder = @"C:\Users\Public" } }));
        Assert.Equal(JobStatus.Succeeded, diskJob.Status);
        var change = HvOperations.FromElement<VmConfigChange>(_app.Agent.OperationsReceived(HvOperation.SetVmConfig).Last().OperationArgs)!;
        Assert.Equal(@"D:\Tenants\A\t-web1\Virtual Hard Disks", change.NewDisk!.Folder);
    }

    [Fact]
    public async Task Queued_creations_reserve_the_quota_and_limits_are_checked_again_when_the_job_runs()
    {
        var (h1, _, folder, admin, tenant) = await TenantAsync(new { maxVms = 2, hosts = new[] { "H1" } });
        object Vm(string name) => new { vm = new { name, processorCount = 1, memoryStartupMb = 1024, diskSizeGb = 0 }, groupId = folder };

        // Two creations are accepted (not run yet): together they fill the quota, a third one is refused.
        var a = await tenant.PostJsonAsync($"/api/v1/hosts/{h1}/vms", Vm("q1"));
        var b = await tenant.PostJsonAsync($"/api/v1/hosts/{h1}/vms", Vm("q2"));
        Assert.Equal(HttpStatusCode.Accepted, a.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, b.StatusCode);
        Assert.Equal("quota_exceeded", await (await tenant.PostJsonAsync($"/api/v1/hosts/{h1}/vms", Vm("q3"))).CodeAsync());

        // The limit is lowered before the jobs run: the first still fits, the second is refused at execution.
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(admin, $"/api/v1/groups/{folder}/policy", new { maxVms = 1, hosts = new[] { h1 } })).StatusCode);
        // Creations on one host run one at a time: the second waits a moment for the first.
        for (var i = 0; i < 30; i++)
        {
            await _app.RunJobsAsync();
            if (!await _app.WithDbAsync(db => db.Jobs.AnyAsync(j => j.Status == JobStatus.Queued || j.Status == JobStatus.Running)))
            {
                break;
            }
            await Task.Delay(500);
        }
        var jobs = await _app.WithDbAsync(db => db.Jobs.AsNoTracking().Where(j => j.Type == "vm.create").OrderBy(j => j.CreatedAt).ToListAsync());
        Assert.Single(jobs, j => j.Status == JobStatus.Succeeded);
        var refused = Assert.Single(jobs, j => j.Status == JobStatus.Failed);
        Assert.Equal("quota_exceeded", refused.ErrorCode);
        Assert.Single(_app.Agent.OperationsReceived(HvOperation.NewVm));

        // The host is taken away from the folder while a creation waits: the job is no longer authorized.
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(admin, $"/api/v1/groups/{folder}/policy", new { maxVms = 5, hosts = new[] { h1 } })).StatusCode);
        var c = await tenant.PostJsonAsync($"/api/v1/hosts/{h1}/vms", Vm("q4"));
        Assert.Equal(HttpStatusCode.Accepted, c.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(admin, $"/api/v1/groups/{folder}/policy", new { maxVms = 5 })).StatusCode);
        var late = await JobAsync(c);
        Assert.Equal(JobStatus.Failed, late.Status);
        Assert.Equal("authorization_revoked", late.ErrorCode);
        Assert.Single(_app.Agent.OperationsReceived(HvOperation.NewVm));
    }

    [Fact]
    public async Task Limits_of_a_parent_folder_apply_to_subfolders()
    {
        var (h1, _, folder, admin, tenant) = await TenantAsync(new { maxVms = 1, hosts = new[] { "H1" } });
        var sub = await CreateGroupAsync(admin, "Production", folder);
        // The role on the parent folder applies to the subfolder, and so does the parent's quota.
        var job = await JobAsync(await tenant.PostJsonAsync($"/api/v1/hosts/{h1}/vms", new { vm = new { name = "p1", diskSizeGb = 0 }, groupId = sub }));
        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Equal("quota_exceeded", await (await tenant.PostJsonAsync($"/api/v1/hosts/{h1}/vms", new { vm = new { name = "p2", diskSizeGb = 0 }, groupId = folder })).CodeAsync());
        Assert.Equal("quota_exceeded", await (await tenant.PostJsonAsync($"/api/v1/hosts/{h1}/vms", new { vm = new { name = "p2", diskSizeGb = 0 }, groupId = sub })).CodeAsync());
        // Administrators are bound by the folder's quota as well (they can raise it).
        Assert.Equal("quota_exceeded", await (await admin.PostJsonAsync($"/api/v1/hosts/{h1}/vms", new { vm = new { name = "p3", diskSizeGb = 0 }, groupId = sub })).CodeAsync());
        Assert.Equal(HttpStatusCode.Accepted, (await admin.PostJsonAsync($"/api/v1/hosts/{h1}/vms", new { vm = new { name = "p3", diskSizeGb = 0 } })).StatusCode);
    }

    // ------------------------------------------------------------------ networks

    [Fact]
    public async Task Virtual_switches_need_network_manage_and_folder_users_get_private_networks_within_the_quota()
    {
        var (h1, h2, folder, admin, tenant) = await TenantAsync(new { maxNetworks = 1, hosts = new[] { "H1" }, networks = Array.Empty<object>() });
        await _app.CreateUserAsync("net-ro", BuiltInRoles.ReadOnly);
        var ro = await LoginAsync("net-ro");

        // Host level: readers cannot create or list adapters; nothing is queued.
        Assert.Equal(HttpStatusCode.Forbidden, (await ro.PostJsonAsync($"/api/v1/hosts/{h1}/switches", new { name = "lab", type = "Private" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ro.GetAsync($"/api/v1/hosts/{h1}/net-adapters")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tenant.GetAsync($"/api/v1/hosts/{h1}/net-adapters")).StatusCode);
        Assert.True(await AuditCountAsync("host.network.create.request", AuditOutcome.Denied) >= 1);

        // Folder level: private networks only, on the folder's hosts, within the quota.
        Assert.Equal(HttpStatusCode.Forbidden, (await tenant.PostJsonAsync($"/api/v1/hosts/{h1}/switches", new { name = "t-net", type = "Private" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tenant.PostJsonAsync($"/api/v1/hosts/{h2}/switches", new { name = "t-net", type = "Private", groupId = folder })).StatusCode);
        Assert.Equal("folder_networks_are_private", await (await tenant.PostJsonAsync($"/api/v1/hosts/{h1}/switches",
            new { name = "t-net", type = "External", netAdapterName = "Ethernet 2", groupId = folder })).CodeAsync());
        Assert.Equal("invalid_switch_name", await (await tenant.PostJsonAsync($"/api/v1/hosts/{h1}/switches", new { name = "bad|name", type = "Private", groupId = folder })).CodeAsync());
        Assert.Equal("switch_exists", await (await tenant.PostJsonAsync($"/api/v1/hosts/{h1}/switches", new { name = "vSwitch", type = "Private", groupId = folder })).CodeAsync());
        Assert.Empty(_app.Agent.OperationsReceived(HvOperation.NewSwitch));

        var created = await JobAsync(await tenant.PostJsonAsync($"/api/v1/hosts/{h1}/switches", new { name = "t-net", type = "Private", groupId = folder }));
        Assert.Equal(JobStatus.Succeeded, created.Status);
        var owned = await _app.WithDbAsync(db => db.GroupNetworks.AsNoTracking().SingleAsync());
        Assert.Equal((folder, h1, "t-net"), (owned.GroupId, owned.HostId, owned.SwitchName));
        Assert.Equal("quota_exceeded", await (await tenant.PostJsonAsync($"/api/v1/hosts/{h1}/switches", new { name = "t-net2", type = "Private", groupId = folder })).CodeAsync());

        // The folder allows no host network (empty list), but its own network is usable by its VMs.
        await SetHostSwitchesAsync(h1, "vSwitch", "t-net");
        Assert.Equal("network_not_allowed", await (await tenant.PostJsonAsync($"/api/v1/hosts/{h1}/vms", new { vm = new { name = "n1", diskSizeGb = 0, switchName = "vSwitch" }, groupId = folder })).CodeAsync());
        var vmJob = await JobAsync(await tenant.PostJsonAsync($"/api/v1/hosts/{h1}/vms", new { vm = new { name = "n1", diskSizeGb = 0, switchName = "t-net" }, groupId = folder }));
        Assert.Equal(JobStatus.Succeeded, vmJob.Status);

        // Listing: the owner folder and who may delete.
        var list = (await (await tenant.GetAsync($"/api/v1/hosts/{h1}/switches")).JsonAsync()).EnumerateArray().ToList();
        var mine = list.Single(s => s.GetProperty("name").GetString() == "t-net");
        Assert.Equal(folder, mine.GetProperty("ownerGroupId").GetGuid());
        Assert.True(mine.GetProperty("canDelete").GetBoolean());
        var shared = list.Single(s => s.GetProperty("name").GetString() == "vSwitch");
        Assert.False(shared.GetProperty("canDelete").GetBoolean());

        // Deleting: only the folder's own network, with the name typed.
        Assert.Equal(HttpStatusCode.Forbidden, (await tenant.PostJsonAsync($"/api/v1/hosts/{h1}/switches/delete",
            new { switchId = shared.GetProperty("id").GetString(), name = "vSwitch", confirmName = "vSwitch" })).StatusCode);
        var switchId = mine.GetProperty("id").GetString();
        Assert.Equal("confirmation_required", await (await tenant.PostJsonAsync($"/api/v1/hosts/{h1}/switches/delete", new { switchId, name = "t-net" })).CodeAsync());
        var removed = await JobAsync(await tenant.PostJsonAsync($"/api/v1/hosts/{h1}/switches/delete", new { switchId, name = "t-net", confirmName = "t-net" }));
        Assert.Equal(JobStatus.Succeeded, removed.Status);
        Assert.Equal(0, await _app.WithDbAsync(db => db.GroupNetworks.CountAsync()));

        // Host administrators: any type, with the physical adapters of the host.
        var adapters = await (await admin.GetAsync($"/api/v1/hosts/{h1}/net-adapters")).JsonAsync();
        Assert.Equal(2, adapters.GetArrayLength());
        Assert.Equal("invalid_net_adapter", await (await admin.PostJsonAsync($"/api/v1/hosts/{h1}/switches", new { name = "uplink", type = "External" })).CodeAsync());
        Assert.Equal("net_adapter_only_for_external", await (await admin.PostJsonAsync($"/api/v1/hosts/{h1}/switches",
            new { name = "uplink", type = "Internal", netAdapterName = "Ethernet 2" })).CodeAsync());
        var external = await JobAsync(await admin.PostJsonAsync($"/api/v1/hosts/{h1}/switches",
            new { name = "uplink", type = "External", netAdapterName = "Ethernet 2", allowManagementOs = true }));
        Assert.Equal(JobStatus.Succeeded, external.Status);
        var args = HvOperations.FromElement<NewSwitchRequest>(_app.Agent.OperationsReceived(HvOperation.NewSwitch).Last().OperationArgs)!;
        Assert.Equal(("uplink", "External", "Ethernet 2", true), (args.Name, args.Type, args.NetAdapterName, args.AllowManagementOs));
        Assert.Equal(0, await _app.WithDbAsync(db => db.GroupNetworks.CountAsync()));

        // Older agents are never sent the new operations.
        _app.Agent.ProtocolVersions[h1] = 6;
        Assert.Equal("agent_outdated", await (await admin.PostJsonAsync($"/api/v1/hosts/{h1}/switches", new { name = "later", type = "Private" })).CodeAsync());
    }

    // ------------------------------------------------------------------ files of a folder

    [Fact]
    public async Task Folder_users_reach_only_the_storage_paths_of_the_folder()
    {
        var (h1, h2, folder, _, tenant) = await TenantAsync(new { hosts = new[] { "H1" }, storage = new[] { new { hostId = "H1", path = @"D:\Tenants\A" } } });
        _app.Agent.Folders[@"D:\Tenants\A"] = ["iso"];
        _app.Agent.Folders[@"D:\Tenants\A\iso"] = [];
        _app.Agent.Folders[@"D:\Tenants\B"] = [];
        _app.Agent.Files[@"D:\Tenants\A\readme.txt"] = "hello"u8.ToArray();
        _app.Agent.Files[@"D:\Tenants\B\secret.txt"] = "other tenant"u8.ToArray();

        // Without the folder scope the tenant has no access to the host files at all.
        Assert.Equal(HttpStatusCode.NotFound, (await tenant.GetAsync($"/api/v1/hosts/{h1}/files")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tenant.GetAsync($"/api/v1/hosts/{h2}/files?groupId={folder}")).StatusCode);

        // Folder scope: the starting points are the folder's storage paths; everything else is refused and audited.
        var root = await (await tenant.GetAsync($"/api/v1/hosts/{h1}/files?groupId={folder}")).JsonAsync();
        Assert.Equal(@"D:\Tenants\A", root.GetProperty("folders")[0].GetProperty("path").GetString());
        Assert.Equal(0, root.GetProperty("drives").GetArrayLength());
        var inside = await (await tenant.GetAsync($"/api/v1/hosts/{h1}/files?groupId={folder}&path={Uri.EscapeDataString(@"D:\Tenants\A")}")).JsonAsync();
        Assert.Equal("readme.txt", inside.GetProperty("files")[0].GetProperty("name").GetString());
        foreach (var outside in new[] { @"D:\Tenants\B", @"D:\Tenants", @"D:\Tenants\AB", @"D:\" })
        {
            Assert.Equal("storage_not_allowed", await (await tenant.GetAsync($"/api/v1/hosts/{h1}/files?groupId={folder}&path={Uri.EscapeDataString(outside)}")).CodeAsync());
        }
        Assert.Equal(HttpStatusCode.OK, (await tenant.GetAsync($"/api/v1/hosts/{h1}/files/download?groupId={folder}&path={Uri.EscapeDataString(@"D:\Tenants\A\readme.txt")}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tenant.GetAsync($"/api/v1/hosts/{h1}/files/download?groupId={folder}&path={Uri.EscapeDataString(@"D:\Tenants\B\secret.txt")}")).StatusCode);
        Assert.True(await AuditCountAsync("host.files.list", AuditOutcome.Denied) >= 4);
        Assert.Equal(1, await AuditCountAsync("host.files.download", AuditOutcome.Denied));

        await tenant.CsrfAsync();
        var bytes = new ByteArrayContent("data"u8.ToArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await tenant.PostAsync($"/api/v1/hosts/{h1}/files/upload?groupId={folder}&folder={Uri.EscapeDataString(@"D:\Tenants\B")}&name=x.iso", bytes)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tenant.PostAsync($"/api/v1/hosts/{h1}/files/upload?groupId={folder}&folder={Uri.EscapeDataString(@"D:\Tenants\A\iso")}&name=x.iso",
            new ByteArrayContent("data"u8.ToArray()))).StatusCode);
        Assert.True(_app.Agent.Files.ContainsKey(@"D:\Tenants\A\iso\x.iso"));
    }

    // ------------------------------------------------------------------ users

    [Fact]
    public async Task Delete_user_removes_sessions_and_assignments_and_is_restricted()
    {
        var (hostId, _) = await _app.CreateHostAsync("du-host");
        var adminId = await _app.CreateUserAsync("du-admin", BuiltInRoles.PlatformAdmin);
        var victim = await _app.CreateUserAsync("du-victim", BuiltInRoles.VmOperator, ScopeType.Host, hostId.ToString("D"));
        await _app.CreateUserAsync("du-infra", BuiltInRoles.InfrastructureAdmin);
        var victimBrowser = await LoginAsync("du-victim");
        Assert.Equal(HttpStatusCode.OK, (await victimBrowser.GetAsync("/api/v1/vms")).StatusCode);

        // No users.manage: refused and audited, nothing removed.
        var infra = await LoginAsync("du-infra");
        Assert.Equal(HttpStatusCode.Forbidden, (await infra.PostJsonAsync($"/api/v1/users/{victim}/delete", new { confirmName = "du-victim" })).StatusCode);
        Assert.Equal(1, await AuditCountAsync("users.delete", AuditOutcome.Denied));

        var admin = await LoginAsync("du-admin");
        await admin.ReauthAsync();
        Assert.Equal("confirmation_required", await (await admin.PostJsonAsync($"/api/v1/users/{victim}/delete", new { confirmName = "DU-VICTIM" })).CodeAsync());
        Assert.Equal("self_management_forbidden", await (await admin.PostJsonAsync($"/api/v1/users/{adminId}/delete", new { confirmName = "du-admin" })).CodeAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostJsonAsync($"/api/v1/users/{Guid.NewGuid()}/delete", new { confirmName = "x" })).StatusCode);
        Assert.True(await _app.WithDbAsync(db => db.Users.AnyAsync(u => u.Id == victim)));

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostJsonAsync($"/api/v1/users/{victim}/delete", new { confirmName = "du-victim" })).StatusCode);
        var principal = victim.ToString("D");
        Assert.False(await _app.WithDbAsync(db => db.Users.AnyAsync(u => u.Id == victim)));
        Assert.False(await _app.WithDbAsync(db => db.Sessions.AnyAsync(s => s.UserId == victim)));
        Assert.False(await _app.WithDbAsync(db => db.RoleAssignments.AnyAsync(a => a.PrincipalId == principal)));
        Assert.Equal(1, await AuditCountAsync("users.delete", AuditOutcome.Success));
        // The open session of the deleted user stops working at once.
        Assert.Equal(HttpStatusCode.Unauthorized, (await victimBrowser.GetAsync("/api/v1/vms")).StatusCode);
        // The name can be used again.
        await _app.CreateUserAsync("du-victim");
    }

    // ------------------------------------------------------------------ automatic database copies

    [Fact]
    public async Task Automatic_database_copy_is_verified_pruned_after_seven_days_and_restricted()
    {
        await _app.CreateUserAsync("bk-admin", BuiltInRoles.PlatformAdmin);
        await _app.CreateUserAsync("bk-infra", BuiltInRoles.InfrastructureAdmin);
        var infra = await LoginAsync("bk-infra");
        Assert.Equal(HttpStatusCode.Forbidden, (await infra.GetAsync("/api/v1/settings/backup/auto")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await infra.PostJsonAsync("/api/v1/settings/backup/auto/run")).StatusCode);

        var admin = await LoginAsync("bk-admin");
        await admin.ReauthAsync();
        var status = await (await admin.GetAsync("/api/v1/settings/backup/auto")).JsonAsync();
        Assert.True(status.GetProperty("enabled").GetBoolean());
        Assert.Equal(7, status.GetProperty("retentionDays").GetInt32());
        Assert.Equal(24, status.GetProperty("intervalHours").GetInt32());
        var dir = status.GetProperty("directory").GetString()!;
        Assert.StartsWith(_app.DataDirectory, dir, StringComparison.OrdinalIgnoreCase);

        // Copies of 10 and 3 days ago: the first is older than the retention.
        Directory.CreateDirectory(dir);
        string Name(int daysAgo) => $"hvportal-auto-{DateTime.UtcNow.AddDays(-daysAgo):yyyyMMdd-HHmmss}.db";
        var expired = Path.Combine(dir, Name(10));
        var recent = Path.Combine(dir, Name(3));
        await File.WriteAllBytesAsync(expired, [1, 2, 3]);
        await File.WriteAllBytesAsync(recent, [1, 2, 3]);

        var run = await admin.PostJsonAsync("/api/v1/settings/backup/auto/run");
        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        var files = (await run.JsonAsync()).GetProperty("files").EnumerateArray().Select(f => f.GetProperty("name").GetString()!).ToList();
        Assert.Equal(2, files.Count);
        Assert.False(File.Exists(expired));
        Assert.True(File.Exists(recent));
        Assert.DoesNotContain(Directory.GetFiles(dir), f => f.EndsWith(".tmp", StringComparison.Ordinal));

        // The new copy is a consistent database with the portal's data.
        var copy = Path.Combine(dir, files[0]);
        await using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={copy};Mode=ReadOnly;Pooling=False"))
        {
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM Users WHERE UserName IN ('bk-admin', 'bk-infra')";
            Assert.Equal(2L, (long)(await cmd.ExecuteScalarAsync())!);
        }
        Assert.Equal(1, await AuditCountAsync("settings.backup.auto.run", AuditOutcome.Success));
    }

    // ------------------------------------------------------------------ checkpoints

    [Fact]
    public async Task Checkpoint_can_be_applied_to_a_running_vm_with_a_protocol_7_agent()
    {
        var (hostId, vms) = await _app.CreateHostAsync("cp-host", ("run1", VmPowerState.Running));
        await _app.CreateUserAsync("cp-admin", BuiltInRoles.PlatformAdmin);
        var c = await LoginAsync("cp-admin");
        var cp = Guid.NewGuid();
        var view = await (await c.GetAsync($"/api/v1/vms/{vms["run1"]}/checkpoints")).JsonAsync();
        Assert.True(view.GetProperty("applyWhileRunning").GetBoolean());
        Assert.Equal("confirmation_required", await (await c.PostJsonAsync($"/api/v1/vms/{vms["run1"]}/checkpoints/{cp}/apply", new { })).CodeAsync());
        var job = await JobAsync(await c.PostJsonAsync($"/api/v1/vms/{vms["run1"]}/checkpoints/{cp}/apply", new { confirmName = "run1" }));
        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Single(_app.Agent.OperationsReceived(HvOperation.RestoreCheckpoint));

        // An older agent does not turn the VM off first: there the VM must already be off.
        _app.Agent.ProtocolVersions[hostId] = 6;
        Assert.False((await (await c.GetAsync($"/api/v1/vms/{vms["run1"]}/checkpoints")).JsonAsync()).GetProperty("applyWhileRunning").GetBoolean());
        Assert.Equal("invalid_state", await (await c.PostJsonAsync($"/api/v1/vms/{vms["run1"]}/checkpoints/{cp}/apply", new { confirmName = "run1" })).CodeAsync());
    }
}
