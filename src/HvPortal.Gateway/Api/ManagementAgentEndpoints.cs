using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Agents;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Configuration;
using HvPortal.Gateway.Crypto;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Pki;
using HvPortal.Gateway.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HvPortal.Gateway.Api;

/// <summary>
/// Management agents and the remote hosts they reach with stored credentials (ADR-022). Everything here requires
/// hosts.manage at global scope; changes also require a recent re-authentication. Passwords are write-only: stored
/// encrypted (key ring, bound to the host id), never returned, never written to the audit log.
/// </summary>
public static class ManagementAgentEndpoints
{
    public sealed record CreateManagementAgentRequest(string DisplayName, string? Description);

    public sealed record RemoteHostRequest(string? DisplayName, Guid ManagementAgentId, string? ComputerName, string? UserName, string? Password, bool UseHttps);

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/management-agents").WithTags("Management agents");
        g.MapGet("/", ListAsync);
        g.MapPost("/", CreateAsync);
        g.MapPost("/{id:guid}/enrollment-token", NewTokenAsync);
        g.MapPost("/{id:guid}/revoke", RevokeAsync);
        g.MapPost("/{id:guid}/delete", DeleteAsync);

        var hosts = app.MapGroup("/api/v1/hosts").WithTags("Hosts");
        hosts.MapPost("/remote", CreateRemoteHostAsync);
        hosts.MapPut("/{id:guid}/remote", UpdateRemoteHostAsync);
    }

    private static async Task<IResult> ListAsync(HttpContext http, PortalDbContext db, IAgentDispatcher agents)
    {
        if (await http.GuardAsync(Permissions.HostsManage, ResourceRef.Global, "management_agents.list") is { } denied)
        {
            return denied;
        }
        var counts = await db.Hosts.AsNoTracking().Where(h => h.ManagementAgentId != null && h.Status != HostStatus.Revoked)
            .GroupBy(h => h.ManagementAgentId!.Value).Select(x => new { x.Key, n = x.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.n, http.RequestAborted);
        var list = await db.ManagementAgents.AsNoTracking().OrderBy(a => a.DisplayName).ToListAsync(http.RequestAborted);
        return Results.Ok(list.Select(a => new
        {
            a.Id,
            a.DisplayName,
            a.Description,
            a.MachineName,
            a.AgentVersion,
            status = a.Status.ToString(),
            connected = agents.IsManagementAgentConnected(a.Id),
            a.CreatedAt,
            a.EnrolledAt,
            a.LastSeenAt,
            hostCount = counts.GetValueOrDefault(a.Id),
        }));
    }

    private static async Task<IResult> CreateAsync(CreateManagementAgentRequest req, HttpContext http, PortalDbContext db, EnrollmentService enrollment,
        InternalCertificateAuthority ca, IOptions<GatewayOptions> options, TimeProvider time)
    {
        if ((await http.GuardAsync(Permissions.HostsManage, ResourceRef.Global, "management_agents.create") ?? await http.RequireRecentReauthAsync()) is { } denied)
        {
            return denied;
        }
        if (req.DisplayName is null || !ApiSupport.DisplayNameRegex().IsMatch(req.DisplayName))
        {
            return ApiSupport.BadRequest("invalid_display_name");
        }
        var (description, ok) = InventoryManagementEndpoints.CleanDescription(req.Description);
        if (!ok)
        {
            return ApiSupport.BadRequest("invalid_description");
        }
        if (await db.ManagementAgents.AnyAsync(a => a.DisplayName == req.DisplayName, http.RequestAborted))
        {
            return ApiSupport.Conflict("management_agent_exists");
        }
        var agent = new ManagementAgent { DisplayName = req.DisplayName, Description = description, CreatedAt = time.GetUtcNow() };
        db.ManagementAgents.Add(agent);
        await db.SaveChangesAsync(http.RequestAborted);
        var (token, expires) = await enrollment.CreateTokenAsync(agent.Id, http.User.UserId(), http.RequestAborted);
        await http.WriteAuditAsync("management_agents.create", resourceType: "management_agent", resourceId: agent.Id.ToString("D"),
            details: new { agent.DisplayName, tokenExpires = expires });
        return Results.Ok(new { managementAgentId = agent.Id, enrollment = InventoryEndpoints.EnrollmentInfo(http, ca, options.Value, token, expires, management: true) });
    }

    private static async Task<IResult> NewTokenAsync(Guid id, HttpContext http, PortalDbContext db, EnrollmentService enrollment,
        InternalCertificateAuthority ca, IOptions<GatewayOptions> options)
    {
        if ((await http.GuardAsync(Permissions.HostsManage, ResourceRef.Global, "management_agents.enrollment_token") ?? await http.RequireRecentReauthAsync()) is { } denied)
        {
            return denied;
        }
        if (!await db.ManagementAgents.AnyAsync(a => a.Id == id, http.RequestAborted))
        {
            return ApiSupport.NotFound();
        }
        var (token, expires) = await enrollment.CreateTokenAsync(id, http.User.UserId(), http.RequestAborted);
        await http.WriteAuditAsync("management_agents.enrollment_token", resourceType: "management_agent", resourceId: id.ToString("D"), details: new { expires });
        return Results.Ok(InventoryEndpoints.EnrollmentInfo(http, ca, options.Value, token, expires, management: true));
    }

    /// <summary>Revokes the agent's certificates and disconnects it: its remote hosts go offline (credentials stay stored).</summary>
    private static async Task<IResult> RevokeAsync(Guid id, HttpContext http, PortalDbContext db, IAgentDispatcher agents, TimeProvider time)
    {
        if ((await http.GuardAsync(Permissions.HostsManage, ResourceRef.Global, "management_agents.revoke") ?? await http.RequireRecentReauthAsync()) is { } denied)
        {
            return denied;
        }
        var agent = await db.ManagementAgents.FirstOrDefaultAsync(a => a.Id == id, http.RequestAborted);
        if (agent is null)
        {
            return ApiSupport.NotFound();
        }
        var now = time.GetUtcNow();
        agent.Status = HostStatus.Revoked;
        await db.SaveChangesAsync(http.RequestAborted);
        var certs = await db.AgentCertificates.Where(c => c.HostId == id && c.RevokedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.RevokedAt, now).SetProperty(c => c.RevokedReason, "management agent revoked"), http.RequestAborted);
        await db.EnrollmentTokens.Where(t => t.HostId == id && t.ConsumedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(t => t.ExpiresAt, now), http.RequestAborted);
        agents.DisconnectManagementAgent(id, "management agent revoked");
        await http.WriteAuditAsync("management_agents.revoke", resourceType: "management_agent", resourceId: id.ToString("D"), details: new { certificatesRevoked = certs });
        return Results.NoContent();
    }

    /// <summary>Only when no host uses it any more (move or delete its remote hosts first).</summary>
    private static async Task<IResult> DeleteAsync(Guid id, InventoryManagementEndpoints.ConfirmRequest req, HttpContext http, PortalDbContext db, IAgentDispatcher agents)
    {
        if ((await http.GuardAsync(Permissions.HostsManage, ResourceRef.Global, "management_agents.delete") ?? await http.RequireRecentReauthAsync()) is { } denied)
        {
            return denied;
        }
        var agent = await db.ManagementAgents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, http.RequestAborted);
        if (agent is null)
        {
            return ApiSupport.NotFound();
        }
        if (!string.Equals(req.ConfirmName, agent.DisplayName, StringComparison.Ordinal))
        {
            return ApiSupport.BadRequest("confirmation_required", "Type the agent name exactly to confirm.");
        }
        if (await db.Hosts.AnyAsync(h => h.ManagementAgentId == id, http.RequestAborted))
        {
            return ApiSupport.Conflict("management_agent_in_use", "Remote hosts still use this management agent: assign them to another agent or delete them first.");
        }
        agents.DisconnectManagementAgent(id, "management agent deleted");
        await db.EnrollmentTokens.Where(t => t.HostId == id).ExecuteDeleteAsync(http.RequestAborted);
        await db.AgentCertificates.Where(c => c.HostId == id).ExecuteDeleteAsync(http.RequestAborted);
        await db.ManagementAgents.Where(a => a.Id == id).ExecuteDeleteAsync(http.RequestAborted);
        await http.WriteAuditAsync("management_agents.delete", resourceType: "management_agent", resourceId: id.ToString("D"), details: new { agent.DisplayName, agent.MachineName });
        return Results.NoContent();
    }

    // ------------------------------------------------------------------ remote hosts

    private static string? ValidateRemote(RemoteHostRequest req, bool passwordRequired) =>
        RemoteHosts.Validate(req.ComputerName?.Trim(), req.UserName?.Trim(), req.Password, passwordRequired);

    private static async Task<IResult> CreateRemoteHostAsync(RemoteHostRequest req, HttpContext http, PortalDbContext db, KeyRing keys,
        IAgentDispatcher agents, TimeProvider time)
    {
        if ((await http.GuardAsync(Permissions.HostsManage, ResourceRef.Global, "hosts.create_remote") ?? await http.RequireRecentReauthAsync()) is { } denied)
        {
            return denied;
        }
        if (req.DisplayName is null || !ApiSupport.DisplayNameRegex().IsMatch(req.DisplayName))
        {
            return ApiSupport.BadRequest("invalid_display_name");
        }
        if (ValidateRemote(req, passwordRequired: true) is { } invalid)
        {
            return ApiSupport.BadRequest(invalid);
        }
        if (!await db.ManagementAgents.AnyAsync(a => a.Id == req.ManagementAgentId && a.Status != HostStatus.Revoked, http.RequestAborted))
        {
            return ApiSupport.BadRequest("invalid_management_agent");
        }
        var computer = req.ComputerName!.Trim();
        if (await db.Hosts.AnyAsync(h => h.DisplayName == req.DisplayName, http.RequestAborted))
        {
            return ApiSupport.Conflict("host_exists");
        }
        if (await db.Hosts.AnyAsync(h => h.RemoteComputerName == computer || h.MachineName == computer || h.DnsHostName == computer, http.RequestAborted))
        {
            return ApiSupport.Conflict("host_exists", "This computer is already in the portal.");
        }
        var now = time.GetUtcNow();
        var host = new ManagedHost
        {
            DisplayName = req.DisplayName,
            CreatedAt = now,
            EnrolledAt = now,
            Status = HostStatus.Active,
            ConnectionMode = HostConnectionMode.Remote,
            ManagementAgentId = req.ManagementAgentId,
            RemoteComputerName = computer,
            RemoteUserName = req.UserName!.Trim(),
            RemoteUseHttps = req.UseHttps,
            RemoteCredentialUpdatedAt = now,
        };
        host.RemotePasswordEncrypted = RemoteHostCredentials.Protect(keys, host.Id, req.Password!);
        db.Hosts.Add(host);
        await db.SaveChangesAsync(http.RequestAborted);
        await agents.ReconfigureManagementAgentAsync(req.ManagementAgentId, http.RequestAborted);
        await http.WriteAuditAsync("hosts.create_remote", resourceType: "host", resourceId: host.Id.ToString("D"),
            details: new { host.DisplayName, computerName = computer, userName = host.RemoteUserName, req.UseHttps, managementAgentId = req.ManagementAgentId });
        return Results.Ok(new { hostId = host.Id });
    }

    /// <summary>
    /// Changes connection, account, management agent; the password only when a new one is given. Re-enables a revoked
    /// remote host (it has no agent of its own to re-enroll).
    /// </summary>
    private static async Task<IResult> UpdateRemoteHostAsync(Guid id, RemoteHostRequest req, HttpContext http, PortalDbContext db, KeyRing keys,
        IAgentDispatcher agents, TimeProvider time)
    {
        if ((await http.GuardAsync(Permissions.HostsManage, ResourceRef.Global, "hosts.update_remote") ?? await http.RequireRecentReauthAsync()) is { } denied)
        {
            return denied;
        }
        var host = await db.Hosts.FirstOrDefaultAsync(h => h.Id == id, http.RequestAborted);
        if (host is null)
        {
            return ApiSupport.NotFound();
        }
        if (host.ConnectionMode != HostConnectionMode.Remote)
        {
            return ApiSupport.BadRequest("not_a_remote_host", "This host runs its own agent.");
        }
        if (ValidateRemote(req, passwordRequired: false) is { } invalid)
        {
            return ApiSupport.BadRequest(invalid);
        }
        if (!await db.ManagementAgents.AnyAsync(a => a.Id == req.ManagementAgentId && a.Status != HostStatus.Revoked, http.RequestAborted))
        {
            return ApiSupport.BadRequest("invalid_management_agent");
        }
        var computer = req.ComputerName!.Trim();
        if (await db.Hosts.AnyAsync(h => h.Id != id && (h.RemoteComputerName == computer || h.MachineName == computer || h.DnsHostName == computer), http.RequestAborted))
        {
            return ApiSupport.Conflict("host_exists", "This computer is already in the portal.");
        }
        var before = new { computerName = host.RemoteComputerName, userName = host.RemoteUserName, useHttps = host.RemoteUseHttps, managementAgentId = host.ManagementAgentId };
        var previousAgent = host.ManagementAgentId;
        host.RemoteComputerName = computer;
        host.RemoteUserName = req.UserName!.Trim();
        host.RemoteUseHttps = req.UseHttps;
        host.ManagementAgentId = req.ManagementAgentId;
        var passwordChanged = req.Password is not null;
        if (passwordChanged)
        {
            host.RemotePasswordEncrypted = RemoteHostCredentials.Protect(keys, host.Id, req.Password!);
        }
        host.RemoteCredentialUpdatedAt = time.GetUtcNow();
        // A revoked remote host has no agent to re-enroll: saving its connection again re-enables it.
        var reactivated = host.Status == HostStatus.Revoked;
        host.Status = HostStatus.Active;
        await db.SaveChangesAsync(http.RequestAborted);
        if (previousAgent is { } old && old != req.ManagementAgentId)
        {
            agents.Disconnect(id, "moved to another management agent");
            await agents.ReconfigureManagementAgentAsync(old, http.RequestAborted);
        }
        await agents.ReconfigureManagementAgentAsync(req.ManagementAgentId, http.RequestAborted);
        await http.WriteAuditAsync("hosts.update_remote", resourceType: "host", resourceId: id.ToString("D"), details: new
        {
            before,
            after = new { computerName = computer, userName = host.RemoteUserName, useHttps = host.RemoteUseHttps, managementAgentId = host.ManagementAgentId },
            passwordChanged,
            reactivated,
        });
        return Results.NoContent();
    }
}
