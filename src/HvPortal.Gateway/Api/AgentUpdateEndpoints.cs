using HvPortal.Gateway.Agents;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Configuration;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Jobs;
using HvPortal.Gateway.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HvPortal.Gateway.Api;

/// <summary>
/// Agent versions and updates from the portal (ADR-029): which agents are older than the package of this gateway, and
/// update jobs (one per agent). An agent can be updated from the portal only when it runs 0.7.0+, its updater task is
/// registered and the package carries a signed catalog (the updater on the host checks the signature again).
/// </summary>
public static class AgentUpdateEndpoints
{
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/agents").WithTags("Agent updates");
        g.MapGet("/updates", StatusAsync);
        g.MapPost("/update-outdated", UpdateOutdatedAsync);
        app.MapPost("/api/v1/hosts/{id:guid}/agent/update", (Guid id, HttpContext http, PortalDbContext db, IAgentDispatcher agents, JobService jobs,
            IOptions<GatewayOptions> options) => UpdateOneAsync(id, false, http, db, agents, jobs, options)).WithTags("Agent updates");
        app.MapPost("/api/v1/management-agents/{id:guid}/update", (Guid id, HttpContext http, PortalDbContext db, IAgentDispatcher agents, JobService jobs,
            IOptions<GatewayOptions> options) => UpdateOneAsync(id, true, http, db, agents, jobs, options)).WithTags("Agent updates");
    }

    public sealed record AgentState(Guid Id, string Kind, string Name, string? Version, bool Connected, int Protocol, bool Outdated, bool? UpdaterInstalled,
        string? LastUpdateResult, bool CanUpdate, string? Reason, Guid? RunningJobId);

    internal static async Task<List<AgentState>> AgentsAsync(PortalDbContext db, IAgentDispatcher agents, DownloadEndpoints.PackageInfo? package, CancellationToken ct)
    {
        var version = package?.Version;
        var running = await db.Jobs.AsNoTracking().Where(j => j.Type == JobService.AgentUpdateJobType
                && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running || j.Status == JobStatus.Reconciling))
            .Select(j => new { j.TargetId, j.Id }).ToListAsync(ct);
        var list = new List<AgentState>();
        var hosts = await db.Hosts.AsNoTracking().Where(h => h.Status == HostStatus.Active && h.ConnectionMode == HostConnectionMode.Agent)
            .OrderBy(h => h.DisplayName).ToListAsync(ct);
        foreach (var h in hosts)
        {
            list.Add(State(h.Id, "host", h.DisplayName, h.AgentVersion, h.AgentUpdaterInstalled, h.AgentLastUpdateResult));
        }
        foreach (var a in await db.ManagementAgents.AsNoTracking().Where(a => a.Status == HostStatus.Active).OrderBy(a => a.DisplayName).ToListAsync(ct))
        {
            list.Add(State(a.Id, "management", a.DisplayName, a.AgentVersion, a.UpdaterInstalled, a.LastUpdateResult));
        }
        return list;

        AgentState State(Guid id, string kind, string name, string? recorded, bool? updater, string? last)
        {
            var live = agents.AgentOf(id);
            var current = live?.Version ?? recorded;
            var outdated = version is not null && current is not null && !AgentVersions.AtLeast(current, version);
            var reason = !outdated ? null
                : live is null ? "not_connected"
                : live.Value.Protocol < 9 ? "agent_too_old"
                : updater != true ? "updater_missing"
                : package is not { CatalogSigned: true } ? "package_unsigned"
                : null;
            return new AgentState(id, kind, name, current, live is not null, live?.Protocol ?? 0, outdated, updater, last, outdated && reason is null, reason,
                running.FirstOrDefault(r => r.TargetId == id)?.Id);
        }
    }

    private static async Task<IResult> StatusAsync(HttpContext http, PortalDbContext db, IAgentDispatcher agents, IOptions<GatewayOptions> options)
    {
        if (await http.GuardAsync(Permissions.HostsManage, ResourceRef.Global, "agents.updates") is { } denied)
        {
            return denied;
        }
        var package = DownloadEndpoints.Describe(options.Value);
        var list = await AgentsAsync(db, agents, package, http.RequestAborted);
        return Results.Ok(new
        {
            gatewayVersion = ProductInfo.Version,
            package = package is null ? null : new { package.Version, package.Sha256, package.SizeBytes, package.Signed, package.SelfSigned, package.CatalogSigned, package.Signer },
            outdated = list.Count(a => a.Outdated),
            agents = list,
        });
    }

    private static async Task<IResult> UpdateOneAsync(Guid id, bool management, HttpContext http, PortalDbContext db, IAgentDispatcher agents, JobService jobs,
        IOptions<GatewayOptions> options)
    {
        if ((await http.GuardAsync(Permissions.HostsManage, ResourceRef.Global, "agent.update.request") ?? await http.RequireRecentReauthAsync()) is { } denied)
        {
            return denied;
        }
        var package = DownloadEndpoints.Describe(options.Value);
        if (package?.Version is null)
        {
            return ApiSupport.Conflict("package_missing", "This gateway has no agent package to send.");
        }
        var state = (await AgentsAsync(db, agents, package, http.RequestAborted)).FirstOrDefault(a => a.Id == id && a.Kind == (management ? "management" : "host"));
        if (state is null)
        {
            return ApiSupport.NotFound();
        }
        if (!state.Outdated)
        {
            return ApiSupport.Conflict("up_to_date", $"The agent already runs {state.Version}.");
        }
        if (!state.CanUpdate)
        {
            return ApiSupport.Conflict(state.Reason ?? "cannot_update", Explain(state.Reason));
        }
        var (job, created) = await jobs.CreateAgentUpdateJobAsync(http.User.UserId(), id, state.Name, management, package.Version, http.RequestAborted);
        if (created)
        {
            await http.WriteAuditAsync("agent.update.request", resourceType: management ? "management_agent" : "host", resourceId: id.ToString("D"),
                details: new { jobId = job.Id, from = state.Version, to = package.Version });
        }
        return Results.Accepted($"/api/v1/jobs/{job.Id}", new { jobId = job.Id, created });
    }

    private static async Task<IResult> UpdateOutdatedAsync(HttpContext http, PortalDbContext db, IAgentDispatcher agents, JobService jobs, IOptions<GatewayOptions> options)
    {
        if ((await http.GuardAsync(Permissions.HostsManage, ResourceRef.Global, "agent.update.request") ?? await http.RequireRecentReauthAsync()) is { } denied)
        {
            return denied;
        }
        var package = DownloadEndpoints.Describe(options.Value);
        if (package?.Version is null)
        {
            return ApiSupport.Conflict("package_missing", "This gateway has no agent package to send.");
        }
        var states = await AgentsAsync(db, agents, package, http.RequestAborted);
        var queued = new List<object>();
        foreach (var s in states.Where(a => a.CanUpdate))
        {
            var (job, created) = await jobs.CreateAgentUpdateJobAsync(http.User.UserId(), s.Id, s.Name, s.Kind == "management", package.Version, http.RequestAborted);
            queued.Add(new { s.Id, s.Name, jobId = job.Id, created });
        }
        var skipped = states.Where(a => a.Outdated && !a.CanUpdate).Select(a => new { a.Id, a.Name, a.Reason }).ToList();
        await http.WriteAuditAsync("agent.update.request", details: new { all = true, to = package.Version, queued = queued.Count, skipped = skipped.Count });
        return Results.Ok(new { queued, skipped });
    }

    internal static string Explain(string? reason) => reason switch
    {
        "not_connected" => "The agent is not connected.",
        "agent_too_old" => "The agent is older than 0.7.0: update it once by hand (Guide > Update), later versions can be installed from the portal.",
        "updater_missing" => "The updater task is not registered on that machine: run Install-Agent.ps1 (0.7.0 or later) once, without -DisableRemoteUpdate.",
        "package_unsigned" => "The agent package of this gateway has no signed catalog (unsigned build): agents refuse it; update them by hand.",
        _ => "The agent cannot be updated from the portal now.",
    };
}
