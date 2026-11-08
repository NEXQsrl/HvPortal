using System.Text.Json;
using HvPortal.Contracts.Agent;
using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Agents;
using HvPortal.Gateway.Audit;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Jobs;
using HvPortal.Gateway.Quotas;
using HvPortal.Gateway.Security;
using Microsoft.EntityFrameworkCore;

namespace HvPortal.Gateway.Api;

/// <summary>
/// Protocol 9 host and VM operations: health of the host (read live), VM move to another host or folder (jobs), the
/// storage folders of remote hosts and the backup staging folder of a host (host settings).
/// </summary>
public static class HostOperationsEndpoints
{
    public sealed record MoveRequest(string? Mode, Guid? DestinationHostId, string? DestinationPath, bool IncludeStorage = true, string? IdempotencyKey = null);
    public sealed record FoldersRequest(string[]? Folders);
    public sealed record StagingRequest(string? Path);

    public static void Map(WebApplication app)
    {
        var hosts = app.MapGroup("/api/v1/hosts").WithTags("Hosts");
        hosts.MapGet("/{id:guid}/health", HealthAsync);
        hosts.MapPut("/{id:guid}/storage-folders", SetStorageFoldersAsync);
        hosts.MapPut("/{id:guid}/backup-staging", SetStagingAsync);
        app.MapGroup("/api/v1/vms").WithTags("Virtual machines").MapPost("/{id:guid}/move", MoveAsync);
    }

    // ------------------------------------------------------------------ health

    private static async Task<IResult> HealthAsync(Guid id, HttpContext http, PortalDbContext db, AccessEvaluator access, IAgentDispatcher agents, TimeProvider time)
    {
        var host = await db.Hosts.AsNoTracking().FirstOrDefaultAsync(h => h.Id == id && h.Status == HostStatus.Active, http.RequestAborted);
        // Hardware and storage of the host: for who reads the host itself, not only some of its VMs.
        if (host is null || !await access.HasAsync(http.User.UserId(), Permissions.InventoryRead, ResourceRef.Host(id), http.RequestAborted))
        {
            return ApiSupport.NotFound();
        }
        if (agents.IsConnected(id) && agents.ProtocolVersionOf(id) < 9)
        {
            return ApiSupport.Error(StatusCodes.Status501NotImplemented, "agent_outdated", "The health of the host needs agent 0.7.0 or later. Update the agent.");
        }
        var (result, error) = await VmOperationsEndpoints.QueryAsync(agents, id, HvOperation.GetHostHealth, new { }, null, http.RequestAborted, timeoutSeconds: 120);
        if (error is not null)
        {
            return error;
        }
        var report = HvOperations.FromElement<HostHealthReport>(result) ?? new HostHealthReport();
        return Results.Ok(report with { Status = Overall(report, time.GetUtcNow()) });
    }

    /// <summary>The worst state read; recent critical events make it at least a warning; unreadable sections make it at least unknown.</summary>
    internal static string Overall(HostHealthReport r, DateTimeOffset now)
    {
        var statuses = new List<string?>();
        statuses.AddRange(r.PhysicalDisks.Select(d => d.Status));
        statuses.AddRange(r.Volumes.Select(v => v.Status));
        statuses.AddRange(r.StoragePools.Select(p => p.Status));
        statuses.AddRange(r.NetworkAdapters.Select(a => a.Health));
        if (r.Hardware is { Status: { } hs } && !hs.Equals("OK", StringComparison.OrdinalIgnoreCase))
        {
            statuses.Add(HealthLevel.Warning);
        }
        if (r.Services.Any(s => s.Name.Equals("vmms", StringComparison.OrdinalIgnoreCase) && !string.Equals(s.State, "Running", StringComparison.OrdinalIgnoreCase)))
        {
            statuses.Add(HealthLevel.Critical);
        }
        if (r.Events.Any(e => e.Level == "Critical" && now - e.TimeUtc < TimeSpan.FromHours(24)))
        {
            statuses.Add(HealthLevel.Warning);
        }
        if (r.Errors.Count > 0)
        {
            statuses.Add(HealthLevel.Unknown);
        }
        return statuses.Count == 0 ? HealthLevel.Unknown : HealthLevel.Worst(statuses);
    }

    // ------------------------------------------------------------------ VM move

