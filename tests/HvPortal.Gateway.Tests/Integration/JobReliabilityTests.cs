using System.Net;
using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Security;
using HvPortal.Gateway.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HvPortal.Gateway.Tests.Integration;

/// <summary>Service restart during a job, channel loss, host unreachable, cancellation.</summary>
public sealed class JobReliabilityTests : IAsyncLifetime
{
    private GatewayFactory _app = null!;
    private Guid _user;

    public async Task InitializeAsync()
    {
        _app = new GatewayFactory();
        _ = _app.Server;
        await _app.SetMfaPolicyAsync(MfaRequirement.None);
        _user = await _app.CreateUserAsync("ops", BuiltInRoles.InfrastructureAdmin);
    }

    public Task DisposeAsync()
    {
        _app.Dispose();
        return Task.CompletedTask;
    }

    private async Task<Guid> InsertJobAsync(Guid vmId, Guid hostId, string op, JobStatus status, DateTimeOffset? dispatchedAt, string leaseOwner = "previous-process")
    {
        return await _app.WithDbAsync(async db =>
        {
            var job = new Job
            {
                Type = "vm.power",
                Operation = op,
                TargetType = ScopeType.Vm,
                TargetId = vmId,
                HostId = hostId,
                RequestedByUserId = _user,
                RequiredPermission = Permissions.VmPower,
                Status = status,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                DispatchedAt = dispatchedAt,
                LeaseOwner = leaseOwner,
                TimeoutSeconds = 180,
                Attempt = 1,
            };
            db.Jobs.Add(job);
            await db.SaveChangesAsync();
            return job.Id;
        });
    }

    private Task<Job> JobAsync(Guid id) => _app.WithDbAsync(db => db.Jobs.AsNoTracking().FirstAsync(j => j.Id == id));
    private Task<Guid> HyperVIdAsync(Guid vmId) => _app.WithDbAsync(db => db.VirtualMachines.Where(v => v.Id == vmId).Select(v => v.HyperVId).FirstAsync());

    [Fact]
    public async Task Restart_before_dispatch_requeues_and_executes_once()
    {
        var (hostId, vms) = await _app.CreateHostAsync("r1", ("vm", VmPowerState.Off));
        var id = await InsertJobAsync(vms["vm"], hostId, "Start", JobStatus.Running, dispatchedAt: null);
        await _app.Runner.RecoverAfterRestartAsync(CancellationToken.None);
        Assert.Equal(JobStatus.Queued, (await JobAsync(id)).Status);
        await _app.RunJobsAsync();
        Assert.Equal(JobStatus.Succeeded, (await JobAsync(id)).Status);
        Assert.Equal(1, _app.Agent.PowerCommandsFor(await HyperVIdAsync(vms["vm"])));
    }

    [Fact]
    public async Task Restart_after_dispatch_is_reconciled_from_host_state_without_resending()
    {
        var (hostId, vms) = await _app.CreateHostAsync("r2", ("vm", VmPowerState.Off));
        var hv = await HyperVIdAsync(vms["vm"]);
        _app.Agent.SetState(hv, VmPowerState.Running); // the previous process managed to start it before dying
        var id = await InsertJobAsync(vms["vm"], hostId, "Start", JobStatus.Running, DateTimeOffset.UtcNow.AddSeconds(-30));

        await _app.Runner.RecoverAfterRestartAsync(CancellationToken.None);
        Assert.Equal(JobStatus.Reconciling, (await JobAsync(id)).Status);
        await _app.Runner.ReconcileAsync(CancellationToken.None);

        var job = await JobAsync(id);
        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Contains("reconciliation", job.ResultMessage);
        Assert.Equal(0, _app.Agent.PowerCommandsFor(hv)); // never re-sent
    }

    [Fact]
    public async Task Restart_after_dispatch_with_unchanged_state_is_reported_unknown_not_success()
    {
        var (hostId, vms) = await _app.CreateHostAsync("r3", ("vm", VmPowerState.Off));
        var id = await InsertJobAsync(vms["vm"], hostId, "Start", JobStatus.Running, DateTimeOffset.UtcNow.AddSeconds(-30));
        await _app.Runner.RecoverAfterRestartAsync(CancellationToken.None);
        await _app.Runner.ReconcileAsync(CancellationToken.None);
        var job = await JobAsync(id);
        Assert.Equal(JobStatus.Unknown, job.Status);
        Assert.Equal("outcome_unknown", job.ErrorCode);
    }

