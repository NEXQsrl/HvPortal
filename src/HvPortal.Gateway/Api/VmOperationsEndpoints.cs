using System.Text.Json;
using HvPortal.Contracts.Agent;
using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Agents;
using HvPortal.Gateway.Audit;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Jobs;
using HvPortal.Gateway.Security;
using Microsoft.EntityFrameworkCore;

namespace HvPortal.Gateway.Api;

/// <summary>
/// VM configuration, VM creation, checkpoints and host resources (switches, disks, ISO library).
/// Reads are live calls to the agent; changes are persistent jobs (re-authorized and re-validated at execution).
/// Every operation maps to a fixed, typed agent operation: there is no generic command path.
/// </summary>
public static class VmOperationsEndpoints
{
    public sealed record CheckpointCreateRequest(string? Name, string? IdempotencyKey);
    public sealed record CheckpointApplyRequest(string? ConfirmName, string? IdempotencyKey);
    public sealed record CheckpointDeleteRequest(bool IncludeChildren, string? IdempotencyKey);
    public sealed record IsoLibraryRequest(string[] Paths);
    public sealed record CreateVmRequest(NewVmRequest Vm, Guid? GroupId, string? IdempotencyKey);

    private static readonly string[] RequiresOffFields = ["processorCount", "dynamicMemoryEnabled", "secureBoot"];

    public static void Map(WebApplication app)
    {
        var vms = app.MapGroup("/api/v1/vms").WithTags("Virtual machines");
        vms.MapGet("/{id:guid}/config", GetConfigAsync);
        vms.MapMethods("/{id:guid}/config", ["PATCH"], UpdateConfigAsync);
        vms.MapGet("/{id:guid}/checkpoints", ListCheckpointsAsync);
        vms.MapPost("/{id:guid}/checkpoints", CreateCheckpointAsync);
        vms.MapPost("/{id:guid}/checkpoints/{checkpointId:guid}/apply", ApplyCheckpointAsync);
        vms.MapPost("/{id:guid}/checkpoints/{checkpointId:guid}/delete", DeleteCheckpointAsync);

        var hosts = app.MapGroup("/api/v1/hosts").WithTags("Hosts");
        hosts.MapGet("/{id:guid}/defaults", HostDefaultsAsync);
        hosts.MapGet("/{id:guid}/switches", SwitchesAsync);
        hosts.MapGet("/{id:guid}/disks", DisksAsync);
        hosts.MapGet("/{id:guid}/isos", IsosAsync);
        hosts.MapPut("/{id:guid}/iso-library", SetIsoLibraryAsync);
        hosts.MapPost("/{id:guid}/vms", CreateVmAsync);
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<(JsonElement? Result, IResult? Error)> QueryAsync(IAgentDispatcher agents, Guid hostId, HvOperation op, object args,
        Guid? hyperVId, CancellationToken ct, int timeoutSeconds = 60)
    {
        if (!agents.IsConnected(hostId))
        {
            return (null, ApiSupport.Error(StatusCodes.Status503ServiceUnavailable, "host_unreachable", "The host agent is not connected."));
        }
        try
        {
            var r = await agents.SendAsync(hostId, new AgentCommand
            {
                CommandId = Guid.NewGuid(),
                Kind = AgentCommandKind.HyperVOperation,
                Operation = op,
                OperationArgs = HvOperations.ToElement(args),
                VmId = hyperVId,
                TimeoutSeconds = timeoutSeconds,
            }, null, TimeSpan.FromSeconds(timeoutSeconds + 15), ct);
            if (r.Status == AgentCommandStatus.Rejected && r.Operation is null)
            {
                return (null, ApiSupport.Error(StatusCodes.Status501NotImplemented, "agent_outdated",
                    "The agent on this host does not support this operation. Update the agent."));
            }
            if (r.Operation is { Ok: true } ok)
            {
                return (ok.Result, null);
            }
            return (null, ApiSupport.Error(StatusCodes.Status502BadGateway, "host_operation_failed", r.Operation?.Error ?? r.Message ?? r.Status.ToString()));
        }
        catch (HostNotConnectedException)
        {
            return (null, ApiSupport.Error(StatusCodes.Status503ServiceUnavailable, "host_unreachable", "The host agent is not connected."));
        }
        catch (Exception ex) when (ex is TimeoutException or AgentChannelLostException)
        {
            return (null, ApiSupport.Error(StatusCodes.Status504GatewayTimeout, "host_timeout", ex.Message));
        }
    }

    private static async Task<VirtualMachine?> ReadableVmAsync(Guid id, HttpContext http, PortalDbContext db, AccessEvaluator access) =>
        await db.VirtualMachines.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id, http.RequestAborted) is { } vm
        && await access.HasAsync(http.User.UserId(), Permissions.InventoryRead, ResourceRef.Vm(id), http.RequestAborted)
            ? vm
            : null;

