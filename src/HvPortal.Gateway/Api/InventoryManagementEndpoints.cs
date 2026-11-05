using System.Text.Json;
using HvPortal.Contracts.Agent;
using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Agents;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Jobs;
using HvPortal.Gateway.Security;
using Microsoft.EntityFrameworkCore;

namespace HvPortal.Gateway.Api;

/// <summary>Descriptions, host removal, VM details on demand, VM deletion (job), forgetting missing VMs.</summary>
public static class InventoryManagementEndpoints
{
    public sealed record HostUpdateRequest(string? DisplayName, string? Description);
    public sealed record VmUpdateRequest(string? Description);
    public sealed record ConfirmRequest(string? ConfirmName, string? IdempotencyKey);

    public const int MaxDescriptionLength = 1000;

    public static void Map(WebApplication app)
    {
        app.MapMethods("/api/v1/hosts/{id:guid}", ["PATCH"], UpdateHostAsync).WithTags("Hosts");
        app.MapPost("/api/v1/hosts/{id:guid}/delete", DeleteHostAsync).WithTags("Hosts");
        app.MapMethods("/api/v1/vms/{id:guid}", ["PATCH"], UpdateVmAsync).WithTags("Virtual machines");
        app.MapGet("/api/v1/vms/{id:guid}/details", VmDetailsAsync).WithTags("Virtual machines");
        app.MapPost("/api/v1/vms/{id:guid}/delete", DeleteVmAsync).WithTags("Virtual machines");
        app.MapPost("/api/v1/vms/{id:guid}/forget", ForgetVmAsync).WithTags("Virtual machines");
    }

    /// <summary>Trims, removes control characters (except line breaks), enforces the length limit. Empty = null.</summary>
    internal static (string? Value, bool Ok) CleanDescription(string? text)
    {
        if (text is null)
        {
            return (null, true);
        }
        var clean = new string(text.Where(c => !char.IsControl(c) || c is '\n' or '\r' or '\t').ToArray()).Trim();
        return clean.Length > MaxDescriptionLength ? (null, false) : (clean.Length == 0 ? null : clean, true);
    }

    private static async Task<IResult> UpdateHostAsync(Guid id, HostUpdateRequest req, HttpContext http, PortalDbContext db)
    {
        if (await http.GuardAsync(Permissions.HostsManage, ResourceRef.Global, "hosts.update") is { } denied)
        {
            return denied;
        }
        var host = await db.Hosts.FirstOrDefaultAsync(h => h.Id == id, http.RequestAborted);
        if (host is null)
        {
            return ApiSupport.NotFound();
        }
        var before = new { host.DisplayName, host.Description };
        if (req.DisplayName is not null)
        {
            if (!ApiSupport.DisplayNameRegex().IsMatch(req.DisplayName))
            {
                return ApiSupport.BadRequest("invalid_display_name");
            }
            if (await db.Hosts.AnyAsync(h => h.Id != id && h.DisplayName == req.DisplayName, http.RequestAborted))
            {
                return ApiSupport.Conflict("host_exists");
            }
            host.DisplayName = req.DisplayName;
        }
        var (description, ok) = CleanDescription(req.Description);
        if (!ok)
        {
            return ApiSupport.BadRequest("description_too_long", $"Max {MaxDescriptionLength} characters.");
        }
        if (req.Description is not null)
        {
            host.Description = description;
        }
        await db.SaveChangesAsync(http.RequestAborted);
        await http.WriteAuditAsync("hosts.update", resourceType: "host", resourceId: id.ToString("D"),
            details: new { before, after = new { host.DisplayName, host.Description } });
        return Results.NoContent();
    }