    [Fact]
    public async Task Channel_loss_after_dispatch_goes_to_reconciliation_and_is_not_resent()
    {
        var (_, vms) = await _app.CreateHostAsync("r4", ("vm", VmPowerState.Off));
        var hv = await HyperVIdAsync(vms["vm"]);
        _app.Agent.DropChannelAfterPowerCommand = true;
        _app.Agent.ExecuteBeforeDrop = true;
        var c = _app.CreateBrowser();
        await c.LoginAsync("ops");
        var r = await c.PostJsonAsync($"/api/v1/vms/{vms["vm"]}/power", new { operation = "Start" });
        var id = (await r.JsonAsync()).GetProperty("jobId").GetGuid();

        await _app.RunJobsAsync();
        Assert.Equal(JobStatus.Reconciling, (await JobAsync(id)).Status);

        _app.Agent.DropChannelAfterPowerCommand = false;
        await _app.Runner.ReconcileAsync(CancellationToken.None);
        Assert.Equal(JobStatus.Succeeded, (await JobAsync(id)).Status);
        Assert.Equal(1, _app.Agent.PowerCommandsFor(hv));
    }

    [Fact]
    public async Task Unreachable_host_is_retried_then_failed_without_sending()
    {
        var (hostId, vms) = await _app.CreateHostAsync("r5", ("vm", VmPowerState.Off));
        _app.Agent.DisconnectHost(hostId);
        var c = _app.CreateBrowser();
        await c.LoginAsync("ops");
        var id = (await (await c.PostJsonAsync($"/api/v1/vms/{vms["vm"]}/power", new { operation = "Start" })).JsonAsync()).GetProperty("jobId").GetGuid();

        for (var i = 0; i < 3; i++)
        {
            await _app.WithDbAsync(db => db.Jobs.Where(j => j.Id == id).ExecuteUpdateAsync(u => u.SetProperty(j => j.NotBefore, (DateTimeOffset?)null)));
            await _app.RunJobsAsync();
        }
        var job = await JobAsync(id);
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal("host_unreachable", job.ErrorCode);
        Assert.Contains("not sent", job.ResultMessage);
    }

    [Fact]
    public async Task Queued_job_can_be_cancelled_and_never_runs()
    {
        var (_, vms) = await _app.CreateHostAsync("r6", ("vm", VmPowerState.Off));
        var c = _app.CreateBrowser();
        await c.LoginAsync("ops");
        var id = (await (await c.PostJsonAsync($"/api/v1/vms/{vms["vm"]}/power", new { operation = "Start" })).JsonAsync()).GetProperty("jobId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await c.PostJsonAsync($"/api/v1/jobs/{id}/cancel")).StatusCode);
        await _app.RunJobsAsync();
        Assert.Equal(JobStatus.Cancelled, (await JobAsync(id)).Status);
        Assert.Equal(0, _app.Agent.PowerCommandsFor(await HyperVIdAsync(vms["vm"])));
    }

    [Fact]
    public async Task Dispatched_job_cannot_be_cancelled()
    {
        var (hostId, vms) = await _app.CreateHostAsync("r7", ("vm", VmPowerState.Off));
        var id = await InsertJobAsync(vms["vm"], hostId, "Start", JobStatus.Reconciling, DateTimeOffset.UtcNow);
        var c = _app.CreateBrowser();
        await c.LoginAsync("ops");
        var r = await c.PostJsonAsync($"/api/v1/jobs/{id}/cancel");
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("not_interruptible", await r.CodeAsync());
    }

    [Fact]
    public async Task Operations_on_the_same_vm_are_serialized_by_a_resource_lock()
    {
        var (hostId, vms) = await _app.CreateHostAsync("r8", ("vm", VmPowerState.Off));
        var held = await InsertJobAsync(vms["vm"], hostId, "Start", JobStatus.Reconciling, DateTimeOffset.UtcNow);
        await _app.WithDbAsync(async db =>
        {
            db.ResourceLocks.Add(new ResourceLock { ResourceKey = "vm:" + vms["vm"].ToString("N"), JobId = held, AcquiredAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10) });
            await db.SaveChangesAsync();
        });
        var c = _app.CreateBrowser();
        await c.LoginAsync("ops");
        var id = (await (await c.PostJsonAsync($"/api/v1/vms/{vms["vm"]}/power", new { operation = "Start" })).JsonAsync()).GetProperty("jobId").GetGuid();
        await _app.RunJobsAsync();
        var job = await JobAsync(id);
        Assert.Equal(JobStatus.Queued, job.Status);
        Assert.Contains("Waiting", job.ProgressMessage);
    }
}
