using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Agents;
using HvPortal.Gateway.Audit;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Jobs;
using HvPortal.Gateway.Quotas;
using HvPortal.Gateway.Security;
using HvPortal.Gateway.Templates;
using Microsoft.EntityFrameworkCore;

namespace HvPortal.Gateway.Api;

/// <summary>
/// VM templates (ADR-028). A template is created from a VM that is off (its disks are copied into the template
/// library of the host; the VM is not changed), listed to who may create VMs on that host, and deleted with its files.
/// VMs are created from a template through the normal creation endpoint (POST /hosts/{id}/vms with vm.templateId).
/// Creating, renaming and deleting need templates.manage on the host; using a template needs vm.create.
/// </summary>
public static class TemplateEndpoints
{
    public sealed record CaptureRequest(string? Name, string? Description, string? IdempotencyKey);
    public sealed record UpdateRequest(string? Name, string? Description);
    public sealed record DeleteRequest(string? ConfirmName, string? IdempotencyKey);
    public sealed record LibraryRequest(string? Path);

    /// <summary>Agent protocol that knows templates (agent 0.6.0).</summary>
    public const int TemplateProtocol = 8;

    private static IResult Outdated() => ApiSupport.Error(StatusCodes.Status501NotImplemented, "agent_outdated",
        "Templates need agent 0.6.0 or later on this host. Update the agent.");

    public static void Map(WebApplication app)
    {
        var templates = app.MapGroup("/api/v1/templates").WithTags("Templates");
        templates.MapGet("/", ListAsync);
        templates.MapPatch("/{id:guid}", UpdateAsync);
        templates.MapPost("/{id:guid}/delete", DeleteAsync);
        app.MapPost("/api/v1/vms/{id:guid}/template", CaptureAsync).WithTags("Templates");
        app.MapPut("/api/v1/hosts/{id:guid}/template-library", SetLibraryAsync).WithTags("Templates");
    }

    /// <summary>
    /// Templates of the hosts where the caller may create VMs (directly, or inside <paramref name="groupId"/> when the
    /// limits of that folder open the host) or manages templates. Nobody else learns that a template exists.
    /// </summary>
    private static async Task<IResult> ListAsync(Guid? hostId, Guid? groupId, HttpContext http, PortalDbContext db, AccessEvaluator access,
        GroupPolicyService policies, IAgentDispatcher agents)
    {
        var ct = http.RequestAborted;
        var uid = http.User.UserId();
        var all = await db.VmTemplates.AsNoTracking().Where(t => hostId == null || t.HostId == hostId).ToListAsync(ct);
        var hostIds = all.Select(t => t.HostId).Distinct().ToList();
        var hosts = await db.Hosts.AsNoTracking().Where(h => hostIds.Contains(h.Id) && h.Status == HostStatus.Active).ToDictionaryAsync(h => h.Id, ct);
        var rights = new Dictionary<Guid, (bool Deploy, bool Manage)>();
        foreach (var id in hosts.Keys)
        {
            rights[id] = (await policies.HoldsOnHostOrGroupAsync(uid, Permissions.VmCreate, id, groupId, ct),
                await access.HasAsync(uid, Permissions.TemplatesManage, ResourceRef.Host(id), ct));
        }
        var items = all.Where(t => rights.TryGetValue(t.HostId, out var r) && (r.Deploy || r.Manage))
            .OrderBy(t => hosts[t.HostId].DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t =>
            {
                var (deploy, manage) = rights[t.HostId];
                var host = hosts[t.HostId];
                var usable = agents.IsConnected(t.HostId) && agents.ProtocolVersionOf(t.HostId) >= TemplateProtocol && host.Access == HyperVAccessStatus.Ok;
                return new
                {
                    t.Id,
                    t.HostId,
                    hostName = host.DisplayName,
                    t.Name,
                    t.Description,
                    status = t.Status.ToString(),
                    t.Error,
                    t.SourceVmName,
                    t.Generation,
                    t.ProcessorCount,
                    t.MemoryStartupMb,
                    t.DynamicMemory,
                    t.MemoryMinimumMb,
                    t.MemoryMaximumMb,
                    t.SecureBootTemplate,
                    t.TpmEnabled,
                    disks = TemplateRules.Disks(t),
                    bootDiskGb = TemplateRules.BootDiskGb(t),
                    t.SizeBytes,
                    t.CreatedAt,
                    t.JobId,
                    // Where the files are is for who manages them.
                    folderPath = manage ? t.FolderPath : null,
                    can = new
                    {
                        deploy = deploy && usable && t.Status == TemplateStatus.Ready,
                        manage,
                        delete = manage && usable && t.Status is not (TemplateStatus.Creating or TemplateStatus.Deleting),
                    },
                };
            }).ToList();
        return Results.Ok(new { items });
    }

