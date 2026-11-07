using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using HvPortal.Contracts.Agent;
using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Security;
using HvPortal.Gateway.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HvPortal.Gateway.Tests.Integration;

/// <summary>
/// v0.7.0 (agent protocol 9) without backups: networks with VLAN, host health, VM move, storage folders, new folders and
/// agent updates from the portal. Hyper-V is simulated by the test double; what the host really does is in the
/// real-host test plan.
/// </summary>
public sealed class Protocol9Tests : IAsyncLifetime
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

    private async Task<HttpClient> UserAsync(string name, string role)
    {
        await _app.CreateUserAsync(name, role);
        var c = _app.CreateBrowser();
        await c.LoginAsync(name);
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

    private async Task<Job> JobAsync(HttpResponseMessage accepted)
    {
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var id = (await accepted.JsonAsync()).GetProperty("jobId").GetGuid();
        await _app.RunJobsAsync();
        return await _app.WithDbAsync(db => db.Jobs.AsNoTracking().FirstAsync(j => j.Id == id));
    }

    private Task<int> DeniedAsync(string action) =>
        _app.WithDbAsync(db => db.AuditEntries.CountAsync(a => a.Action == action && a.Outcome == AuditOutcome.Denied));

    // ------------------------------------------------------------------ networks with VLAN

    [Fact]
    public async Task A_network_is_a_switch_with_a_vlan_and_vms_put_on_it_get_that_vlan()
    {
        var (hostId, vms) = await _app.CreateHostAsync("net-host", ("web01", VmPowerState.Off));
        var admin = await UserAsync("net-admin", BuiltInRoles.InfrastructureAdmin);

        // Only network administrators of the host; the switch must exist; VLAN 1-4094.
        var reader = await UserAsync("net-reader", BuiltInRoles.ReadOnly);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostJsonAsync($"/api/v1/hosts/{hostId}/networks", new { name = "Prod", switchName = "vSwitch", vlanId = 20 })).StatusCode);
        Assert.Equal(1, await DeniedAsync("network.portal.create"));
        Assert.Equal("unknown_switch", await (await admin.PostJsonAsync($"/api/v1/hosts/{hostId}/networks", new { name = "Prod", switchName = "nope", vlanId = 20 })).CodeAsync());
        Assert.Equal("invalid_vlan", await (await admin.PostJsonAsync($"/api/v1/hosts/{hostId}/networks", new { name = "Prod", switchName = "vSwitch", vlanId = 5000 })).CodeAsync());
        var created = await admin.PostJsonAsync($"/api/v1/hosts/{hostId}/networks", new { name = "Prod", switchName = "vSwitch", vlanId = 20, description = "Production" });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var networkId = (await created.JsonAsync()).GetProperty("id").GetGuid();
        Assert.Equal("network_exists", await (await admin.PostJsonAsync($"/api/v1/hosts/{hostId}/networks", new { name = "Prod2", switchName = "vSwitch", vlanId = 20 })).CodeAsync());
        Assert.Equal("network_name_exists", await (await admin.PostJsonAsync($"/api/v1/hosts/{hostId}/networks", new { name = "Prod", switchName = "vSwitch", vlanId = 30 })).CodeAsync());
        var listed = await (await reader.GetAsync($"/api/v1/hosts/{hostId}/networks")).JsonAsync();
        Assert.Equal(20, Assert.Single(listed.EnumerateArray()).GetProperty("vlanId").GetInt32());

        // A VM adapter moved to the network: the job carries the switch and VLAN of the network.
        var job = await JobAsync(await PatchAsync(admin, $"/api/v1/vms/{vms["web01"]}/config",
            new { networkAdapterChanges = new[] { new { adapterId = "Microsoft:NIC1", switchName = "ignored", vlanId = 999, networkId } } }));
        Assert.Equal(JobStatus.Succeeded, job.Status);
        var sent = HvOperations.FromElement<VmConfigChange>(_app.Agent.OperationsReceived(HvOperation.SetVmConfig).Last().OperationArgs)!;
        var nic = Assert.Single(sent.NetworkAdapterChanges!);
        Assert.Equal(("vSwitch", 20), (nic.SwitchName, nic.VlanId));

        // A new adapter on the network gets the VLAN too (agent 0.7.0+ only).
        await JobAsync(await PatchAsync(admin, $"/api/v1/vms/{vms["web01"]}/config", new { addNetworkAdapterSwitch = "", addNetworkAdapterNetworkId = networkId }));
        var added = HvOperations.FromElement<VmConfigChange>(_app.Agent.OperationsReceived(HvOperation.SetVmConfig).Last().OperationArgs)!;
        Assert.Equal(("vSwitch", 20), (added.AddNetworkAdapterSwitch, added.AddNetworkAdapterVlanId));
        _app.Agent.ProtocolVersions[hostId] = 8;
        Assert.Equal("agent_outdated", await (await PatchAsync(admin, $"/api/v1/vms/{vms["web01"]}/config", new { addNetworkAdapterSwitch = "", addNetworkAdapterNetworkId = networkId })).CodeAsync());
        Assert.Equal("agent_outdated", await (await admin.PostJsonAsync($"/api/v1/hosts/{hostId}/vms", new { vm = new { name = "new01", networkId } })).CodeAsync());
        _app.Agent.ProtocolVersions.TryRemove(hostId, out _);

        // A new VM on the network: switch and VLAN of the network in the creation command.
        var newVm = await JobAsync(await admin.PostJsonAsync($"/api/v1/hosts/{hostId}/vms", new { vm = new { name = "new01", generation = 2, processorCount = 2, memoryStartupMb = 2048, diskSizeGb = 0, networkId } }));
        Assert.Equal(JobStatus.Succeeded, newVm.Status);
        var create = HvOperations.FromElement<NewVmRequest>(_app.Agent.OperationsReceived(HvOperation.NewVm).Last().OperationArgs)!;
        Assert.Equal(("vSwitch", (int?)20), (create.SwitchName, create.VlanId));
        Assert.Equal(9, AgentProtocol.RequiredVersion(_app.Agent.OperationsReceived(HvOperation.NewVm).Last()));
        Assert.Equal("unknown_network", await (await admin.PostJsonAsync($"/api/v1/hosts/{hostId}/vms", new { vm = new { name = "new02", networkId = Guid.NewGuid() } })).CodeAsync());

        // Changing the VLAN of the network re-tags the adapters on it now (read from the host): one job per VM.
        var changed = await PutAsync(admin, $"/api/v1/networks/{networkId}", new { name = "Prod", vlanId = 21, updateAdapters = true });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(0, (await changed.JsonAsync()).GetProperty("jobs").GetArrayLength()); // the simulated adapter is untagged, not on VLAN 20

        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteWithCsrfAsync($"/api/v1/networks/{networkId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteWithCsrfAsync($"/api/v1/networks/{networkId}")).StatusCode);
        Assert.True(await _app.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "network.portal.delete")));
    }

    [Fact]
    public async Task Changing_the_vlan_of_a_network_retags_only_adapters_on_it()
    {
        var (hostId, vms) = await _app.CreateHostAsync("retag-host", ("app01", VmPowerState.Running));
        var admin = await UserAsync("retag-admin", BuiltInRoles.InfrastructureAdmin);
        // The simulated adapter is on vSwitch untagged: a network "vSwitch untagged" owns it.
        var id = (await (await admin.PostJsonAsync($"/api/v1/hosts/{hostId}/networks", new { name = "Untagged", switchName = "vSwitch" })).JsonAsync()).GetProperty("id").GetGuid();
        var r = await (await PutAsync(admin, $"/api/v1/networks/{id}", new { name = "Untagged", vlanId = 30, updateAdapters = true })).JsonAsync();
        var queued = Assert.Single(r.GetProperty("jobs").EnumerateArray());
        Assert.Equal(vms["app01"], queued.GetProperty("vmId").GetGuid());
        await _app.RunJobsAsync();
        var change = HvOperations.FromElement<VmConfigChange>(_app.Agent.OperationsReceived(HvOperation.SetVmConfig).Last().OperationArgs)!;
        Assert.Equal(30, Assert.Single(change.NetworkAdapterChanges!).VlanId);
    }

    // ------------------------------------------------------------------ host health

    [Fact]
    public async Task Host_health_is_read_live_for_who_reads_the_host_and_reports_the_worst_state()
    {
        var (hostId, healthVms) = await _app.CreateHostAsync("health-host", ("vm1", VmPowerState.Running));
        var admin = await UserAsync("health-admin", BuiltInRoles.InfrastructureAdmin);
        var ok = await (await admin.GetAsync($"/api/v1/hosts/{hostId}/health")).JsonAsync();
        Assert.Equal("Ok", ok.GetProperty("status").GetString());

        _app.Agent.Health = new HostHealthReport
        {
            PhysicalDisks = [new PhysicalDiskHealth { Name = "Disk 1", HealthStatus = "Unhealthy", Status = HealthLevel.Critical }],
            Volumes = [new VolumeHealth { Name = "D:", Status = HealthLevel.Warning }],
            Services = [new ServiceHealth("vmms", null, "Running", "Automatic")],
            Errors = ["storagePools: access denied"],
        };
        Assert.Equal("Critical", (await (await admin.GetAsync($"/api/v1/hosts/{hostId}/health")).JsonAsync()).GetProperty("status").GetString());
        _app.Agent.Health = new HostHealthReport { Errors = ["physicalDisks: access denied"], Services = [new ServiceHealth("vmms", null, "Running", null)] };
        // Nothing read is never "Ok".
        Assert.Equal("Unknown", (await (await admin.GetAsync($"/api/v1/hosts/{hostId}/health")).JsonAsync()).GetProperty("status").GetString());

        // A user who sees only one VM of the host does not see the host's hardware.
        var vmUser = await _app.CreateUserAsync("health-vm-user", BuiltInRoles.ReadOnly, ScopeType.Vm, healthVms["vm1"].ToString("D"));
        var vmOnly = _app.CreateBrowser();
        await vmOnly.LoginAsync("health-vm-user");
        Assert.Equal(HttpStatusCode.OK, (await vmOnly.GetAsync($"/api/v1/vms/{healthVms["vm1"]}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await vmOnly.GetAsync($"/api/v1/hosts/{hostId}/health")).StatusCode);
        _ = vmUser;
        _app.Agent.ProtocolVersions[hostId] = 8;
        Assert.Equal("agent_outdated", await (await admin.GetAsync($"/api/v1/hosts/{hostId}/health")).CodeAsync());
    }

    // ------------------------------------------------------------------ VM move

    [Fact]
    public async Task A_vm_moved_to_another_host_keeps_its_portal_record_and_needs_vm_create_there()
    {
        var (source, vms) = await _app.CreateHostAsync("move-src", ("db01", VmPowerState.Running));
        var (destination, _) = await _app.CreateHostAsync("move-dst");
        await _app.WithDbAsync(async db =>
        {
            var h = await db.Hosts.FirstAsync(x => x.Id == destination);
            h.HostFactsJson = JsonSerializer.Serialize(new HostFacts { ComputerName = "MOVE-DST", DnsHostName = "move-dst.contoso.local", AgentVersion = "0.7.0", VirtualSwitches = ["vSwitch"] });
            await db.SaveChangesAsync();
        });
        var vm = vms["db01"];

        // vm.migrate on the VM AND vm.create on the destination host.
        var operatorId = await _app.CreateUserAsync("move-op", BuiltInRoles.VmOperator);
        var op = _app.CreateBrowser();
        await op.LoginAsync("move-op");
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostJsonAsync($"/api/v1/vms/{vm}/move", new { mode = "host", destinationHostId = destination, destinationPath = @"D:\VMs" })).StatusCode);
        Assert.Equal(1, await DeniedAsync("vm.migrate.request"));

        var admin = await UserAsync("move-admin", BuiltInRoles.InfrastructureAdmin);
        Assert.Equal("destination_path_required", await (await admin.PostJsonAsync($"/api/v1/vms/{vm}/move", new { mode = "host", destinationHostId = destination })).CodeAsync());
        Assert.Equal("invalid_destination_host", await (await admin.PostJsonAsync($"/api/v1/vms/{vm}/move", new { mode = "host", destinationHostId = source, destinationPath = @"D:\VMs" })).CodeAsync());
        _app.Agent.ProtocolVersions[source] = 8;
        Assert.Equal("agent_outdated", await (await admin.PostJsonAsync($"/api/v1/vms/{vm}/move", new { mode = "host", destinationHostId = destination, destinationPath = @"D:\VMs" })).CodeAsync());
        _app.Agent.ProtocolVersions.TryRemove(source, out _);

        // The destination inventory may have reported the VM before the job ends: the portal record wins.
        await _app.WithDbAsync(async db =>
        {
            db.VirtualMachines.Add(new VirtualMachine { HostId = destination, HyperVId = await db.VirtualMachines.Where(v => v.Id == vm).Select(v => v.HyperVId).FirstAsync(),
                Name = "db01", FirstSeenAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        });
        var groupId = (await (await admin.PostJsonAsync("/api/v1/groups", new { name = "Databases" })).JsonAsync()).GetProperty("id").GetGuid();
        Assert.True((await admin.PostJsonAsync($"/api/v1/groups/{groupId}/members", new { vmIds = new[] { vm } })).IsSuccessStatusCode);

        var job = await JobAsync(await admin.PostJsonAsync($"/api/v1/vms/{vm}/move", new { mode = "host", destinationHostId = destination, destinationPath = @"D:\VMs" }));
        Assert.Equal(JobStatus.Succeeded, job.Status);
        var move = HvOperations.FromElement<MoveVmRequest>(_app.Agent.OperationsReceived(HvOperation.MoveVm).Single().OperationArgs)!;
        Assert.Equal(("move-dst.contoso.local", @"D:\VMs", true), (move.DestinationHost, move.DestinationPath, move.IncludeStorage));
        var rows = await _app.WithDbAsync(db => db.VirtualMachines.AsNoTracking().Where(v => v.Name == "db01").ToListAsync());
        var row = Assert.Single(rows);
        Assert.Equal((vm, destination), (row.Id, row.HostId));
        Assert.True(await _app.WithDbAsync(db => db.LogicalGroupMembers.AnyAsync(m => m.VmId == vm && m.GroupId == groupId)));
        _ = operatorId;
    }

    [Fact]
    public async Task Moving_the_files_of_a_vm_follows_the_storage_rules_of_its_host()
    {
        var (hostId, vms) = await _app.CreateHostAsync("storage-move", ("app01", VmPowerState.Running));
        var admin = await UserAsync("storage-move-admin", BuiltInRoles.InfrastructureAdmin);
        Assert.Equal("location_not_allowed", await (await admin.PostJsonAsync($"/api/v1/vms/{vms["app01"]}/move", new { mode = "storage", destinationPath = @"C:\Windows\VMs" })).CodeAsync());
        // Allowed folders reported by the agent of the host.
        await _app.WithDbAsync(async db =>
        {
            var h = await db.Hosts.FirstAsync(x => x.Id == hostId);
            h.HostFactsJson = JsonSerializer.Serialize(new HostFacts { ComputerName = "S", AgentVersion = "0.7.0", VirtualSwitches = ["vSwitch"], StorageFolders = [@"D:\VMs"] });
            await db.SaveChangesAsync();
        });
        Assert.Equal("location_not_allowed", await (await admin.PostJsonAsync($"/api/v1/vms/{vms["app01"]}/move", new { mode = "storage", destinationPath = @"E:\VMs" })).CodeAsync());
        var job = await JobAsync(await admin.PostJsonAsync($"/api/v1/vms/{vms["app01"]}/move", new { mode = "storage", destinationPath = @"D:\VMs\Fast" }));
        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Equal(@"D:\VMs\Fast\vm", await _app.WithDbAsync(db => db.VirtualMachines.Where(v => v.Id == vms["app01"]).Select(v => v.ConfigurationPath).FirstAsync()));
    }

    // ------------------------------------------------------------------ storage folders and new folders

    [Fact]
    public async Task Storage_folders_are_decided_on_the_host_except_for_remote_hosts()
    {
        var (hostId, _) = await _app.CreateHostAsync("sf-agent-host");
        var admin = await UserAsync("sf-admin", BuiltInRoles.PlatformAdmin);
        Assert.Equal("decided_on_the_host", await (await PutAsync(admin, $"/api/v1/hosts/{hostId}/storage-folders", new { folders = new[] { @"D:\VMs" } })).CodeAsync());
        await _app.WithDbAsync(async db =>
        {
            var h = await db.Hosts.FirstAsync(x => x.Id == hostId);
            h.ConnectionMode = HostConnectionMode.Remote;
            await db.SaveChangesAsync();
        });
        Assert.Equal("invalid_storage_folder", await (await PutAsync(admin, $"/api/v1/hosts/{hostId}/storage-folders", new { folders = new[] { @"C:\Windows" } })).CodeAsync());
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(admin, $"/api/v1/hosts/{hostId}/storage-folders", new { folders = new[] { @"d:\vms\", @"E:\ISO" } })).StatusCode);
        var host = await (await admin.GetAsync($"/api/v1/hosts/{hostId}")).JsonAsync();
        Assert.Equal([@"D:\vms", @"E:\ISO"], host.GetProperty("host").GetProperty("storageFolders").EnumerateArray().Select(x => x.GetString()!).ToArray());
        var reader = await UserAsync("sf-reader", BuiltInRoles.ReadOnly);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(reader, $"/api/v1/hosts/{hostId}/storage-folders", new { folders = Array.Empty<string>() })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(reader, $"/api/v1/hosts/{hostId}/backup-staging", new { path = @"D:\vms\staging" })).StatusCode);
        Assert.Equal("location_not_allowed", await (await PutAsync(admin, $"/api/v1/hosts/{hostId}/backup-staging", new { path = @"F:\Staging" })).CodeAsync());
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(admin, $"/api/v1/hosts/{hostId}/backup-staging", new { path = @"D:\vms\staging" })).StatusCode);

        // A management agent older than 0.7.0 would ignore the list: the portal refuses to set it instead of pretending.
        await _app.WithDbAsync(async db =>
        {
            var agent = new ManagementAgent { DisplayName = "old-mgmt", AgentVersion = "0.6.0", Status = HostStatus.Active };
            db.ManagementAgents.Add(agent);
            (await db.Hosts.FirstAsync(x => x.Id == hostId)).ManagementAgentId = agent.Id;
            await db.SaveChangesAsync();
        });
        Assert.Equal("agent_outdated", await (await PutAsync(admin, $"/api/v1/hosts/{hostId}/storage-folders", new { folders = new[] { @"D:\VMs" } })).CodeAsync());
    }

    [Fact]
    public async Task New_folders_need_write_access_and_agent_0_7()
    {
        var (hostId, _) = await _app.CreateHostAsync("folder-host");
        var admin = await UserAsync("folder-admin", BuiltInRoles.InfrastructureAdmin);
        var created = await admin.PostJsonAsync($"/api/v1/hosts/{hostId}/files/folders", new { parent = @"D:\Virtual Machines", name = "Project A" });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Contains("Project A", _app.Agent.Folders[@"D:\Virtual Machines"]);
        Assert.Equal("file_exists", await (await admin.PostJsonAsync($"/api/v1/hosts/{hostId}/files/folders", new { parent = @"D:\Virtual Machines", name = "Project A" })).CodeAsync());
        Assert.Equal("location_not_allowed", await (await admin.PostJsonAsync($"/api/v1/hosts/{hostId}/files/folders", new { parent = @"C:\", name = "Windows" })).CodeAsync());
        var reader = await UserAsync("folder-reader", BuiltInRoles.ReadOnly);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostJsonAsync($"/api/v1/hosts/{hostId}/files/folders", new { parent = @"D:\", name = "x" })).StatusCode);
        _app.Agent.ProtocolVersions[hostId] = 8;
        Assert.Equal("agent_outdated", await (await admin.PostJsonAsync($"/api/v1/hosts/{hostId}/files/folders", new { parent = @"D:\", name = "y" })).CodeAsync());
    }

    // ------------------------------------------------------------------ agent updates

    private async Task PackageAsync(string version, bool catalogSigned)
    {
        Directory.CreateDirectory(_app.PackagesDirectory);
        await File.WriteAllBytesAsync(Path.Combine(_app.PackagesDirectory, "HvPortal.Agent-win-x64.zip"), RandomNumberGenerator.GetBytes(2048));
        await File.WriteAllTextAsync(Path.Combine(_app.PackagesDirectory, "HvPortal.Agent-win-x64.json"),
            $$"""{ "version": "{{version}}", "builtAtUtc": "2026-11-07T00:00:00Z", "signed": true, "signer": "CN=NEXQ S.r.l.", "selfSigned": true, "catalogSigned": {{(catalogSigned ? "true" : "false")}} }""");
    }

    [Fact]
    public async Task Outdated_agents_are_listed_and_updated_from_the_portal_only_when_the_update_can_be_checked_on_the_host()
    {
        var (hostId, _) = await _app.CreateHostAsync("upd-host");
        var (oldId, _) = await _app.CreateHostAsync("upd-old");
        await _app.WithDbAsync(async db =>
        {
            foreach (var h in await db.Hosts.ToListAsync())
            {
                h.AgentUpdaterInstalled = true;
            }
            await db.SaveChangesAsync();
        });
        _app.Agent.ProtocolVersions[oldId] = 8;
        _app.Agent.AgentVersions[oldId] = "0.6.0";
        var admin = await UserAsync("upd-admin", BuiltInRoles.InfrastructureAdmin);
        var reader = await UserAsync("upd-reader", BuiltInRoles.ReadOnly);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/v1/agents/updates")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostJsonAsync($"/api/v1/hosts/{hostId}/agent/update")).StatusCode);

        Assert.Equal("package_missing", await (await admin.PostJsonAsync($"/api/v1/hosts/{hostId}/agent/update")).CodeAsync());
        await PackageAsync("0.7.1", catalogSigned: false);
        var status = await (await admin.GetAsync("/api/v1/agents/updates")).JsonAsync();
        Assert.Equal(2, status.GetProperty("outdated").GetInt32());
        var agents = status.GetProperty("agents").EnumerateArray().ToDictionary(a => a.GetProperty("name").GetString()!);
        Assert.Equal("package_unsigned", agents["upd-host"].GetProperty("reason").GetString());
        Assert.Equal("agent_too_old", agents["upd-old"].GetProperty("reason").GetString());
        Assert.Equal("package_unsigned", await (await admin.PostJsonAsync($"/api/v1/hosts/{hostId}/agent/update")).CodeAsync());

        await PackageAsync("0.7.1", catalogSigned: true);
        _app.Agent.VersionAfterUpdate = "0.7.1";
        var job = await JobAsync(await admin.PostJsonAsync($"/api/v1/hosts/{hostId}/agent/update"));
        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Contains("0.7.1", job.ResultMessage);
        var update = _app.Agent.Received.Last(c => c.Kind == AgentCommandKind.UpdateAgent).Update!;
        Assert.Equal("0.7.1", update.Version);
        Assert.Equal(64, update.Sha256.Length);
        Assert.Equal("up_to_date", await (await admin.PostJsonAsync($"/api/v1/hosts/{hostId}/agent/update")).CodeAsync());

        // "Update all" queues only the agents that can be updated; the others are listed with the reason.
        var all = await (await admin.PostJsonAsync("/api/v1/agents/update-outdated")).JsonAsync();
        Assert.Equal(0, all.GetProperty("queued").GetArrayLength());
        Assert.Equal("agent_too_old", Assert.Single(all.GetProperty("skipped").EnumerateArray()).GetProperty("reason").GetString());
        Assert.True(await _app.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "agent.update.request" && a.Outcome == AuditOutcome.Success)));
    }

    [Fact]
    public async Task The_agent_package_on_the_agent_port_needs_an_agent_certificate()
    {
        // Without a client certificate of the internal CA nothing is served (the in-memory test server has none).
        var anonymous = _app.CreateBrowser();
        var r = await anonymous.GetAsync("/agent/v1/package");
        Assert.True(r.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, r.StatusCode.ToString());
    }
}