    private static async Task<IResult> MoveAsync(Guid id, MoveRequest req, HttpContext http, PortalDbContext db, AccessEvaluator access, JobService jobs,
        IAgentDispatcher agents, GroupPolicyService policies)
    {
        var vm = await VmOperationsEndpoints.ReadableVmAsync(id, http, db, access);
        if (vm is null)
        {
            return ApiSupport.NotFound();
        }
        if (await http.GuardAsync(Permissions.VmMigrate, ResourceRef.Vm(id), "vm.migrate.request") is { } denied)
        {
            return denied;
        }
        if (vm.IsMissing)
        {
            return ApiSupport.Conflict("vm_missing", "The VM is not present in the latest inventory.");
        }
        if (!agents.IsConnected(vm.HostId))
        {
            return ApiSupport.Error(StatusCodes.Status503ServiceUnavailable, "host_unreachable", "The host agent is not connected.");
        }
        if (agents.ProtocolVersionOf(vm.HostId) < 9)
        {
            return ApiSupport.Error(StatusCodes.Status501NotImplemented, "agent_outdated", "Moving VMs needs agent 0.7.0 or later on the VM's host. Update the agent.");
        }
        var path = string.IsNullOrWhiteSpace(req.DestinationPath) ? null : HostPaths.Normalize(req.DestinationPath);
        var key = VmOperationsEndpoints.IdempotencyKey(http, req.IdempotencyKey);
        if (key == "!")
        {
            return ApiSupport.BadRequest("invalid_idempotency_key");
        }
        var host = await db.Hosts.AsNoTracking().FirstAsync(h => h.Id == vm.HostId, http.RequestAborted);
        if (req.Mode == "storage")
        {
            var spec = new MoveVmStorageRequest { VmId = vm.HyperVId, DestinationPath = path ?? "" };
            if (OperationValidation.Validate(spec) is { } invalid)
            {
                return ApiSupport.BadRequest(invalid, @"Local folder of the host such as D:\VMs (the VM goes to <folder>\<VM name>).");
            }
            if (InventoryEndpoints.StorageFoldersOf(host) is { Count: > 0 } allowed && !StorageFolderRules.IsInside(path!, allowed))
            {
                return ApiSupport.BadRequest("location_not_allowed", "The folder is outside the storage folders allowed on this host.");
            }
            if (await policies.CheckMoveAsync(vm, vm.HostId, path, http.RequestAborted) is { } violation)
            {
                await http.WriteAuditAsync("vm.migrate.request", AuditOutcome.Denied, "vm", id.ToString("D"), new { reason = violation.Code });
                return ApiSupport.Conflict(violation.Code, violation.Message);
            }
            return await VmOperationsEndpoints.QueueAsync(http, jobs, new JobService.OperationJobSpec(JobService.MigrateJobType, "Storage", ScopeType.Vm, vm.Id, vm.Name,
                vm.HostId, Permissions.VmMigrate, new JobService.OperationParameters(HvOperations.ToElement(spec)), 12 * 3600,
                $"Move the files of {vm.Name} to {path}"), key, "vm.migrate.request", new { mode = "storage", path });
        }
        if (req.Mode != "host")
        {
            return ApiSupport.BadRequest("invalid_move_mode", "Mode is 'host' or 'storage'.");
        }
        if (req.DestinationHostId is not { } destinationId || destinationId == vm.HostId)
        {
            return ApiSupport.BadRequest("invalid_destination_host", "Choose another host of the portal.");
        }
        var destination = await db.Hosts.AsNoTracking().FirstOrDefaultAsync(h => h.Id == destinationId && h.Status == HostStatus.Active, http.RequestAborted);
        if (destination is null || !await access.CanSeeHostAsync(http.User.UserId(), destinationId, http.RequestAborted))
        {
            return ApiSupport.BadRequest("invalid_destination_host", "The destination host does not exist.");
        }
        if (await http.GuardAsync(Permissions.VmCreate, ResourceRef.Host(destinationId), "vm.migrate.request") is { } deniedDestination)
        {
            return deniedDestination;
        }
        if (MigrationRules.DestinationName(destination) is not { } name)
        {
            return ApiSupport.Conflict("destination_unknown_name", "The DNS name of the destination host is not known yet (no inventory from it).");
        }
        var move = new MoveVmRequest { VmId = vm.HyperVId, DestinationHost = name, IncludeStorage = req.IncludeStorage, DestinationPath = req.IncludeStorage ? path : null };
        if (OperationValidation.Validate(move) is { } invalidMove)
        {
            return ApiSupport.BadRequest(invalidMove);
        }
        if (path is not null && InventoryEndpoints.StorageFoldersOf(destination) is { Count: > 0 } destAllowed && !StorageFolderRules.IsInside(path, destAllowed))
        {
            return ApiSupport.BadRequest("location_not_allowed", "The folder is outside the storage folders allowed on the destination host.");
        }
        if (await policies.CheckMoveAsync(vm, destinationId, req.IncludeStorage ? path : null, http.RequestAborted) is { } hostViolation)
        {
            await http.WriteAuditAsync("vm.migrate.request", AuditOutcome.Denied, "vm", id.ToString("D"), new { reason = hostViolation.Code, destinationId });
            return ApiSupport.Conflict(hostViolation.Code, hostViolation.Message);
        }
        return await VmOperationsEndpoints.QueueAsync(http, jobs, new JobService.OperationJobSpec(JobService.MigrateJobType, "Host", ScopeType.Vm, vm.Id, vm.Name,
            vm.HostId, Permissions.VmMigrate, new JobService.OperationParameters(HvOperations.ToElement(move), DestinationHostId: destinationId), 12 * 3600,
            $"Move {vm.Name} from {host.DisplayName} to {destination.DisplayName}"), key, "vm.migrate.request",
            new { mode = "host", destination = destination.DisplayName, destinationId, req.IncludeStorage, path });
    }