    private static async Task<IResult> CaptureAsync(Guid id, CaptureRequest req, HttpContext http, PortalDbContext db, AccessEvaluator access, JobService jobs,
        IAgentDispatcher agents, TimeProvider time)
    {
        var ct = http.RequestAborted;
        var vm = await VmOperationsEndpoints.ReadableVmAsync(id, http, db, access);
        if (vm is null)
        {
            return ApiSupport.NotFound();
        }
        if (await http.GuardAsync(Permissions.TemplatesManage, ResourceRef.Host(vm.HostId), "template.capture.request") is { } denied)
        {
            return denied;
        }
        var host = await db.Hosts.AsNoTracking().FirstOrDefaultAsync(h => h.Id == vm.HostId && h.Status == HostStatus.Active, ct);
        if (host is null)
        {
            return ApiSupport.NotFound();
        }
        var uid = http.User.UserId();
        var key = VmOperationsEndpoints.IdempotencyKey(http, req.IdempotencyKey);
        if (key == "!")
        {
            return ApiSupport.BadRequest("invalid_idempotency_key");
        }
        // The same request sent twice gives the same task (and not "name already used").
        if (key is not null && await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.RequestedByUserId == uid && j.IdempotencyKey == key, ct) is { } replay)
        {
            return replay.TargetId == vm.Id && replay.Type == JobService.TemplateCaptureJobType
                ? Results.Accepted($"/api/v1/jobs/{replay.Id}", new { jobId = replay.Id, created = false })
                : ApiSupport.Conflict("idempotency_conflict", "The idempotency key was already used for a different operation.");
        }
        var name = req.Name?.Trim() ?? "";
        if (!TemplateRules.IsValidName(name))
        {
            return ApiSupport.BadRequest("invalid_template_name", "The template name is required (up to 100 characters).");
        }
        var description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim();
        if (description is { Length: > 2000 })
        {
            return ApiSupport.BadRequest("description_too_long");
        }
        if (vm.IsMissing || vm.PowerState != VmPowerState.Off)
        {
            return ApiSupport.Conflict("invalid_state", "The VM must be off to create a template from it: shut it down first.");
        }
        if (!agents.IsConnected(vm.HostId))
        {
            return ApiSupport.Error(StatusCodes.Status503ServiceUnavailable, "host_unreachable", "The host agent is not connected.");
        }
        if (agents.ProtocolVersionOf(vm.HostId) < TemplateProtocol)
        {
            return Outdated();
        }
        if (!TemplateRules.IsValidLibraryPath(host.TemplateLibraryPath))
        {
            return ApiSupport.Conflict("template_library_not_set", "This host has no template library folder yet (Host > Settings > Template library).");
        }
        if (await db.VmTemplates.AnyAsync(t => t.HostId == vm.HostId && t.Name == name, ct))
        {
            return ApiSupport.Conflict("template_name_exists", "A template with this name already exists on the host.");
        }
        var library = HostPaths.Normalize(host.TemplateLibraryPath!);
        var template = new VmTemplate
        {
            HostId = vm.HostId,
            Name = name,
            Description = description,
            Status = TemplateStatus.Creating,
            LibraryPath = library,
            FolderPath = "",
            SourceVmName = vm.Name,
            CreatedAt = time.GetUtcNow(),
            CreatedByUserId = uid,
        };
        template.FolderPath = TemplateRules.FolderOf(library, template.Id);
        // Saved together with the job (same unit of work): the job always finds its template, and the other way round.
        db.VmTemplates.Add(template);
        var args = new NewTemplateRequest { VmId = vm.HyperVId, Folder = template.FolderPath, Roots = [library] };
        // The size on disk is not known here: the provisioned size bounds it; unknown = half of the longest wait.
        var timeout = vm.DiskProvisionedBytes is > 0 ? TemplateRules.CopyTimeoutSeconds(vm.DiskProvisionedBytes.Value) : TemplateRules.MaxCopySeconds / 2;
        var (job, _) = await jobs.CreateOperationJobAsync(uid, new JobService.OperationJobSpec(JobService.TemplateCaptureJobType, "Create", ScopeType.Vm, vm.Id,
            vm.Name, vm.HostId, Permissions.TemplatesManage, new JobService.OperationParameters(HvOperations.ToElement(args)), timeout,
            $"Create template {name} from VM {vm.Name} on {host.DisplayName}"), key, ct);
        template.JobId = job.Id;
        await db.SaveChangesAsync(ct);
        await http.WriteAuditAsync("template.capture.request", resourceType: "vm", resourceId: vm.Id.ToString("D"),
            details: new { jobId = job.Id, request = new { vm = vm.Name, template = name, templateId = template.Id, folder = template.FolderPath } });
        return Results.Accepted($"/api/v1/jobs/{job.Id}", new { jobId = job.Id, created = true, templateId = template.Id });
    }

    private static async Task<VmTemplate?> ManagedTemplateAsync(Guid id, HttpContext http, PortalDbContext db, AccessEvaluator access, GroupPolicyService policies)
    {
        var template = await db.VmTemplates.FirstOrDefaultAsync(t => t.Id == id, http.RequestAborted);
        if (template is null)
        {
            return null;
        }
        // Visible to who can use or manage it; anyone else gets "not found" (the guard then decides on templates.manage).
        var uid = http.User.UserId();
        return await access.HasAsync(uid, Permissions.TemplatesManage, ResourceRef.Host(template.HostId), http.RequestAborted)
               || await policies.HoldsOnHostOrGroupAsync(uid, Permissions.VmCreate, template.HostId, null, http.RequestAborted)
            ? template
            : null;
    }

    private static async Task<IResult> UpdateAsync(Guid id, UpdateRequest req, HttpContext http, PortalDbContext db, AccessEvaluator access, GroupPolicyService policies)
    {
        var template = await ManagedTemplateAsync(id, http, db, access, policies);
        if (template is null)
        {
            return ApiSupport.NotFound();
        }
        if (await http.GuardAsync(Permissions.TemplatesManage, ResourceRef.Host(template.HostId), "templates.update") is { } denied)
        {
            return denied;
        }
        var before = new { template.Name, template.Description };
        if (req.Name is not null)
        {
            var name = req.Name.Trim();
            if (!TemplateRules.IsValidName(name))
            {
                return ApiSupport.BadRequest("invalid_template_name", "The template name is required (up to 100 characters).");
            }
            if (name != template.Name && await db.VmTemplates.AnyAsync(t => t.HostId == template.HostId && t.Name == name && t.Id != id, http.RequestAborted))
            {
                return ApiSupport.Conflict("template_name_exists", "A template with this name already exists on the host.");
            }
            template.Name = name;
        }
        if (req.Description is not null)
        {
            var description = req.Description.Trim();
            if (description.Length > 2000)
            {
                return ApiSupport.BadRequest("description_too_long");
            }
            template.Description = description.Length == 0 ? null : description;
        }
        await db.SaveChangesAsync(http.RequestAborted);
        await http.WriteAuditAsync("templates.update", resourceType: "template", resourceId: id.ToString("D"),
            details: new { template.Name, before, after = new { template.Name, template.Description } });
        return Results.Ok(new { template.Id, template.Name, template.Description });
    }

    private static async Task<IResult> DeleteAsync(Guid id, DeleteRequest req, HttpContext http, PortalDbContext db, AccessEvaluator access, GroupPolicyService policies,
        JobService jobs, IAgentDispatcher agents)
    {
        var ct = http.RequestAborted;
        var template = await ManagedTemplateAsync(id, http, db, access, policies);
        if (template is null)
        {
            return ApiSupport.NotFound();
        }
        if (await http.GuardAsync(Permissions.TemplatesManage, ResourceRef.Host(template.HostId), "template.delete.request") is { } denied)
        {
            return denied;
        }
        var uid = http.User.UserId();
        var key = VmOperationsEndpoints.IdempotencyKey(http, req.IdempotencyKey);
        if (key == "!")
        {
            return ApiSupport.BadRequest("invalid_idempotency_key");
        }
        if (key is not null && await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.RequestedByUserId == uid && j.IdempotencyKey == key, ct) is { } replay)
        {
            return replay.Type == JobService.TemplateDeleteJobType && replay.Id == template.JobId
                ? Results.Accepted($"/api/v1/jobs/{replay.Id}", new { jobId = replay.Id, created = false })
                : ApiSupport.Conflict("idempotency_conflict", "The idempotency key was already used for a different operation.");
        }
        if (!string.Equals(req.ConfirmName, template.Name, StringComparison.Ordinal))
        {
            return ApiSupport.BadRequest("confirmation_mismatch", "Type the name of the template to confirm.");
        }
        if (template.Status is TemplateStatus.Creating or TemplateStatus.Deleting)
        {
            return ApiSupport.Conflict("template_busy", "The template is being created or deleted: wait for the task to finish.");
        }
        var host = await db.Hosts.AsNoTracking().FirstOrDefaultAsync(h => h.Id == template.HostId && h.Status == HostStatus.Active, ct);
        if (host is null)
        {
            return ApiSupport.NotFound();
        }
        if (!agents.IsConnected(template.HostId))
        {
            return ApiSupport.Error(StatusCodes.Status503ServiceUnavailable, "host_unreachable", "The host agent is not connected.");
        }
        if (agents.ProtocolVersionOf(template.HostId) < TemplateProtocol)
        {
            return Outdated();
        }
        var args = new TemplateFolderRequest { Folder = template.FolderPath, Roots = [template.LibraryPath] };
        // The new state is saved together with the job.
        template.Status = TemplateStatus.Deleting;
        var (job, _) = await jobs.CreateOperationJobAsync(uid, new JobService.OperationJobSpec(JobService.TemplateDeleteJobType, "Delete", ScopeType.Host,
            template.HostId, template.Name, template.HostId, Permissions.TemplatesManage, new JobService.OperationParameters(HvOperations.ToElement(args)), 900,
            $"Delete template {template.Name} on {host.DisplayName}"), key, ct);
        template.JobId = job.Id;
        await db.SaveChangesAsync(ct);
        await http.WriteAuditAsync("template.delete.request", resourceType: "host", resourceId: template.HostId.ToString("D"),
            details: new { jobId = job.Id, request = new { template = template.Name, templateId = template.Id, folder = template.FolderPath } });
        return Results.Accepted($"/api/v1/jobs/{job.Id}", new { jobId = job.Id, created = true });
    }

    /// <summary>The folder of the host where its templates are kept. Existing templates stay where they were created.</summary>
    private static async Task<IResult> SetLibraryAsync(Guid id, LibraryRequest req, HttpContext http, PortalDbContext db)
    {
        if (await http.GuardAsync(Permissions.HostsManage, ResourceRef.Global, "hosts.template_library.update") is { } denied)
        {
            return denied;
        }
        var host = await db.Hosts.FirstOrDefaultAsync(h => h.Id == id, http.RequestAborted);
        if (host is null)
        {
            return ApiSupport.NotFound();
        }
        var path = string.IsNullOrWhiteSpace(req.Path) ? null : HostPaths.Normalize(req.Path.Trim());
        if (path is not null && !TemplateRules.IsValidLibraryPath(path))
        {
            return ApiSupport.BadRequest("invalid_template_library", "A local folder such as D:\\Templates (no UNC paths, no '..', no system folders).");
        }
        var before = host.TemplateLibraryPath;
        host.TemplateLibraryPath = path;
        await db.SaveChangesAsync(http.RequestAborted);
        await http.WriteAuditAsync("hosts.template_library.update", resourceType: "host", resourceId: id.ToString("D"), details: new { before, after = path });
        return Results.Ok(new { path });
    }
}