    private static bool IsFresh(ManagedHost host, IAgentDispatcher agents, TimeProvider time) =>
        agents.IsConnected(host.Id) && host.LastInventoryAt is { } at && time.GetUtcNow() - at <= InventoryEndpoints.StaleAfter;

    private static string? IdempotencyKey(HttpContext http, string? fromBody)
    {
        var key = fromBody ?? http.Request.Headers["Idempotency-Key"].FirstOrDefault();
        return key is not null && !ApiSupport.IdempotencyKeyRegex().IsMatch(key) ? "!" : key;
    }

    private static List<string> SwitchNames(ManagedHost host)
    {
        if (host.HostFactsJson is null)
        {
            return [];
        }
        var facts = JsonSerializer.Deserialize<HostFacts>(host.HostFactsJson);
        return facts?.VirtualSwitches.Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList() ?? [];
    }

    private static async Task<IResult> QueueAsync(HttpContext http, JobService jobs, JobService.OperationJobSpec spec, string? idempotencyKey,
        string auditAction, object auditDetails)
    {
        try
        {
            var (job, created) = await jobs.CreateOperationJobAsync(http.User.UserId(), spec, idempotencyKey, http.RequestAborted);
            if (created)
            {
                await http.WriteAuditAsync(auditAction, resourceType: spec.TargetType == ScopeType.Host ? "host" : "vm",
                    resourceId: spec.TargetId.ToString("D"), details: new { jobId = job.Id, request = auditDetails });
            }
            return Results.Accepted($"/api/v1/jobs/{job.Id}", new { jobId = job.Id, created });
        }
        catch (IdempotencyConflictException ex)
        {
            return ApiSupport.Conflict("idempotency_conflict", ex.Message);
        }
    }

    // ------------------------------------------------------------------ configuration

    private static async Task<IResult> GetConfigAsync(Guid id, HttpContext http, PortalDbContext db, AccessEvaluator access, IAgentDispatcher agents)
    {
        var vm = await ReadableVmAsync(id, http, db, access);
        if (vm is null)
        {
            return ApiSupport.NotFound();
        }
        var host = await db.Hosts.AsNoTracking().FirstAsync(h => h.Id == vm.HostId, http.RequestAborted);
        var (config, error) = await QueryAsync(agents, vm.HostId, HvOperation.GetVmConfig, new VmRef(vm.HyperVId), vm.HyperVId, http.RequestAborted);
        if (error is not null)
        {
            return error;
        }
        var uid = http.User.UserId();
        var r = ResourceRef.Vm(id);
        return Results.Ok(new
        {
            config,
            powerState = vm.PowerState.ToString(),
            can = new
            {
                compute = await access.HasAsync(uid, Permissions.VmConfigCompute, r, http.RequestAborted),
                storage = await access.HasAsync(uid, Permissions.VmConfigStorage, r, http.RequestAborted),
                network = await access.HasAsync(uid, Permissions.VmConfigNetwork, r, http.RequestAborted),
            },
            requiresOff = RequiresOffFields,
            switches = SwitchNames(host),
            isoLibrary = OperationPolicy.IsoRoots(host),
            options = new
            {
                startActions = OperationValidation.StartActions,
                stopActions = OperationValidation.StopActions,
                checkpointTypes = OperationValidation.CheckpointTypes,
                secureBootTemplates = OperationValidation.SecureBootTemplates,
            },
        });
    }

