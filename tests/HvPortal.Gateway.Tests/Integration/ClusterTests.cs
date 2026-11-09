using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Agents;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Jobs;
using HvPortal.Gateway.Security;
using HvPortal.Gateway.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HvPortal.Gateway.Tests.Integration;

/// <summary>
/// Failover clusters (ADR-041, agent protocol 11): clusters of hosts, the cluster read through any node agent, node
/// maintenance, highly available VMs, moves inside the cluster and VMs that follow a failover. The cluster is
/// simulated by the test double; what the cluster cmdlets really do is in the real-host test plan.
/// </summary>
public sealed class ClusterTests : IAsyncLifetime
{
    private const long Gb = 1024L * 1024 * 1024;
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

    private async Task<HttpClient> UserAsync(string name, string? role, ScopeType scope = ScopeType.Global, string scopeId = "")
    {
        await _app.CreateUserAsync(name, role, scope, scopeId);
        var c = _app.CreateBrowser();
        await c.LoginAsync(name);
        return c;
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

    private static ClusterStatusReport Report(params (string Name, string State)[] nodes) => new()
    {
        CollectedUtc = DateTimeOffset.UtcNow,
        Name = "CL01",
        Nodes = nodes.Select(n => new ClusterNodeStatus { Name = n.Name, State = n.State, NodeWeight = 1, DynamicWeight = 1 }).ToList(),
        Quorum = new ClusterQuorumStatus { QuorumType = "Majority", WitnessType = "FileShare", WitnessName = "File Share Witness", WitnessState = "Online" },
        SharedVolumes = [new ClusterSharedVolumeStatus { Name = "Volume1", Path = @"C:\ClusterStorage\Volume1", State = "Online", SizeBytes = 1000 * Gb, FreeBytes = 500 * Gb, Access = "Direct" }],
        Storage = new ClusterStorageStatus
        {
            S2dEnabled = true,
            Pools = [new ClusterPoolStatus { Name = "S2D on CL01", HealthStatus = "Healthy", SizeBytes = 8000 * Gb, AllocatedBytes = 2000 * Gb }],
            VirtualDisks = [new ClusterVirtualDiskStatus { Name = "Volume1", HealthStatus = "Healthy", OperationalStatus = "OK" }],
        },
    };

    /// <summary>A cluster "CL01" in the portal with two nodes HV01 and HV02 (agents connected, 0.9.0), VMs on HV01.</summary>
    private async Task<(Guid ClusterId, Guid Hv01, Guid Hv02, Dictionary<string, Guid> Vms)> ClusterAsync(params (string Name, VmPowerState State)[] vms)
    {
        var (hv01, created) = await _app.CreateHostAsync("hv01", vms);
        var (hv02, _) = await _app.CreateHostAsync("hv02");
        var clusterId = Guid.NewGuid();
        await _app.WithDbAsync(async db =>
        {
            db.Clusters.Add(new HostCluster { Id = clusterId, Name = "Cluster 01", CreatedAt = DateTimeOffset.UtcNow, WindowsName = "CL01" });
            foreach (var (id, node) in new[] { (hv01, "HV01"), (hv02, "HV02") })
            {
                var h = await db.Hosts.FirstAsync(x => x.Id == id);
                h.ClusterId = clusterId;
                h.AgentVersion = "0.9.0";
                h.HostFactsJson = JsonSerializer.Serialize(NodeFacts(node));
            }
            foreach (var v in db.VirtualMachines.Where(v => v.HostId == hv01))
            {
                v.IsClustered = true;
                v.MemoryAssignedMb = 4096;
                v.ProcessorCount = 2;
            }
            await db.SaveChangesAsync();
        });
        _app.Agent.ClusterNodeHosts["HV01"] = hv01;
        _app.Agent.ClusterNodeHosts["HV02"] = hv02;
        _app.Agent.Cluster = Report(("HV01", "Up"), ("HV02", "Up"));
        return (clusterId, hv01, hv02, created);
    }

    private static HostFacts NodeFacts(string node, string cluster = "CL01") => new()
    {
        ComputerName = node, AgentVersion = "0.9.0", VirtualSwitches = ["vSwitch"], ClusterServicePresent = true,
        TotalPhysicalMemoryBytes = 256 * Gb, FreePhysicalMemoryBytes = 128 * Gb, LogicalProcessorCount = 32,
        Cluster = new ClusterFacts
        {
            Name = cluster, NodeName = node, NodeState = "Up",
            SharedVolumes = [new HostVolume(@"C:\ClusterStorage\Volume1", "Volume1", "CSVFS", 1000 * Gb, 500 * Gb)],
        },
    };

    // ------------------------------------------------------------------ create and manage

    [Fact]
    public async Task Creating_a_cluster_needs_hosts_manage_and_gives_every_node_its_own_agent_enrollment()
    {
        var reader = await UserAsync("cl-reader", BuiltInRoles.ReadOnly);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostJsonAsync("/api/v1/clusters", new { name = "Cluster A", newNodes = new[] { "node-a1" } })).StatusCode);
        Assert.Equal(1, await DeniedAsync("clusters.create"));