    // ------------------------------------------------------------------ host settings

    /// <summary>Remote hosts only: for hosts with their own agent the list is decided on the host (Install-Agent -StorageFolders).</summary>
    private static async Task<IResult> SetStorageFoldersAsync(Guid id, FoldersRequest req, HttpContext http, PortalDbContext db, IAgentDispatcher agents)
    {
        if (await http.GuardAsync(Permissions.HostsManage, ResourceRef.Global, "hosts.storage_folders.update") is { } denied)
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
            return ApiSupport.Conflict("decided_on_the_host", "The storage folders of a host with its own agent are set when the agent is installed (Install-Agent -StorageFolders), not from the portal.");
        }
        // Only a management agent 0.7.0+ enforces the list: an older one would silently ignore it.
        if (host.ManagementAgentId is { } managementId)
        {
            var version = agents.AgentOf(managementId)?.Version
                ?? await db.ManagementAgents.AsNoTracking().Where(a => a.Id == managementId).Select(a => a.AgentVersion).FirstOrDefaultAsync(http.RequestAborted);
            if (!Jobs.AgentVersions.AtLeast(version, "0.7.0"))
            {
                return ApiSupport.Error(StatusCodes.Status501NotImplemented, "agent_outdated",
                    "The management agent of this host is older than 0.7.0 and would not enforce the storage folders. Update it first.");
            }
        }
        var folders = (req.Folders ?? []).Select(f => f?.Trim() ?? "").Where(f => f.Length > 0).Select(HostPaths.Normalize).ToList();
        if (StorageFolderRules.Validate(folders) is { } invalid)
        {
            return ApiSupport.BadRequest(invalid, @"Up to 16 local folders such as D:\VMs (no system folders, no duplicates).");
        }
        var before = InventoryEndpoints.StorageFoldersOf(host);
        host.StorageFoldersJson = folders.Count == 0 ? null : JsonSerializer.Serialize(folders);
        await db.SaveChangesAsync(http.RequestAborted);
        if (host.ManagementAgentId is { } agentId)
        {
            await agents.ReconfigureManagementAgentAsync(agentId, http.RequestAborted);
        }
        await http.WriteAuditAsync("hosts.storage_folders.update", resourceType: "host", resourceId: id.ToString("D"), details: new { before, after = folders });
        return Results.Ok(new { folders });
    }

    private static async Task<IResult> SetStagingAsync(Guid id, StagingRequest req, HttpContext http, PortalDbContext db, AccessEvaluator access)
    {
        var uid = http.User.UserId();
        if (!await access.HasAsync(uid, Permissions.HostsManage, ResourceRef.Global, http.RequestAborted)
            && await http.GuardAsync(Permissions.BackupManage, ResourceRef.Global, "hosts.backup_staging.update") is { } denied)
        {
            return denied;
        }
        var host = await db.Hosts.FirstOrDefaultAsync(h => h.Id == id, http.RequestAborted);
        if (host is null)
        {
            return ApiSupport.NotFound();
        }
        var path = string.IsNullOrWhiteSpace(req.Path) ? null : HostPaths.Normalize(req.Path);
        if (path is not null && (HostPaths.ValidateLocation(path) is not null || path.Length <= 3))
        {
            return ApiSupport.BadRequest("invalid_staging_path", @"A local folder such as C:\HvPortal-staging (not a drive root, not a system folder).");
        }
        // A drive the host does not have would only fail at the first backup: refused now (when the drives are known).
        var volumes = (host.HostFactsJson is null ? null : System.Text.Json.JsonSerializer.Deserialize<HvPortal.Contracts.HyperV.HostFacts>(host.HostFactsJson))?.Volumes ?? [];
        if (path is not null && volumes.Count > 0
            && !volumes.Any(v => v.Name.TrimEnd('\\').Equals(path[..2], StringComparison.OrdinalIgnoreCase)))
        {
            return ApiSupport.BadRequest("drive_not_found",
                $"The host has no drive {path[..2]} (local drives: {string.Join(", ", volumes.Select(v => v.Name.TrimEnd('\\')))}).");
        }
        if (path is not null && InventoryEndpoints.StorageFoldersOf(host) is { Count: > 0 } allowed && !StorageFolderRules.IsInside(path, allowed))
        {
            return ApiSupport.BadRequest("location_not_allowed", "The folder is outside the storage folders allowed on this host.");
        }
        var before = host.BackupStagingPath;
        host.BackupStagingPath = path;
        await db.SaveChangesAsync(http.RequestAborted);
        await http.WriteAuditAsync("hosts.backup_staging.update", resourceType: "host", resourceId: id.ToString("D"), details: new { before, after = path });
        return Results.Ok(new { path });
    }
}
