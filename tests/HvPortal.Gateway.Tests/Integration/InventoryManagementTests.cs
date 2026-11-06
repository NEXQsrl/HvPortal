using System.Net;
using System.Net.Http.Json;
using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Jobs;
using HvPortal.Gateway.Security;
using HvPortal.Gateway.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HvPortal.Gateway.Tests.Integration;

public sealed class InventoryManagementTests : IAsyncLifetime
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

    [Fact]
    public async Task Host_description_and_rename_require_hosts_manage_and_are_sanitized()
    {
        var (hostId, _) = await _app.CreateHostAsync("desc-host", ("vm", VmPowerState.Off));
        await _app.CreateUserAsync("ro-desc", BuiltInRoles.ReadOnly);
        await _app.CreateUserAsync("infra-desc", BuiltInRoles.InfrastructureAdmin);
        var ro = _app.CreateBrowser();
        await ro.LoginAsync("ro-desc");
        Assert.Equal(HttpStatusCode.Forbidden, (await PatchAsync(ro, $"/api/v1/hosts/{hostId}", new { description = "x" })).StatusCode);

        var c = _app.CreateBrowser();
        await c.LoginAsync("infra-desc");
        Assert.Equal(HttpStatusCode.NoContent, (await PatchAsync(c, $"/api/v1/hosts/{hostId}", new { description = "  Rack 3\u0007 — production  ", displayName = "hv-rack3" })).StatusCode);
        var host = await _app.WithDbAsync(db => db.Hosts.AsNoTracking().FirstAsync(h => h.Id == hostId));
        Assert.Equal("Rack 3 — production", host.Description);
        Assert.Equal("hv-rack3", host.DisplayName);
        Assert.Equal("description_too_long", await (await PatchAsync(c, $"/api/v1/hosts/{hostId}", new { description = new string('a', 1001) })).CodeAsync());
        Assert.Equal("invalid_display_name", await (await PatchAsync(c, $"/api/v1/hosts/{hostId}", new { displayName = "<script>" })).CodeAsync());
    }

    [Fact]
    public async Task Vm_notes_are_written_to_hyper_v_and_require_annotate_permission_on_that_vm()
    {
        var (_, vms) = await _app.CreateHostAsync("ann-host", ("a", VmPowerState.Off), ("b", VmPowerState.Off));
        await _app.CreateUserAsync("op-ann", BuiltInRoles.VmOperator, ScopeType.Vm, vms["a"].ToString("D"));
        await _app.CreateUserAsync("ro-ann", BuiltInRoles.ReadOnly);
        var op = _app.CreateBrowser();
        await op.LoginAsync("op-ann");
        // The VM has no portal description any more: the notes are a configuration change of the VM (job on the host).
        var accepted = await PatchAsync(op, $"/api/v1/vms/{vms["a"]}/config", new { notes = "web front-end" });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PatchAsync(op, $"/api/v1/vms/{vms["b"]}/config", new { notes = "x" })).StatusCode);
        // A VM operator annotates but does not reconfigure: notes together with a CPU change are refused.
        Assert.Equal(HttpStatusCode.Forbidden, (await PatchAsync(op, $"/api/v1/vms/{vms["a"]}/config", new { notes = "y", processorCount = 8 })).StatusCode);
        var ro = _app.CreateBrowser();
        await ro.LoginAsync("ro-ann");
        Assert.Equal(HttpStatusCode.Forbidden, (await PatchAsync(ro, $"/api/v1/vms/{vms["a"]}/config", new { notes = "x" })).StatusCode);
        var jobId = (await accepted.JsonAsync()).GetProperty("jobId").GetGuid();
        var job = await _app.WithDbAsync(db => db.Jobs.AsNoTracking().FirstAsync(j => j.Id == jobId));
        Assert.Equal(JobService.ConfigJobType, job.Type);
        Assert.Contains("web front-end", job.ParametersJson);
    }

    [Fact]
    public async Task Host_delete_removes_inventory_and_scoped_grants_but_requires_confirmation_and_idle_host()
    {
        var (hostId, vms) = await _app.CreateHostAsync("del-host", ("v1", VmPowerState.Off));
        var opId = await _app.CreateUserAsync("op-del", BuiltInRoles.VmOperator, ScopeType.Host, hostId.ToString("D"));
        await _app.AssignAsync(opId, BuiltInRoles.ReadOnly, ScopeType.Vm, vms["v1"].ToString("D"));
        await _app.CreateUserAsync("infra-del", BuiltInRoles.InfrastructureAdmin);
        var c = _app.CreateBrowser();
        await c.LoginAsync("infra-del");

        Assert.Equal("confirmation_required", await (await c.PostJsonAsync($"/api/v1/hosts/{hostId}/delete", new { confirmName = "wrong" })).CodeAsync());

        var job = await (await c.PostJsonAsync($"/api/v1/vms/{vms["v1"]}/power", new { operation = "Start" })).JsonAsync();
        Assert.Equal("host_busy", await (await c.PostJsonAsync($"/api/v1/hosts/{hostId}/delete", new { confirmName = "del-host" })).CodeAsync());
        await c.PostJsonAsync($"/api/v1/jobs/{job.GetProperty("jobId").GetGuid()}/cancel");

        Assert.Equal(HttpStatusCode.NoContent, (await c.PostJsonAsync($"/api/v1/hosts/{hostId}/delete", new { confirmName = "del-host" })).StatusCode);
        Assert.False(await _app.WithDbAsync(db => db.Hosts.AnyAsync(h => h.Id == hostId)));
        Assert.False(await _app.WithDbAsync(db => db.VirtualMachines.AnyAsync(v => v.HostId == hostId)));
        Assert.False(await _app.WithDbAsync(db => db.RoleAssignments.AnyAsync(a => a.PrincipalId == opId.ToString("D"))));
        Assert.False(_app.Agent.IsConnected(hostId));
        Assert.True(await _app.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "hosts.delete")));
    }

    [Fact]
    public async Task Vm_delete_is_a_confirmed_job_that_requires_off_state_and_permission()
    {
        var (_, vms) = await _app.CreateHostAsync("vmdel-host", ("running", VmPowerState.Running), ("stopped", VmPowerState.Off));
        await _app.CreateUserAsync("op-vmdel", BuiltInRoles.VmOperator);
        await _app.CreateUserAsync("infra-vmdel", BuiltInRoles.InfrastructureAdmin);
        var op = _app.CreateBrowser();
        await op.LoginAsync("op-vmdel");
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostJsonAsync($"/api/v1/vms/{vms["stopped"]}/delete", new { confirmName = "stopped" })).StatusCode);

        var c = _app.CreateBrowser();
        await c.LoginAsync("infra-vmdel");
        Assert.Equal("invalid_state", await (await c.PostJsonAsync($"/api/v1/vms/{vms["running"]}/delete", new { confirmName = "running" })).CodeAsync());
        Assert.Equal("confirmation_required", await (await c.PostJsonAsync($"/api/v1/vms/{vms["stopped"]}/delete", new { confirmName = "STOPPED" })).CodeAsync());
        var hv = await _app.WithDbAsync(db => db.VirtualMachines.Where(v => v.Id == vms["stopped"]).Select(v => v.HyperVId).FirstAsync());
        var r = await c.PostJsonAsync($"/api/v1/vms/{vms["stopped"]}/delete", new { confirmName = "stopped" });
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        await _app.RunJobsAsync();
        var jobId = (await r.JsonAsync()).GetProperty("jobId").GetGuid();
        Assert.Equal(JobStatus.Succeeded, await _app.WithDbAsync(db => db.Jobs.Where(j => j.Id == jobId).Select(j => j.Status).FirstAsync()));
        Assert.False(_app.Agent.Exists(hv));
        Assert.False(await _app.WithDbAsync(db => db.VirtualMachines.AnyAsync(v => v.Id == vms["stopped"])));
        Assert.True(await _app.WithDbAsync(db => db.InventoryEvents.AnyAsync(e => e.Kind == "vm.deleted" && e.VmId == vms["stopped"])));
    }

    [Fact]
    public async Task Missing_vm_can_be_forgotten_but_present_vm_cannot()
    {
        var (_, vms) = await _app.CreateHostAsync("forget-host", ("gone", VmPowerState.Off), ("here", VmPowerState.Off));
        await _app.WithDbAsync(db => db.VirtualMachines.Where(v => v.Id == vms["gone"]).ExecuteUpdateAsync(u => u.SetProperty(v => v.IsMissing, true)));
        await _app.CreateUserAsync("infra-forget", BuiltInRoles.InfrastructureAdmin);
        var c = _app.CreateBrowser();
        await c.LoginAsync("infra-forget");
        Assert.Equal("vm_present", await (await c.PostJsonAsync($"/api/v1/vms/{vms["here"]}/forget")).CodeAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostJsonAsync($"/api/v1/vms/{vms["gone"]}/forget")).StatusCode);
        Assert.False(await _app.WithDbAsync(db => db.VirtualMachines.AnyAsync(v => v.Id == vms["gone"])));
    }

    [Fact]
    public async Task Vm_details_are_fetched_live_and_served_stale_when_host_is_offline()
    {
        var (hostId, vms) = await _app.CreateHostAsync("details-host", ("vm", VmPowerState.Running));
        await _app.CreateUserAsync("ro-details", BuiltInRoles.ReadOnly);
        var c = _app.CreateBrowser();
        await c.LoginAsync("ro-details");
        var live = await (await c.GetAsync($"/api/v1/vms/{vms["vm"]}/details")).JsonAsync();
        Assert.False(live.GetProperty("stale").GetBoolean());
        Assert.Equal("10.0.0.10", live.GetProperty("details").GetProperty("networkAdapters")[0].GetProperty("ipAddresses")[0].GetString());

        _app.Agent.DisconnectHost(hostId);
        var stale = await (await c.GetAsync($"/api/v1/vms/{vms["vm"]}/details")).JsonAsync();
        Assert.True(stale.GetProperty("stale").GetBoolean());
        Assert.Equal("VHDX", stale.GetProperty("details").GetProperty("disks")[0].GetProperty("format").GetString());
    }
}