        var admin = await UserAsync("cl-admin", BuiltInRoles.InfrastructureAdmin);
        await admin.ReauthAsync();
        var (existing, _) = await _app.CreateHostAsync("node-a0");
        Assert.Equal("invalid_display_name", await (await admin.PostJsonAsync("/api/v1/clusters", new { name = "Cluster A", newNodes = new[] { "a", "a" } })).CodeAsync());
        var r = await admin.PostJsonAsync("/api/v1/clusters", new { name = "Cluster A", newNodes = new[] { "node-a1", "node-a2" }, hostIds = new[] { existing } });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.JsonAsync();
        var clusterId = body.GetProperty("clusterId").GetGuid();
        var nodes = body.GetProperty("nodes").EnumerateArray().ToList();
        Assert.Equal(2, nodes.Count);
        foreach (var n in nodes)
        {
            var enrollment = n.GetProperty("enrollment");
            Assert.True(enrollment.GetProperty("token").GetString()!.Length > 20);
            // The agent account of a node also gets access to the cluster.
            Assert.Contains("-ClusterAccess Full", enrollment.GetProperty("command").GetString(), StringComparison.Ordinal);
        }
        var hosts = await _app.WithDbAsync(db => db.Hosts.AsNoTracking().Where(h => h.ClusterId == clusterId).ToListAsync());
        Assert.Equal(3, hosts.Count);
        Assert.Equal(2, hosts.Count(h => h.Status == HostStatus.Pending));
        Assert.Equal(1, await _app.WithDbAsync(db => db.AuditEntries.CountAsync(a => a.Action == "clusters.create" && a.Outcome == AuditOutcome.Success)));

        // A host can be in one cluster only.
        Assert.Equal("already_in_cluster", await (await admin.PostJsonAsync("/api/v1/clusters", new { name = "Cluster B", hostIds = new[] { existing } })).CodeAsync());
        Assert.Equal("cluster_exists", await (await admin.PostJsonAsync("/api/v1/clusters", new { name = "Cluster A" })).CodeAsync());

