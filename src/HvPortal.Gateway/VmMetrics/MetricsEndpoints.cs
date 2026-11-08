using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Agents;
using HvPortal.Gateway.Api;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Configuration;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Jobs;
using HvPortal.Gateway.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HvPortal.Gateway.VmMetrics;

/// <summary>
/// Resource use history of a VM (ADR-039). GET: the samples stored by the collector plus a live reading from the host.
/// POST metering: resource metering on or off, as a job (vm.metering, permission vm.config.compute on the VM).
/// </summary>
public static class MetricsEndpoints
{
    public sealed record MeteringRequest(bool Enabled, string? IdempotencyKey);

    public static void Map(WebApplication app)
    {
        var vms = app.MapGroup("/api/v1/vms").WithTags("Virtual machines");
        vms.MapGet("/{id:guid}/metrics", GetAsync);
        vms.MapPost("/{id:guid}/metering", SetAsync);
    }

    private static async Task<IResult> GetAsync(Guid id, int? hours, HttpContext http, PortalDbContext db, AccessEvaluator access, IAgentDispatcher agents,
        VmMetricsBuffer buffer, IOptions<GatewayOptions> options, TimeProvider time)
    {
        var vm = await VmOperationsEndpoints.ReadableVmAsync(id, http, db, access);
        if (vm is null)
        {
            return ApiSupport.NotFound();
        }
        var o = options.Value.Metrics;
        var now = time.GetUtcNow();
        var range = Math.Clamp(hours ?? 24, 1, 24 * Math.Clamp(o.RetentionDays, 1, 366));
        var connected = agents.IsConnected(vm.HostId);
        var protocol = agents.ProtocolVersionOf(vm.HostId);
        var outdated = connected && protocol < 10;

        // Live reading (also tells whether Hyper-V meters the VM right now).
        VmMetricsSample? live = null;
        string? liveError = null;
        if (connected && !outdated && !vm.IsMissing)
        {
            var (result, error) = await VmOperationsEndpoints.QueryAsync(agents, vm.HostId, HvOperation.GetVmMetrics, new VmMetricsRequest(vm.HyperVId), null,
                http.RequestAborted, timeoutSeconds: 60);
            if (error is null)
            {
                live = HvOperations.FromElement<VmMetricsReport>(result)?.Vms.FirstOrDefault(v => v.VmId == vm.HyperVId);
            }
            else
            {
                liveError = "The host did not answer: " + (error is IStatusCodeHttpResult s ? s.StatusCode.ToString() : "error");
            }
        }

        var metering = await db.VmMeterings.FirstOrDefaultAsync(m => m.VmId == vm.Id, http.RequestAborted);
        if (live is not null && (metering?.Enabled ?? false) != live.MeteringEnabled)
        {
            // Hyper-V is the truth: changed outside the portal (or the portal record is new).
            metering ??= db.VmMeterings.Add(new VmMetering { VmId = vm.Id, HostId = vm.HostId }).Entity;
            metering.Enabled = live.MeteringEnabled;
            if (live.MeteringEnabled)
            {
                metering.EnabledAt ??= live.MeteringSeconds is { } secs ? now - TimeSpan.FromSeconds(secs) : now;
            }
            buffer.SetMetered(vm.Id, live.MeteringEnabled);
            await db.SaveChangesAsync(http.RequestAborted);
        }

        var from = now - TimeSpan.FromHours(range);
        var samples = await db.VmMetricSamples.AsNoTracking().Where(s => s.VmId == vm.Id && s.At >= from).OrderBy(s => s.At).ToListAsync(http.RequestAborted);
        var before = await db.VmMetricSamples.AsNoTracking().Where(s => s.VmId == vm.Id && s.At < from).OrderByDescending(s => s.At).FirstOrDefaultAsync(http.RequestAborted);
        if (before is not null)
        {
            samples.Insert(0, before);
        }
        var interval = Math.Clamp(o.SampleIntervalSeconds, 60, 3600);
        var (points, summary) = MetricsSeries.Build(samples, from, interval);
        var resetBase = metering?.LastResetAt ?? metering?.EnabledAt;
        return Results.Ok(new
        {
            vmId = vm.Id,
            enabled = live?.MeteringEnabled ?? metering?.Enabled,
            known = live is not null,
            // Turned on at least once (by the portal or found on): the tab then never turns it on again by itself.
            everEnabled = metering is not null,
            hostConnected = connected,
            agentOutdated = outdated,
            vmMissing = vm.IsMissing,
            liveError,
            canManage = await access.HasAsync(http.User.UserId(), Permissions.VmConfigCompute, ResourceRef.Vm(vm.Id), http.RequestAborted),
            enabledAt = metering?.EnabledAt,
            lastResetAt = metering?.LastResetAt,
            nextResetAt = metering is { Enabled: true } && resetBase is { } rb ? rb + TimeSpan.FromDays(Math.Clamp(o.ResetDays, 1, 365)) : (DateTimeOffset?)null,
            lastSampleAt = metering?.LastSampleAt,
            lastError = metering?.LastError,
            lastErrorAt = metering?.LastErrorAt,
            intervalSeconds = interval,
            retentionDays = o.RetentionDays,
            resetDays = o.ResetDays,
            hours = range,
            live,
            summary,
            points,
        });
    }

    private static async Task<IResult> SetAsync(Guid id, MeteringRequest req, HttpContext http, PortalDbContext db, AccessEvaluator access, JobService jobs,
        IAgentDispatcher agents)
    {
        var vm = await VmOperationsEndpoints.ReadableVmAsync(id, http, db, access);
        if (vm is null)
        {
            return ApiSupport.NotFound();
        }
        var action = req.Enabled ? "vm.metering.enable.request" : "vm.metering.disable.request";
        if (await http.GuardAsync(Permissions.VmConfigCompute, ResourceRef.Vm(id), action) is { } denied)
        {
            return denied;
        }
        if (vm.IsMissing)
        {
            return ApiSupport.Conflict("vm_missing");
        }
        if (agents.IsConnected(vm.HostId) && agents.ProtocolVersionOf(vm.HostId) < 10)
        {
            return ApiSupport.Error(StatusCodes.Status501NotImplemented, "agent_outdated", "Resource use history needs agent 0.8.0 or later on this host.");
        }
        var key = VmOperationsEndpoints.IdempotencyKey(http, req.IdempotencyKey);
        if (key == "!")
        {
            return ApiSupport.BadRequest("invalid_idempotency_key");
        }
        var args = new VmMeteringChange(vm.HyperVId, req.Enabled);
        return await VmOperationsEndpoints.QueueAsync(http, jobs, new JobService.OperationJobSpec(JobService.MeteringJobType, req.Enabled ? "Enable" : "Disable",
            ScopeType.Vm, vm.Id, vm.Name, vm.HostId, Permissions.VmConfigCompute, new JobService.OperationParameters(HvOperations.ToElement(args)), 120,
            $"Resource metering {(req.Enabled ? "on" : "off")} requested on {vm.Name}"), key, action, new { enabled = req.Enabled });
    }
}