    private static async Task<IResult> UpdateConfigAsync(Guid id, VmConfigChange req, HttpContext http, PortalDbContext db, AccessEvaluator access,
        JobService jobs, IAgentDispatcher agents, TimeProvider time)
    {
        var vm = await ReadableVmAsync(id, http, db, access);
        if (vm is null)
        {
            return ApiSupport.NotFound();
        }
        var host = await db.Hosts.AsNoTracking().FirstAsync(h => h.Id == vm.HostId, http.RequestAborted);
        var roots = OperationPolicy.IsoRoots(host);
        // Server-controlled fields: the client cannot choose the target VM id or the ISO library.
        var change = req with { VmId = vm.HyperVId, IsoRoots = roots, NewName = string.IsNullOrWhiteSpace(req.NewName) || req.NewName == vm.Name ? null : req.NewName.Trim() };
        var permissions = OperationPolicy.PermissionsFor(change);
        if (permissions.Count == 0)
        {
            return ApiSupport.BadRequest("no_changes");
        }
        foreach (var p in permissions)
        {
            if (await http.GuardAsync(p, ResourceRef.Vm(id), "vm.config.update.request") is { } denied)
            {
                return denied;
            }
        }
        if (OperationValidation.Validate(change) is { } invalid)
        {
            return ApiSupport.BadRequest(invalid);
        }
        if (change.DvdIsoPath is { Length: > 0 } iso && !OperationPolicy.IsInsideLibrary(iso, roots))
        {
            return ApiSupport.BadRequest("iso_outside_library", "The ISO must be inside one of the host's ISO library folders (Host > Settings).");
        }
        if (change.AddNetworkAdapterSwitch is { Length: > 0 } sw && !SwitchNames(host).Contains(sw, StringComparer.Ordinal))
        {
            return ApiSupport.BadRequest("unknown_switch");
        }
        foreach (var n in change.NetworkAdapterChanges ?? [])
        {
            if (n.SwitchName is { Length: > 0 } s && !SwitchNames(host).Contains(s, StringComparer.Ordinal))
            {
                return ApiSupport.BadRequest("unknown_switch");
            }
        }
        if (vm.IsMissing)
        {
            return ApiSupport.Conflict("vm_missing", "The VM is not present in the latest inventory.");
        }
        var offOnly = OperationValidation.FieldsRequiringOff(change);
        if (offOnly.Count > 0 && IsFresh(host, agents, time) && vm.PowerState != VmPowerState.Off)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "requires_off",
                detail: "Turn off the VM to change: " + string.Join(", ", offOnly) + ".",
                extensions: new Dictionary<string, object?> { ["code"] = "requires_off", ["fields"] = offOnly });
        }
        var key = IdempotencyKey(http, null);
        if (key == "!")
        {
            return ApiSupport.BadRequest("invalid_idempotency_key");
        }
        var summary = new
        {
            change.NewName, notes = change.Notes is null ? null : "(changed)", change.ProcessorCount, change.ProcessorReserve, change.ProcessorLimit,
            change.ProcessorWeight, change.MemoryStartupMb, change.DynamicMemoryEnabled, change.MemoryMinimumMb, change.MemoryMaximumMb,
            change.MemoryBuffer, change.MemoryPriority, change.AutomaticStartAction, change.AutomaticStartDelaySeconds, change.AutomaticStopAction,
            change.CheckpointType, change.SecureBootEnabled, change.SecureBootTemplate, change.DvdIsoPath, change.NetworkAdapterChanges,
            change.AddNetworkAdapterSwitch, change.NewDisk, change.ExpandDisk,
        };
        var timeout = change.NewDisk is { Fixed: true } nd ? Math.Min(1800, 300 + nd.SizeGb * 2) : change.ExpandDisk is not null ? 900 : 300;
        return await QueueAsync(http, jobs, new JobService.OperationJobSpec(JobService.ConfigJobType, "Update", ScopeType.Vm, vm.Id, vm.Name, vm.HostId,
            permissions[0], new JobService.OperationParameters(HvOperations.ToElement(change), Permissions: permissions), timeout,
            $"Configuration change requested on {vm.Name}"), key, "vm.config.update.request", summary);
    }

    // ------------------------------------------------------------------ checkpoints

    private static async Task<IResult> ListCheckpointsAsync(Guid id, HttpContext http, PortalDbContext db, AccessEvaluator access, IAgentDispatcher agents)
    {
        var vm = await ReadableVmAsync(id, http, db, access);
        if (vm is null)
        {
            return ApiSupport.NotFound();
        }
        var (list, error) = await QueryAsync(agents, vm.HostId, HvOperation.ListCheckpoints, new CheckpointRequest(vm.HyperVId, null, null, false), vm.HyperVId, http.RequestAborted);
        if (error is not null)
        {
            return error;
        }
        var uid = http.User.UserId();
        return Results.Ok(new
        {
            checkpoints = list,
            powerState = vm.PowerState.ToString(),
            canCreate = await access.HasAsync(uid, Permissions.VmCheckpointCreate, ResourceRef.Vm(id), http.RequestAborted),
            canApply = await access.HasAsync(uid, Permissions.VmCheckpointApply, ResourceRef.Vm(id), http.RequestAborted),
            note = "Checkpoints are not backups: they depend on the VM's disks and live on the same storage.",
        });
    }

    private static async Task<IResult> CreateCheckpointAsync(Guid id, CheckpointCreateRequest req, HttpContext http, PortalDbContext db, AccessEvaluator access,
        JobService jobs, TimeProvider time)
    {
        var vm = await ReadableVmAsync(id, http, db, access);
        if (vm is null)
        {
            return ApiSupport.NotFound();
        }
        if (await http.GuardAsync(Permissions.VmCheckpointCreate, ResourceRef.Vm(id), "vm.checkpoint.create.request") is { } denied)
        {
            return denied;
        }
        var name = string.IsNullOrWhiteSpace(req.Name) ? $"{vm.Name} - {time.GetUtcNow():yyyy-MM-dd HH.mm.ss}" : req.Name.Trim();
        if (name.Length > 100)
        {
            name = name[..100].TrimEnd();
        }
        if (!OperationValidation.IsValidCheckpointName(name))
        {
            return ApiSupport.BadRequest("invalid_checkpoint_name");
        }
        if (vm.IsMissing)
        {
            return ApiSupport.Conflict("vm_missing");
        }
        var key = IdempotencyKey(http, req.IdempotencyKey);
        if (key == "!")
        {
            return ApiSupport.BadRequest("invalid_idempotency_key");
        }
        var args = new CheckpointRequest(vm.HyperVId, name, null, false);
        return await QueueAsync(http, jobs, new JobService.OperationJobSpec(JobService.CheckpointJobType, "Create", ScopeType.Vm, vm.Id, vm.Name, vm.HostId,
            Permissions.VmCheckpointCreate, new JobService.OperationParameters(HvOperations.ToElement(args)), 900,
            $"Checkpoint '{name}' requested on {vm.Name}"), key, "vm.checkpoint.create.request", new { name });
    }

    private static async Task<IResult> ApplyCheckpointAsync(Guid id, Guid checkpointId, CheckpointApplyRequest req, HttpContext http, PortalDbContext db,
        AccessEvaluator access, JobService jobs, IAgentDispatcher agents, TimeProvider time)
    {
        var vm = await ReadableVmAsync(id, http, db, access);
        if (vm is null)
        {
            return ApiSupport.NotFound();
        }
        if (await http.GuardAsync(Permissions.VmCheckpointApply, ResourceRef.Vm(id), "vm.checkpoint.apply.request") is { } denied)
        {
            return denied;
        }
        if (!string.Equals(req.ConfirmName, vm.Name, StringComparison.Ordinal))
        {
            return ApiSupport.BadRequest("confirmation_required", "Applying a checkpoint discards the current state of the VM. Type the VM name to confirm.");
        }
        var host = await db.Hosts.AsNoTracking().FirstAsync(h => h.Id == vm.HostId, http.RequestAborted);
        if (vm.IsMissing || (IsFresh(host, agents, time) && vm.PowerState is not (VmPowerState.Off or VmPowerState.Saved)))
        {
            return ApiSupport.Conflict("invalid_state", "Turn off (or save) the VM before applying a checkpoint.");
        }
        var key = IdempotencyKey(http, req.IdempotencyKey);
        if (key == "!")
        {
            return ApiSupport.BadRequest("invalid_idempotency_key");
        }
        var args = new CheckpointRequest(vm.HyperVId, null, checkpointId.ToString("D"), false);
        return await QueueAsync(http, jobs, new JobService.OperationJobSpec(JobService.CheckpointJobType, "Apply", ScopeType.Vm, vm.Id, vm.Name, vm.HostId,
            Permissions.VmCheckpointApply, new JobService.OperationParameters(HvOperations.ToElement(args)), 900,
            $"Apply checkpoint requested on {vm.Name}"), key, "vm.checkpoint.apply.request", new { checkpointId });
    }

    private static async Task<IResult> DeleteCheckpointAsync(Guid id, Guid checkpointId, CheckpointDeleteRequest req, HttpContext http, PortalDbContext db,
        AccessEvaluator access, JobService jobs)
    {
        var vm = await ReadableVmAsync(id, http, db, access);
        if (vm is null)
        {
            return ApiSupport.NotFound();
        }
        if (await http.GuardAsync(Permissions.VmCheckpointApply, ResourceRef.Vm(id), "vm.checkpoint.delete.request") is { } denied)
        {
            return denied;
        }
        if (vm.IsMissing)
        {
            return ApiSupport.Conflict("vm_missing");
        }
        var key = IdempotencyKey(http, req.IdempotencyKey);
        if (key == "!")
        {
            return ApiSupport.BadRequest("invalid_idempotency_key");
        }
        var args = new CheckpointRequest(vm.HyperVId, null, checkpointId.ToString("D"), req.IncludeChildren);
        // Deleting merges the differencing disks into the parent: it can take a long time for large checkpoints.
        return await QueueAsync(http, jobs, new JobService.OperationJobSpec(JobService.CheckpointJobType, "Delete", ScopeType.Vm, vm.Id, vm.Name, vm.HostId,
            Permissions.VmCheckpointApply, new JobService.OperationParameters(HvOperations.ToElement(args)), 1800,
            $"Delete checkpoint requested on {vm.Name}"), key, "vm.checkpoint.delete.request", new { checkpointId, req.IncludeChildren });
    }

    // ------------------------------------------------------------------ host resources

    private static async Task<ManagedHost?> HostAsync(Guid id, HttpContext http, PortalDbContext db) =>
        await db.Hosts.AsNoTracking().FirstOrDefaultAsync(h => h.Id == id && h.Status == HostStatus.Active, http.RequestAborted);

    private static async Task<IResult> HostDefaultsAsync(Guid id, HttpContext http, PortalDbContext db, IAgentDispatcher agents)
    {
        var host = await HostAsync(id, http, db);
        if (host is null)
        {
            return ApiSupport.NotFound();
        }
        if (await http.GuardAsync(Permissions.VmCreate, ResourceRef.Host(id), "hosts.defaults") is { } denied)
        {
            return denied;
        }
        var (defaults, error) = await QueryAsync(agents, id, HvOperation.GetHostDefaults, new { }, null, http.RequestAborted);
        if (error is not null)
        {
            return error;
        }
        return Results.Ok(new
        {
            defaults,
            switches = SwitchNames(host),
            isoLibrary = OperationPolicy.IsoRoots(host),
            existingNames = await db.VirtualMachines.AsNoTracking().Where(v => v.HostId == id && !v.IsMissing).Select(v => v.Name).ToListAsync(http.RequestAborted),
            options = new
            {
                startActions = OperationValidation.StartActions,
                checkpointTypes = OperationValidation.CheckpointTypes,
                secureBootTemplates = OperationValidation.SecureBootTemplates,
            },
        });
    }

    private static async Task<(bool Full, HashSet<string> VisibleNames, Dictionary<Guid, Guid> PortalIds, IResult? Error)> HostVisibilityAsync(
        Guid id, HttpContext http, PortalDbContext db, AccessEvaluator access)
    {
        var uid = http.User.UserId();
        var vms = await db.VirtualMachines.AsNoTracking().Where(v => v.HostId == id).Select(v => new { v.Id, v.HyperVId, v.Name }).ToListAsync(http.RequestAborted);
        var full = await access.HasAsync(uid, Permissions.InventoryRead, ResourceRef.Host(id), http.RequestAborted);
        var visible = full ? vms.Select(v => v.Id).ToHashSet() : await access.FilterVmsAsync(uid, Permissions.InventoryRead, vms.Select(v => v.Id), http.RequestAborted);
        if (!full && visible.Count == 0)
        {
            return (false, [], [], ApiSupport.NotFound());
        }
        return (full,
            vms.Where(v => visible.Contains(v.Id)).Select(v => v.Name).ToHashSet(StringComparer.Ordinal),
            vms.Where(v => visible.Contains(v.Id)).ToDictionary(v => v.HyperVId, v => v.Id),
            null);
    }

    private static async Task<IResult> SwitchesAsync(Guid id, HttpContext http, PortalDbContext db, AccessEvaluator access, IAgentDispatcher agents)
    {
        if (await HostAsync(id, http, db) is null)
        {
            return ApiSupport.NotFound();
        }
        var (full, names, _, denied) = await HostVisibilityAsync(id, http, db, access);
        if (denied is not null)
        {
            return denied;
        }
        var (result, error) = await QueryAsync(agents, id, HvOperation.ListSwitches, new { }, null, http.RequestAborted);
        if (error is not null)
        {
            return error;
        }
        var switches = JsonSerializer.Deserialize<List<SwitchInfo>>(result!.Value.GetRawText(), AgentProtocol.Json) ?? [];
        // VM names on a switch are filtered to the VMs the user can see.
        return Results.Ok(switches.Select(s => s with { ConnectedVms = full ? s.ConnectedVms : s.ConnectedVms.Where(names.Contains).ToList() }));
    }

    private static async Task<IResult> DisksAsync(Guid id, HttpContext http, PortalDbContext db, AccessEvaluator access, IAgentDispatcher agents)
    {
        if (await HostAsync(id, http, db) is null)
        {
            return ApiSupport.NotFound();
        }
        var (_, _, portalIds, denied) = await HostVisibilityAsync(id, http, db, access);
        if (denied is not null)
        {
            return denied;
        }
        var (result, error) = await QueryAsync(agents, id, HvOperation.ListHostDisks, new { }, null, http.RequestAborted, timeoutSeconds: 120);
        if (error is not null)
        {
            return error;
        }
        var disks = JsonSerializer.Deserialize<List<HostDiskInfo>>(result!.Value.GetRawText(), AgentProtocol.Json) ?? [];
        return Results.Ok(disks.Where(d => d.VmId is { } hv && portalIds.ContainsKey(hv))
            .Select(d => new { disk = d, portalVmId = portalIds[d.VmId!.Value] }));
    }

    private static async Task<IResult> IsosAsync(Guid id, HttpContext http, PortalDbContext db, AccessEvaluator access, IAgentDispatcher agents)
    {
        var host = await HostAsync(id, http, db);
        if (host is null)
        {
            return ApiSupport.NotFound();
        }
        var uid = http.User.UserId();
        var allowed = await access.HasAsync(uid, Permissions.InventoryRead, ResourceRef.Host(id), http.RequestAborted)
                      || await access.HasAsync(uid, Permissions.VmCreate, ResourceRef.Host(id), http.RequestAborted);
        if (!allowed)
        {
            var vmIds = await db.VirtualMachines.AsNoTracking().Where(v => v.HostId == id).Select(v => v.Id).ToListAsync(http.RequestAborted);
            allowed = (await access.FilterVmsAsync(uid, Permissions.VmConfigStorage, vmIds, http.RequestAborted)).Count > 0;
        }
        if (!allowed)
        {
            return ApiSupport.NotFound();
        }
        var roots = OperationPolicy.IsoRoots(host);
        if (roots.Count == 0)
        {
            return Results.Ok(new { roots, files = Array.Empty<object>() });
        }
        var (files, error) = await QueryAsync(agents, id, HvOperation.ListIsos, new IsoListRequest(roots), null, http.RequestAborted, timeoutSeconds: 90);
        return error ?? Results.Ok(new { roots, files });
    }

    private static async Task<IResult> SetIsoLibraryAsync(Guid id, IsoLibraryRequest req, HttpContext http, PortalDbContext db)
    {
        if (await http.GuardAsync(Permissions.HostsManage, ResourceRef.Global, "hosts.iso_library.update") is { } denied)
        {
            return denied;
        }
        var host = await db.Hosts.FirstOrDefaultAsync(h => h.Id == id, http.RequestAborted);
        if (host is null)
        {
            return ApiSupport.NotFound();
        }
        var paths = (req.Paths ?? []).Select(p => p?.Trim() ?? "").Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (paths.Count > OperationPolicy.MaxIsoRoots || paths.Any(p => !OperationPolicy.IsValidLibraryPath(p)))
        {
            return ApiSupport.BadRequest("invalid_iso_library", $"Up to {OperationPolicy.MaxIsoRoots} local folders such as D:\\ISO (no UNC paths, no '..').");
        }
        var before = OperationPolicy.IsoRoots(host);
        host.IsoLibraryPathsJson = JsonSerializer.Serialize(paths);
        await db.SaveChangesAsync(http.RequestAborted);
        await http.WriteAuditAsync("hosts.iso_library.update", resourceType: "host", resourceId: id.ToString("D"), details: new { before, after = paths });
        return Results.Ok(new { paths });
    }

    // ------------------------------------------------------------------ VM creation

    private static async Task<IResult> CreateVmAsync(Guid id, CreateVmRequest req, HttpContext http, PortalDbContext db, AccessEvaluator access,
        JobService jobs, IAgentDispatcher agents)
    {
        var host = await HostAsync(id, http, db);
        if (host is null)
        {
            return ApiSupport.NotFound();
        }
        if (await http.GuardAsync(Permissions.VmCreate, ResourceRef.Host(id), "vm.create.request") is { } denied)
        {
            return denied;
        }
        if (req.Vm is null)
        {
            return ApiSupport.BadRequest("invalid_request");
        }
        var roots = OperationPolicy.IsoRoots(host);
        var spec = req.Vm with { Name = req.Vm.Name?.Trim() ?? "", IsoRoots = roots };
        if (OperationValidation.Validate(spec) is { } invalid)
        {
            return ApiSupport.BadRequest(invalid);
        }
        if (spec.IsoPath is { Length: > 0 } iso && !OperationPolicy.IsInsideLibrary(iso, roots))
        {
            return ApiSupport.BadRequest("iso_outside_library", "The ISO must be inside one of the host's ISO library folders (Host > Settings).");
        }
        if (spec.SwitchName is { Length: > 0 } sw && !SwitchNames(host).Contains(sw, StringComparer.Ordinal))
        {
            return ApiSupport.BadRequest("unknown_switch");
        }
        if (await db.VirtualMachines.AnyAsync(v => v.HostId == id && !v.IsMissing && v.Name == spec.Name, http.RequestAborted))
        {
            return ApiSupport.Conflict("vm_name_exists", "A VM with this name already exists on the host.");
        }
        if (!agents.IsConnected(id))
        {
            return ApiSupport.Error(StatusCodes.Status503ServiceUnavailable, "host_unreachable", "The host agent is not connected.");
        }
        var extra = new List<string>();
        if (req.GroupId is { } groupId)
        {
            if (await GroupEndpoints.CheckNewVmPlacementAsync(db, access, http.User.UserId(), groupId, id, http.RequestAborted) is { } placement)
            {
                await http.WriteAuditAsync("vm.create.request", AuditOutcome.Denied, "host", id.ToString("D"), new { reason = placement, groupId });
                return placement == "unknown_group" ? ApiSupport.BadRequest(placement) : ApiSupport.Forbidden(placement);
            }
            extra.Add(Permissions.GroupsManage);
        }
        var key = IdempotencyKey(http, req.IdempotencyKey);
        if (key == "!")
        {
            return ApiSupport.BadRequest("invalid_idempotency_key");
        }
        var timeout = spec.DiskFixed ? Math.Min(1800, 300 + spec.DiskSizeGb * 2) : 600;
        var summary = new
        {
            spec.Name, spec.Generation, spec.ProcessorCount, spec.MemoryStartupMb, spec.DynamicMemory, spec.DiskSizeGb, spec.DiskFixed,
            spec.SwitchName, spec.IsoPath, spec.SecureBootTemplate, spec.EnableTpm, spec.StartAfterCreate, req.GroupId,
        };
        return await QueueAsync(http, jobs, new JobService.OperationJobSpec(JobService.CreateJobType, "New", ScopeType.Host, id, spec.Name, id,
            Permissions.VmCreate, new JobService.OperationParameters(HvOperations.ToElement(spec), req.GroupId, extra), timeout,
            $"Create VM {spec.Name} on {host.DisplayName}"), key, "vm.create.request", summary);
    }
}