        // Removing a node keeps the host (standalone); deleting the cluster keeps all hosts and drops role assignments on it.
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostJsonAsync($"/api/v1/clusters/{clusterId}/nodes/{existing}/remove")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostJsonAsync($"/api/v1/clusters/{clusterId}/nodes/{existing}/remove")).StatusCode);
        Assert.Null(await _app.WithDbAsync(db => db.Hosts.Where(h => h.Id == existing).Select(h => h.ClusterId).FirstAsync()));
        var viewer = await _app.CreateUserAsync("cl-scoped", BuiltInRoles.ReadOnly, ScopeType.Cluster, clusterId.ToString("D"));
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostJsonAsync($"/api/v1/clusters/{clusterId}/delete")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostJsonAsync($"/api/v1/clusters/{clusterId}/delete")).StatusCode);
        Assert.Equal(3, await _app.WithDbAsync(db => db.Hosts.CountAsync(h => h.DisplayName.StartsWith("node-a"))));
        Assert.Equal(0, await _app.WithDbAsync(db => db.RoleAssignments.CountAsync(a => a.PrincipalId == viewer.ToString("D") && a.ScopeType == ScopeType.Cluster)));
    }

    [Fact]
    public async Task A_standalone_host_works_as_before_and_has_no_cluster_actions()
    {
        var (hostId, _) = await _app.CreateHostAsync("solo");
        var admin = await UserAsync("solo-admin", BuiltInRoles.InfrastructureAdmin);
        var host = (await (await admin.GetAsync($"/api/v1/hosts/{hostId}")).JsonAsync()).GetProperty("host");
        Assert.Equal(JsonValueKind.Null, host.GetProperty("clusterId").ValueKind);
        Assert.False(host.GetProperty("canMaintain").GetBoolean());
        Assert.Equal("not_in_cluster", await (await admin.PostJsonAsync($"/api/v1/hosts/{hostId}/maintenance", new { action = "Enter" })).CodeAsync());
        Assert.Equal("not_in_cluster", await (await admin.PostJsonAsync($"/api/v1/hosts/{hostId}/vms",
            new { vm = new { name = "ha-solo", highlyAvailable = true, location = @"C:\ClusterStorage\Volume1", diskSizeGb = 0 } })).CodeAsync());
        // Normal creation is unchanged and never asks the cluster.
        var job = await JobAsync(await admin.PostJsonAsync($"/api/v1/hosts/{hostId}/vms", new { vm = new { name = "plain", diskSizeGb = 0 } }));
        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Empty(_app.Agent.OperationsReceived(HvOperation.GetClusterStatus));
        Assert.False(HvOperations.FromElement<NewVmRequest>(_app.Agent.OperationsReceived(HvOperation.NewVm).Single().OperationArgs)!.HighlyAvailable);
    }

    // ------------------------------------------------------------------ status

    [Fact]
    public async Task The_cluster_is_read_through_another_node_when_one_agent_does_not_answer()
    {
        var (clusterId, hv01, hv02, _) = await ClusterAsync(("web01", VmPowerState.Running));
        var admin = await UserAsync("st-admin", BuiltInRoles.InfrastructureAdmin);
        _app.Agent.ClusterStatusFails[hv01] = "timed out";
        var status = (await (await admin.GetAsync($"/api/v1/clusters/{clusterId}/status")).JsonAsync()).GetProperty("status");
        Assert.Equal("HV02", status.GetProperty("readByNode").GetString());
        Assert.Contains(status.GetProperty("attempts").EnumerateArray(), a => a.GetString()!.Contains("timed out", StringComparison.Ordinal));
        Assert.Equal("Ok", status.GetProperty("level").GetString());
        var stored = await _app.WithDbAsync(db => db.Clusters.AsNoTracking().FirstAsync(c => c.Id == clusterId));
        Assert.Equal("Ok", stored.LastStatusLevel);
        Assert.NotNull(stored.LastStatusJson);

        // The node agent HV02 stops too: nothing can be read; never reported as the last good status.
        _app.Agent.DisconnectHost(hv02);
        var unreadable = (await (await admin.GetAsync($"/api/v1/clusters/{clusterId}/status")).JsonAsync()).GetProperty("status");
        Assert.NotEqual("Ok", unreadable.GetProperty("level").GetString());
        Assert.Contains(unreadable.GetProperty("issues").EnumerateArray(), i => i.GetProperty("code").GetString() == "status_unreadable");
        Assert.Contains(unreadable.GetProperty("issues").EnumerateArray(), i => i.GetProperty("code").GetString() == "agent_disconnected");

        // Problems and capacity: a node down is critical.
        _app.Agent.Connect(hv02);
        _app.Agent.ClusterStatusFails.Clear();
        _app.Agent.Cluster = Report(("HV01", "Up"), ("HV02", "Down"));
        var down = (await (await admin.GetAsync($"/api/v1/clusters/{clusterId}/status")).JsonAsync()).GetProperty("status");
        Assert.Equal("Critical", down.GetProperty("level").GetString());
        Assert.Contains(down.GetProperty("issues").EnumerateArray(), i => i.GetProperty("code").GetString() == "node_down");
        Assert.True(down.GetProperty("capacity").GetProperty("totalMemoryBytes").GetInt64() > 0);
    }

    [Fact]
    public async Task Cluster_role_assignments_apply_to_its_nodes_and_vms_and_the_status_needs_the_cluster()
    {
        var (clusterId, hv01, _, vms) = await ClusterAsync(("app01", VmPowerState.Running));
        var clusterReader = await UserAsync("scope-cl", BuiltInRoles.ReadOnly, ScopeType.Cluster, clusterId.ToString("D"));
        var hostReader = await UserAsync("scope-host", BuiltInRoles.ReadOnly, ScopeType.Host, hv01.ToString("D"));
        var nobody = await UserAsync("scope-none", null);

        Assert.Equal(HttpStatusCode.OK, (await clusterReader.GetAsync($"/api/v1/vms/{vms["app01"]}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await clusterReader.GetAsync($"/api/v1/clusters/{clusterId}")).StatusCode);
        Assert.Equal(2, (await (await clusterReader.GetAsync("/api/v1/hosts")).JsonAsync()).GetArrayLength());

        // Who reads only one node sees the cluster in the tree, not its status.
        var list = (await (await hostReader.GetAsync("/api/v1/clusters")).JsonAsync()).EnumerateArray().Single();
        Assert.False(list.GetProperty("can").GetProperty("readStatus").GetBoolean());
        Assert.Equal(JsonValueKind.Null, list.GetProperty("status").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await hostReader.GetAsync($"/api/v1/clusters/{clusterId}")).StatusCode);
        Assert.Equal(0, (await (await nobody.GetAsync("/api/v1/clusters")).JsonAsync()).GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await nobody.GetAsync($"/api/v1/clusters/{clusterId}/status")).StatusCode);

        // A role assignment on the cluster scope is accepted by the API (and refused for an unknown cluster).
        var admin = await UserAsync("scope-admin", BuiltInRoles.PlatformAdmin);
        await admin.ReauthAsync();
        var target = await _app.CreateUserAsync("scope-target");
        var roleId = await _app.WithDbAsync(db => db.Roles.Where(r => r.BuiltInKey == BuiltInRoles.ReadOnly).Select(r => r.Id).FirstAsync());
        Assert.Equal(HttpStatusCode.OK, (await admin.PostJsonAsync("/api/v1/role-assignments",
            new { userId = target, roleId, scopeType = "Cluster", scopeId = clusterId.ToString("D") })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostJsonAsync("/api/v1/role-assignments",
            new { userId = target, roleId, scopeType = "Cluster", scopeId = Guid.NewGuid().ToString("D") })).StatusCode);
    }

    // ------------------------------------------------------------------ maintenance

    [Fact]
    public async Task Maintenance_is_checked_against_the_live_cluster_and_runs_as_a_job()
    {
        var (clusterId, hv01, hv02, _) = await ClusterAsync(("web01", VmPowerState.Running));
        var operatorClient = await UserAsync("mt-operator", BuiltInRoles.VmOperator);
        Assert.Equal(HttpStatusCode.Forbidden, (await operatorClient.PostJsonAsync($"/api/v1/hosts/{hv01}/maintenance", new { action = "Enter" })).StatusCode);
        Assert.Equal(1, await DeniedAsync("host.maintenance.request"));

        var admin = await UserAsync("mt-admin", BuiltInRoles.InfrastructureAdmin);
        Assert.Equal("invalid_maintenance_action", await (await admin.PostJsonAsync($"/api/v1/hosts/{hv01}/maintenance", new { action = "Evict" })).CodeAsync());

        // Storage repair running: blocked (not overridable in the portal), audited as denied.
        _app.Agent.Cluster = Report(("HV01", "Up"), ("HV02", "Up")) with
        {
            Storage = Report(("HV01", "Up")).Storage! with { Jobs = [new ClusterStorageJobStatus { Name = "Repair", State = "Running", PercentComplete = 30 }] },
        };
        var check = await (await admin.GetAsync($"/api/v1/hosts/{hv01}/maintenance?action=Enter")).JsonAsync();
        Assert.False(check.GetProperty("allowed").GetBoolean());
        Assert.Contains(check.GetProperty("blocking").EnumerateArray(), b => b.GetProperty("code").GetString() == "storage_repair_running");
        Assert.Equal("maintenance_blocked", await (await admin.PostJsonAsync($"/api/v1/hosts/{hv01}/maintenance", new { action = "Enter", acknowledgeWarnings = true })).CodeAsync());
        Assert.Equal(2, await DeniedAsync("host.maintenance.request"));

        // A VM that is not highly available: a warning to confirm.
        _app.Agent.Cluster = Report(("HV01", "Up"), ("HV02", "Up"));
        await _app.WithDbAsync(async db =>
        {
            var v = await db.VirtualMachines.FirstAsync(x => x.Name == "web01");
            v.IsClustered = false;
            await db.SaveChangesAsync();
        });
        Assert.Equal("maintenance_warnings", await (await admin.PostJsonAsync($"/api/v1/hosts/{hv01}/maintenance", new { action = "Enter" })).CodeAsync());
        var job = await JobAsync(await admin.PostJsonAsync($"/api/v1/hosts/{hv01}/maintenance", new { action = "Enter", acknowledgeWarnings = true }));
        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Equal(JobService.MaintenanceJobType, job.Type);
        var (sentTo, request) = _app.Agent.MaintenanceRequests.Single();
        Assert.Equal(hv01, sentTo);
        // The node name comes from the node's inventory, not from the request.
        Assert.Equal("HV01", request.NodeName);
        Assert.Equal("Paused", _app.Agent.Cluster!.Nodes.Single(n => n.Name == "HV01").State);

        // While HV01 is paused, HV02 cannot enter maintenance.
        Assert.Equal("maintenance_blocked", await (await admin.PostJsonAsync($"/api/v1/hosts/{hv02}/maintenance", new { action = "Enter", acknowledgeWarnings = true })).CodeAsync());

        // HV01's agent is gone (the server is being serviced): it is brought back through HV02's agent.
        _app.Agent.DisconnectHost(hv01);
        var back = await JobAsync(await admin.PostJsonAsync($"/api/v1/hosts/{hv01}/maintenance", new { action = "Exit", failback = true }));
        Assert.Equal(JobStatus.Succeeded, back.Status);
        var last = _app.Agent.MaintenanceRequests.Last();
        Assert.Equal(hv02, last.SentTo);
        Assert.True(last.Request.Failback);
        Assert.Equal("Up", _app.Agent.Cluster!.Nodes.Single(n => n.Name == "HV01").State);
        Assert.Equal(2, await _app.WithDbAsync(db => db.AuditEntries.CountAsync(a => a.Action == "host.maintenance.request" && a.Outcome == AuditOutcome.Success)));
        _ = clusterId;
    }

    // ------------------------------------------------------------------ VMs

    [Fact]
    public async Task A_highly_available_vm_is_created_on_a_cluster_shared_volume_of_the_chosen_node()
    {
        var (clusterId, hv01, hv02, _) = await ClusterAsync(("taken", VmPowerState.Off));
        var admin = await UserAsync("ha-admin", BuiltInRoles.InfrastructureAdmin);
        var defaults = await (await admin.GetAsync($"/api/v1/hosts/{hv02}/defaults")).JsonAsync();
        var cluster = defaults.GetProperty("cluster");
        Assert.True(cluster.GetProperty("highlyAvailable").GetBoolean());
        Assert.Equal(@"C:\ClusterStorage\Volume1", cluster.GetProperty("sharedVolumes")[0].GetProperty("name").GetString());
        // Names are checked on every node (the cluster role is named after the VM).
        Assert.Contains("taken", defaults.GetProperty("existingNames").EnumerateArray().Select(n => n.GetString()));

        Assert.Equal("ha_requires_shared_volume", await (await admin.PostJsonAsync($"/api/v1/hosts/{hv02}/vms",
            new { vm = new { name = "ha01", highlyAvailable = true, location = @"D:\VMs", diskSizeGb = 0 } })).CodeAsync());
        Assert.Equal("ha_requires_shared_volume", await (await admin.PostJsonAsync($"/api/v1/hosts/{hv02}/vms",
            new { vm = new { name = "ha01", highlyAvailable = true, location = @"C:\ClusterStorage\Volume9", diskSizeGb = 0 } })).CodeAsync());
        Assert.Equal("vm_name_exists", await (await admin.PostJsonAsync($"/api/v1/hosts/{hv02}/vms",
            new { vm = new { name = "taken", highlyAvailable = true, location = @"C:\ClusterStorage\Volume1", diskSizeGb = 0 } })).CodeAsync());
        _app.Agent.ProtocolVersions[hv02] = 10;
        Assert.Equal("agent_outdated", await (await admin.PostJsonAsync($"/api/v1/hosts/{hv02}/vms",
            new { vm = new { name = "ha01", highlyAvailable = true, location = @"C:\ClusterStorage\Volume1", diskSizeGb = 0 } })).CodeAsync());
        _app.Agent.ProtocolVersions.TryRemove(hv02, out _);

        var job = await JobAsync(await admin.PostJsonAsync($"/api/v1/hosts/{hv02}/vms",
            new { vm = new { name = "ha01", highlyAvailable = true, location = @"C:\ClusterStorage\Volume1\VMs", diskSizeGb = 0 } }));
        Assert.Equal(JobStatus.Succeeded, job.Status);
        var sent = _app.Agent.OperationsReceived(HvOperation.NewVm).Single();
        var spec = HvOperations.FromElement<NewVmRequest>(sent.OperationArgs)!;
        Assert.True(spec.HighlyAvailable);
        Assert.Equal(@"C:\ClusterStorage\Volume1\VMs", spec.Location);
        _ = (clusterId, hv01);
    }

    [Fact]
    public async Task A_vm_moved_by_the_cluster_keeps_its_record_groups_and_history()
    {
        var (_, hv01, hv02, vms) = await ClusterAsync(("db01", VmPowerState.Running));
        var hyperVId = await _app.WithDbAsync(db => db.VirtualMachines.Where(v => v.Id == vms["db01"]).Select(v => v.HyperVId).FirstAsync());
        var groupId = Guid.NewGuid();
        await _app.WithDbAsync(async db =>
        {
            db.LogicalGroups.Add(new LogicalGroup { Id = groupId, Name = "Databases", CreatedAt = DateTimeOffset.UtcNow });
            db.LogicalGroupMembers.Add(new LogicalGroupMember { GroupId = groupId, VmId = vms["db01"] });
            var h = await db.Hosts.FirstAsync(x => x.Id == hv01);
            h.LastInventoryAt = DateTimeOffset.UtcNow;
            var v = await db.VirtualMachines.FirstAsync(x => x.Id == vms["db01"]);
            v.LastSeenAt = h.LastInventoryAt.Value;
            await db.SaveChangesAsync();
        });

        async Task IngestAsync(Guid hostId, string node, bool withVm)
        {
            using var scope = _app.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<InventoryIngestor>().ApplyAsync(hostId, new HostInventory
            {
                CollectedAtUtc = DateTimeOffset.UtcNow,
                Access = HyperVAccessStatus.Ok,
                Host = NodeFacts(node) with { Cluster = NodeFacts(node).Cluster! with { ClusteredVmIds = [hyperVId] } },
                Vms = withVm ? [new VmSummary { VmId = hyperVId, Name = "db01", PowerState = VmPowerState.Running }] : [],
            }, CancellationToken.None);
        }

        // Live migration in progress: HV02 reports the VM while HV01 still has it. No second record.
        await IngestAsync(hv02, "HV02", withVm: true);
        Assert.Equal(1, await _app.WithDbAsync(db => db.VirtualMachines.CountAsync(v => v.HyperVId == hyperVId)));
        Assert.Equal(hv01, await _app.WithDbAsync(db => db.VirtualMachines.Where(v => v.Id == vms["db01"]).Select(v => v.HostId).FirstAsync()));

        // HV01 no longer has it, HV02 does: the record follows the VM.
        await IngestAsync(hv01, "HV01", withVm: false);
        await IngestAsync(hv02, "HV02", withVm: true);
        var vm = await _app.WithDbAsync(db => db.VirtualMachines.AsNoTracking().SingleAsync(v => v.HyperVId == hyperVId));
        Assert.Equal(vms["db01"], vm.Id);
        Assert.Equal(hv02, vm.HostId);
        Assert.False(vm.IsMissing);
        Assert.True(vm.IsClustered);
        Assert.True(await _app.WithDbAsync(db => db.LogicalGroupMembers.AnyAsync(m => m.VmId == vm.Id && m.GroupId == groupId)));
        Assert.True(await _app.WithDbAsync(db => db.InventoryEvents.AnyAsync(e => e.VmId == vm.Id && e.Kind == "vm.moved_in" && e.HostId == hv02)));
    }

    [Fact]
    public async Task Inside_a_cluster_a_highly_available_vm_is_moved_by_the_cluster()
    {
        var (_, hv01, hv02, vms) = await ClusterAsync(("app01", VmPowerState.Running), ("local01", VmPowerState.Running));
        await _app.WithDbAsync(async db =>
        {
            var local = await db.VirtualMachines.FirstAsync(x => x.Id == vms["local01"]);
            local.IsClustered = false;
            await db.SaveChangesAsync();
        });
        var admin = await UserAsync("mv-admin", BuiltInRoles.InfrastructureAdmin);
        Assert.Equal("vm_not_highly_available", await (await admin.PostJsonAsync($"/api/v1/vms/{vms["local01"]}/move",
            new { mode = "host", destinationHostId = hv02 })).CodeAsync());
        var job = await JobAsync(await admin.PostJsonAsync($"/api/v1/vms/{vms["app01"]}/move", new { mode = "host", destinationHostId = hv02 }));
        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Equal(JobService.ClusterNodeMoveOperation, job.Operation);
        Assert.Empty(_app.Agent.OperationsReceived(HvOperation.MoveVm));
        var move = HvOperations.FromElement<MoveClusterVmRequest>(_app.Agent.OperationsReceived(HvOperation.MoveClusterVm).Single().OperationArgs)!;
        Assert.Equal("HV02", move.NodeName);
        Assert.Equal(hv02, await _app.WithDbAsync(db => db.VirtualMachines.Where(v => v.Id == vms["app01"]).Select(v => v.HostId).FirstAsync()));
        _ = hv01;
    }

    [Fact]
    public async Task A_host_whose_windows_cluster_differs_is_flagged_and_never_changed()
    {
        var (_, hv01, _, _) = await ClusterAsync();
        await _app.WithDbAsync(async db =>
        {
            var h = await db.Hosts.FirstAsync(x => x.Id == hv01);
            h.HostFactsJson = JsonSerializer.Serialize(NodeFacts("HV01", cluster: "OTHER"));
            await db.SaveChangesAsync();
        });
        var (solo, _) = await _app.CreateHostAsync("loose");
        await _app.WithDbAsync(async db =>
        {
            var h = await db.Hosts.FirstAsync(x => x.Id == solo);
            h.HostFactsJson = JsonSerializer.Serialize(NodeFacts("HV03"));
            await db.SaveChangesAsync();
        });
        var admin = await UserAsync("mm-admin", BuiltInRoles.InfrastructureAdmin);
        var hosts = (await (await admin.GetAsync("/api/v1/hosts")).JsonAsync()).EnumerateArray().ToList();
        Assert.Equal("other_cluster", hosts.Single(h => h.GetProperty("id").GetGuid() == hv01).GetProperty("clusterMismatch").GetString());
        Assert.Equal("unassigned", hosts.Single(h => h.GetProperty("id").GetGuid() == solo).GetProperty("clusterMismatch").GetString());
        Assert.Null(await _app.WithDbAsync(db => db.Hosts.Where(h => h.Id == solo).Select(h => h.ClusterId).FirstAsync()));
    }
}