    /// <summary>
    /// Removes a host from the portal: certificates and tokens revoked/deleted, agent disconnected, VM records,
    /// scoped role assignments and group memberships removed. Nothing is changed on the Hyper-V host itself.
    /// Refused while jobs on the host are queued or running.
    /// </summary>
    private static async Task<IResult> DeleteHostAsync(Guid id, ConfirmRequest req, HttpContext http, PortalDbContext db, IAgentDispatcher agents)
    {
        if ((await http.GuardAsync(Permissions.HostsManage, ResourceRef.Global, "hosts.delete") ?? await http.RequireRecentReauthAsync()) is { } denied)
        {
            return denied;
        }
        var host = await db.Hosts.AsNoTracking().FirstOrDefaultAsync(h => h.Id == id, http.RequestAborted);
        if (host is null)
        {
            return ApiSupport.NotFound();
        }
        if (!string.Equals(req.ConfirmName, host.DisplayName, StringComparison.Ordinal))
        {
            return ApiSupport.BadRequest("confirmation_required", "Type the host name exactly to confirm.");
        }
        if (await db.Jobs.AnyAsync(j => j.HostId == id && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running || j.Status == JobStatus.Reconciling), http.RequestAborted))
        {
            return ApiSupport.Conflict("host_busy", "Operations are still running on this host. Wait for them to finish.");
        }
        agents.Disconnect(id, "host deleted");
        await using var tx = await db.Database.BeginTransactionAsync(http.RequestAborted);
        var vms = await InventoryCleanup.RemoveHostAsync(db, id, http.RequestAborted);
        await tx.CommitAsync(http.RequestAborted);
        await http.WriteAuditAsync("hosts.delete", resourceType: "host", resourceId: id.ToString("D"),
            details: new { host.DisplayName, host.MachineName, vmRecordsRemoved = vms });
        return Results.NoContent();
    }

    private static async Task<VirtualMachine?> ReadableVmAsync(Guid id, HttpContext http, PortalDbContext db, AccessEvaluator access) =>
        await db.VirtualMachines.FirstOrDefaultAsync(v => v.Id == id, http.RequestAborted) is { } vm
        && await access.HasAsync(http.User.UserId(), Permissions.InventoryRead, ResourceRef.Vm(id), http.RequestAborted)
            ? vm
            : null;

    private static async Task<IResult> UpdateVmAsync(Guid id, VmUpdateRequest req, HttpContext http, PortalDbContext db, AccessEvaluator access)
    {
        var vm = await ReadableVmAsync(id, http, db, access);
        if (vm is null)
        {
            return ApiSupport.NotFound();
        }
        if (await http.GuardAsync(Permissions.InventoryAnnotate, ResourceRef.Vm(id), "vms.update") is { } denied)
        {
            return denied;
        }
        var (description, ok) = CleanDescription(req.Description);
        if (!ok)
        {
            return ApiSupport.BadRequest("description_too_long", $"Max {MaxDescriptionLength} characters.");
        }
        var before = vm.Description;
        vm.Description = description;
        await db.SaveChangesAsync(http.RequestAborted);
        await http.WriteAuditAsync("vms.update", resourceType: "vm", resourceId: id.ToString("D"), details: new { before, after = description });
        return Results.NoContent();
    }

    /// <summary>Live details from the agent; when the host is unreachable the last collected copy is returned as stale.</summary>
    private static async Task<IResult> VmDetailsAsync(Guid id, HttpContext http, PortalDbContext db, AccessEvaluator access, IAgentDispatcher agents, TimeProvider time)
    {
        var vm = await ReadableVmAsync(id, http, db, access);
        if (vm is null)
        {
            return ApiSupport.NotFound();
        }
        string? error = null;
        if (agents.IsConnected(vm.HostId) && !vm.IsMissing)
        {
            try
            {
                var r = await agents.SendAsync(vm.HostId, new AgentCommand { CommandId = Guid.NewGuid(), Kind = AgentCommandKind.GetVmDetails, VmId = vm.HyperVId },
                    null, TimeSpan.FromSeconds(45), http.RequestAborted);
                if (r.Status == AgentCommandStatus.Succeeded && r.Details is { } d)
                {
                    vm.DetailsJson = JsonSerializer.Serialize(d with
                    {
                        Disks = d.Disks.Take(64).ToList(),
                        NetworkAdapters = d.NetworkAdapters.Take(32).ToList(),
                        Warnings = d.Warnings.Take(20).ToList(),
                    });
                    vm.DetailsCollectedAt = time.GetUtcNow();
                    await db.SaveChangesAsync(http.RequestAborted);
                    return Results.Ok(new { details = d, stale = false, collectedAt = vm.DetailsCollectedAt });
                }
                error = r.Message ?? r.Status.ToString();
            }
            catch (Exception ex) when (ex is HostNotConnectedException or AgentChannelLostException or TimeoutException)
            {
                error = ex.Message;
            }
        }
        else
        {
            error = "The host agent is not connected.";
        }
        var cached = vm.DetailsJson is null ? null : JsonSerializer.Deserialize<VmDetails>(vm.DetailsJson);
        return Results.Ok(new { details = cached, stale = true, collectedAt = vm.DetailsCollectedAt, error });
    }

    private static async Task<IResult> DeleteVmAsync(Guid id, ConfirmRequest req, HttpContext http, PortalDbContext db, AccessEvaluator access,
        JobService jobs, IAgentDispatcher agents)
    {
        var vm = await ReadableVmAsync(id, http, db, access);
        if (vm is null)
        {
            return ApiSupport.NotFound();
        }
        if ((await http.GuardAsync(Permissions.VmDelete, ResourceRef.Vm(id), "vm.delete.request") ?? await http.RequireRecentReauthAsync()) is { } denied)
        {
            return denied;
        }
        if (!string.Equals(req.ConfirmName, vm.Name, StringComparison.Ordinal))
        {
            return ApiSupport.BadRequest("confirmation_required", "Type the VM name exactly to confirm.");
        }
        if (vm.IsMissing)
        {
            return ApiSupport.Conflict("vm_missing", "The VM is no longer on the host; use 'Remove from portal'.");
        }
        if (vm.PowerState is not (VmPowerState.Off or VmPowerState.Saved))
        {
            return ApiSupport.Conflict("invalid_state", "Turn off the VM before deleting it.");
        }
        if (req.IdempotencyKey is not null && !ApiSupport.IdempotencyKeyRegex().IsMatch(req.IdempotencyKey))
        {
            return ApiSupport.BadRequest("invalid_idempotency_key");
        }
        try
        {
            var (job, created) = await jobs.CreateVmDeleteJobAsync(http.User.UserId(), vm, req.IdempotencyKey, http.RequestAborted);
            if (created)
            {
                await http.WriteAuditAsync("vm.delete.request", resourceType: "vm", resourceId: id.ToString("D"), details: new { jobId = job.Id, vm = vm.Name });
            }
            return Results.Accepted($"/api/v1/jobs/{job.Id}", new { jobId = job.Id, created });
        }
        catch (IdempotencyConflictException ex)
        {
            return ApiSupport.Conflict("idempotency_conflict", ex.Message);
        }
    }

    /// <summary>Removes the record of a VM that no longer exists on its host (deleted outside the portal).</summary>
    private static async Task<IResult> ForgetVmAsync(Guid id, HttpContext http, PortalDbContext db, AccessEvaluator access, TimeProvider time)
    {
        var vm = await ReadableVmAsync(id, http, db, access);
        if (vm is null)
        {
            return ApiSupport.NotFound();
        }
        if (await http.GuardAsync(Permissions.VmDelete, ResourceRef.Vm(id), "vm.forget") is { } denied)
        {
            return denied;
        }
        if (!vm.IsMissing)
        {
            return ApiSupport.Conflict("vm_present", "The VM still exists on the host.");
        }
        await InventoryCleanup.RemoveVmAsync(db, vm.Id, vm.HostId, "vm.forgotten", time.GetUtcNow(), http.RequestAborted);
        await http.WriteAuditAsync("vm.forget", resourceType: "vm", resourceId: id.ToString("D"), details: new { vm.Name });
        return Results.NoContent();
    }
}
