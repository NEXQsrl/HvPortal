using System.Net;
using System.Text.Json;
using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Agents;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Jobs;
using HvPortal.Gateway.Security;
using HvPortal.Gateway.Tests.Infrastructure;
using HvPortal.Gateway.VmMetrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HvPortal.Gateway.Tests.Integration;

/// <summary>
/// v0.8.0 (agent protocol 10): resource use history of VMs (ADR-039). Hyper-V (Get-VM / Measure-VM) is simulated by the
/// test double; what the host really reports is in the real-host test plan (V69-V71).
/// </summary>
public sealed class VmMetricsTests : IAsyncLifetime
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

    private async Task<HttpClient> UserAsync(string name, string? role)
    {
        await _app.CreateUserAsync(name, role);
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

    private Task<Guid> HyperVIdAsync(Guid vmId) => _app.WithDbAsync(db => db.VirtualMachines.Where(v => v.Id == vmId).Select(v => v.HyperVId).FirstAsync());

    private async Task CollectAsync() => await _app.Services.GetRequiredService<MetricsCollector>().CollectAsync(CancellationToken.None);

    private async Task IngestAsync(Guid hostId, Guid hyperVId, string name, ushort cpu, long memMb)
    {
        using var scope = _app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<InventoryIngestor>().ApplyAsync(hostId, new HostInventory
        {
            CollectedAtUtc = DateTimeOffset.UtcNow,
            Access = HyperVAccessStatus.Ok,
            Partial = true,
            Host = new HostFacts { ComputerName = "M", AgentVersion = "0.8.0", VirtualSwitches = [] },
            Vms = [new VmSummary { VmId = hyperVId, Name = name, PowerState = VmPowerState.Running, ProcessorLoadPercent = cpu, MemoryAssignedMb = memMb, MemoryAvailablePercent = 30 }],
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Metering_is_read_with_inventory_read_and_changed_with_vm_config_compute_and_agent_0_8()
    {
        var (hostId, vms) = await _app.CreateHostAsync("met-host", ("app01", VmPowerState.Running));
        var (oldHost, oldVms) = await _app.CreateHostAsync("met-old", ("legacy", VmPowerState.Running));
        _app.Agent.ProtocolVersions[oldHost] = 9;
        var vm = vms["app01"];
        var nobody = await UserAsync("met-nobody", null);
        var reader = await UserAsync("met-reader", BuiltInRoles.ReadOnly);
        var admin = await UserAsync("met-admin", BuiltInRoles.InfrastructureAdmin);

        // No role: the VM does not exist for this user. Reader: sees the state, cannot change it.
        Assert.Equal(HttpStatusCode.NotFound, (await nobody.GetAsync($"/api/v1/vms/{vm}/metrics")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await nobody.PostJsonAsync($"/api/v1/vms/{vm}/metering", new { enabled = true })).StatusCode);
        var off = await (await reader.GetAsync($"/api/v1/vms/{vm}/metrics")).JsonAsync();
        Assert.False(off.GetProperty("enabled").GetBoolean());
        Assert.True(off.GetProperty("known").GetBoolean());
        Assert.False(off.GetProperty("canManage").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostJsonAsync($"/api/v1/vms/{vm}/metering", new { enabled = true })).StatusCode);
        Assert.True(await _app.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "vm.metering.enable.request" && a.Outcome == AuditOutcome.Denied)));
        Assert.Empty(_app.Agent.OperationsReceived(HvOperation.SetVmResourceMetering));

        // Agent 0.7 (protocol 9): refused at request time, and the tab says why.
        Assert.Equal("agent_outdated", await (await admin.PostJsonAsync($"/api/v1/vms/{oldVms["legacy"]}/metering", new { enabled = true })).CodeAsync());
        Assert.True((await (await admin.GetAsync($"/api/v1/vms/{oldVms["legacy"]}/metrics")).JsonAsync()).GetProperty("agentOutdated").GetBoolean());

        var on = await JobAsync(await admin.PostJsonAsync($"/api/v1/vms/{vm}/metering", new { enabled = true }));
        Assert.Equal(JobStatus.Succeeded, on.Status);
        Assert.Equal(JobService.MeteringJobType, on.Type);
        var hv = await HyperVIdAsync(vm);
        Assert.Equal(hv, HvOperations.FromElement<VmMeteringChange>(_app.Agent.OperationsReceived(HvOperation.SetVmResourceMetering).Single().OperationArgs)!.VmId);
        Assert.True(_app.Agent.Meters.ContainsKey(hv));
        Assert.True(await _app.WithDbAsync(db => db.VmMeterings.AnyAsync(m => m.VmId == vm && m.Enabled && m.EnabledAt != null)));
        var after = await (await admin.GetAsync($"/api/v1/vms/{vm}/metrics")).JsonAsync();
        Assert.True(after.GetProperty("enabled").GetBoolean());
        Assert.True(after.GetProperty("canManage").GetBoolean());
        Assert.Equal(JsonValueKind.String, after.GetProperty("nextResetAt").ValueKind);

        var offJob = await JobAsync(await admin.PostJsonAsync($"/api/v1/vms/{vm}/metering", new { enabled = false }));
        Assert.Equal(JobStatus.Succeeded, offJob.Status);
        Assert.False(_app.Agent.Meters.ContainsKey(hv));
        Assert.False(await _app.WithDbAsync(db => db.VmMeterings.Where(m => m.VmId == vm).Select(m => m.Enabled).FirstAsync()));
    }

    [Fact]
    public async Task Samples_combine_inventory_readings_with_hyper_v_totals_and_follow_resets()
    {
        var (hostId, vms) = await _app.CreateHostAsync("met-col", ("db01", VmPowerState.Running), ("web01", VmPowerState.Running));
        var vm = vms["db01"];
        var hv = await HyperVIdAsync(vm);
        var admin = await UserAsync("met-col-admin", BuiltInRoles.InfrastructureAdmin);
        Assert.Equal(JobStatus.Succeeded, (await JobAsync(await admin.PostJsonAsync($"/api/v1/vms/{vm}/metering", new { enabled = true }))).Status);

        // Two inventory reports in the period: average 40 %, peak 60 %; the VM without metering is not collected.
        await IngestAsync(hostId, hv, "db01", 20, 4096);
        await IngestAsync(hostId, hv, "db01", 60, 6144);
        await IngestAsync(hostId, await HyperVIdAsync(vms["web01"]), "web01", 99, 1024);
        await CollectAsync();
        var first = await _app.WithDbAsync(db => db.VmMetricSamples.AsNoTracking().SingleAsync());
        Assert.Equal(vm, first.VmId);
        Assert.Equal(40, first.CpuAvgPercent);
        Assert.Equal(60, first.CpuMaxPercent);
        Assert.Equal(5120, first.MemoryAssignedAvgMb);
        Assert.Equal(6144, first.MemoryAssignedMaxMb);
        Assert.Equal(30, first.MemoryAvailableMinPercent);

        // Hyper-V measured 300 s more: 300 MB read, 30 000 normalized I/O, 75 MB received -> 1 MB/s, 100 IOPS, 2 Mbit/s.
        _app.Agent.Meters[hv] = _app.Agent.Meters[hv] with { MeteringSeconds = 300, DiskReadMb = 300, DiskWrittenMb = 150, NormalizedIoCount = 30_000, NetworkInboundMb = 75, NetworkOutboundMb = 0 };
        await CollectAsync();
        var json = await (await admin.GetAsync($"/api/v1/vms/{vm}/metrics?hours=24")).JsonAsync();
        var points = json.GetProperty("points").EnumerateArray().ToList();
        Assert.Equal(2, points.Count);
        Assert.Equal(1, points[1].GetProperty("readMBps").GetDouble());
        Assert.Equal(0.5, points[1].GetProperty("writeMBps").GetDouble());
        Assert.Equal(100, points[1].GetProperty("iops").GetDouble());
        Assert.Equal(2, points[1].GetProperty("netInMbps").GetDouble());
        var summary = json.GetProperty("summary");
        Assert.Equal(300, summary.GetProperty("readMb").GetDouble());
        Assert.Equal(60, summary.GetProperty("cpuMax").GetDouble());
        Assert.Equal(6144, summary.GetProperty("memAssignedMax").GetInt64());

        // Counters reset (by Hyper-V or by hand): the period starts from zero instead of giving negative values.
        _app.Agent.Meters[hv] = FakeAgentDispatcher.NewMeter(hv) with { MeteringSeconds = 60, DiskReadMb = 60 };
        await CollectAsync();
        var third = (await (await admin.GetAsync($"/api/v1/vms/{vm}/metrics")).JsonAsync()).GetProperty("points").EnumerateArray().Last();
        Assert.Equal(1, third.GetProperty("readMBps").GetDouble());

        // Every 10 days the portal resets the Hyper-V totals (audited as a system action).
        await _app.WithDbAsync(db => db.VmMeterings.Where(m => m.VmId == vm).ExecuteUpdateAsync(u => u.SetProperty(m => m.EnabledAt, DateTimeOffset.UtcNow.AddDays(-11))));
        await CollectAsync();
        Assert.Contains(hv, _app.Agent.MeteringResets);
        Assert.NotNull(await _app.WithDbAsync(db => db.VmMeterings.Where(m => m.VmId == vm).Select(m => m.LastResetAt).FirstAsync()));
        Assert.True(await _app.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "vm.metering.reset" && a.Outcome == AuditOutcome.Success)));
        await CollectAsync();
        Assert.Single(_app.Agent.MeteringResets);

        // Samples older than 10 days are deleted.
        await _app.WithDbAsync(async db =>
        {
            db.VmMetricSamples.Add(new VmMetricSample { VmId = vm, At = DateTimeOffset.UtcNow.AddDays(-11) });
            await db.SaveChangesAsync();
        });
        await CollectAsync();
        var tenDaysAgo = DateTimeOffset.UtcNow.AddDays(-10);
        Assert.False(await _app.WithDbAsync(db => db.VmMetricSamples.AnyAsync(s => s.At < tenDaysAgo)));

        // Turned off outside the portal: the portal follows Hyper-V and stops collecting.
        _app.Agent.Meters.TryRemove(hv, out _);
        var before = await _app.WithDbAsync(db => db.VmMetricSamples.CountAsync());
        await CollectAsync();
        Assert.False(await _app.WithDbAsync(db => db.VmMeterings.Where(m => m.VmId == vm).Select(m => m.Enabled).FirstAsync()));
        await CollectAsync();
        Assert.Equal(before, await _app.WithDbAsync(db => db.VmMetricSamples.CountAsync()));
    }

    [Fact]
    public async Task Metering_turned_on_outside_the_portal_is_adopted_and_a_deleted_vm_loses_its_history()
    {
        var (hostId, vms) = await _app.CreateHostAsync("met-adopt", ("a1", VmPowerState.Running), ("a2", VmPowerState.Running));
        var admin = await UserAsync("met-adopt-admin", BuiltInRoles.InfrastructureAdmin);
        Assert.Equal(JobStatus.Succeeded, (await JobAsync(await admin.PostJsonAsync($"/api/v1/vms/{vms["a1"]}/metering", new { enabled = true }))).Status);
        // a2 metered with Hyper-V Manager: found at the next pass on that host.
        var hv2 = await HyperVIdAsync(vms["a2"]);
        _app.Agent.Meters[hv2] = FakeAgentDispatcher.NewMeter(hv2);
        await CollectAsync();
        Assert.True(await _app.WithDbAsync(db => db.VmMeterings.AnyAsync(m => m.VmId == vms["a2"] && m.Enabled)));
        Assert.True(await _app.WithDbAsync(db => db.VmMetricSamples.AnyAsync(s => s.VmId == vms["a2"])));

        await _app.WithDbAsync(db => InventoryCleanup.RemoveVmAsync(db, vms["a2"], hostId, "vm.deleted", DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.False(await _app.WithDbAsync(db => db.VmMetricSamples.AnyAsync(s => s.VmId == vms["a2"])));
        Assert.False(await _app.WithDbAsync(db => db.VmMeterings.AnyAsync(m => m.VmId == vms["a2"])));
    }

    [Fact]
    public void Series_leave_gaps_for_missing_periods_and_keep_at_most_600_points()
    {
        var vm = Guid.NewGuid();
        var t0 = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var samples = new List<VmMetricSample>();
        for (var i = 0; i < 2000; i++)
        {
            // Host offline between sample 100 and 101 (one hour).
            var at = t0.AddSeconds(i * 300 + (i > 100 ? 3600 : 0));
            samples.Add(new VmMetricSample { VmId = vm, At = at, CpuAvgPercent = i % 2 == 0 ? 10 : 90, CpuMaxPercent = 95, MeteringSeconds = i * 300.0, DiskReadMb = i * 30L });
        }
        var (points, summary) = MetricsSeries.Build(samples, t0, 300);
        Assert.True(points.Count <= MetricsSeries.MaxPoints);
        Assert.Contains(points, p => p.GapBefore);
        Assert.Equal(95, summary.CpuMax);
        Assert.Equal(50, summary.CpuAvg);
        Assert.Equal(90, summary.CpuP95);
        // The difference over the gap is not counted: 1999 periods minus the one across the gap, 30 MB each.
        Assert.Equal(1998 * 30, summary.ReadMb);
    }
}
