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
    public sealed record CreateSwitchRequest(string? Name, string? Type, string? NetAdapterName, bool AllowManagementOs, string? Notes, Guid? GroupId, string? IdempotencyKey);
    public sealed record DeleteSwitchRequest(string? SwitchId, string? Name, string? ConfirmName, string? IdempotencyKey);

    private static readonly string[] RequiresOffFields = ["processorCount", "dynamicMemoryEnabled", "secureBoot", "automaticStopAction"];

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
        hosts.MapPost("/{id:guid}/switches", CreateSwitchAsync);
        hosts.MapPost("/{id:guid}/switches/delete", DeleteSwitchAsync);
        hosts.MapGet("/{id:guid}/net-adapters", NetAdaptersAsync);
        hosts.MapGet("/{id:guid}/disks", DisksAsync);
        hosts.MapGet("/{id:guid}/isos", IsosAsync);
        hosts.MapGet("/{id:guid}/folders", FoldersAsync);
        hosts.MapPut("/{id:guid}/iso-library", SetIsoLibraryAsync);
        hosts.MapPost("/{id:guid}/vms", CreateVmAsync);
    }

    // ------------------------------------------------------------------ helpers

    internal static async Task<(JsonElement? Result, IResult? Error)> QueryAsync(IAgentDispatcher agents, Guid hostId, HvOperation op, object args,
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

    internal static async Task<VirtualMachine?> ReadableVmAsync(Guid id, HttpContext http, PortalDbContext db, AccessEvaluator access) =>
        await db.VirtualMachines.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id, http.RequestAborted) is { } vm
        && await access.HasAsync(http.User.UserId(), Permissions.InventoryRead, ResourceRef.Vm(id), http.RequestAborted)
            ? vm
            : null;

    internal static bool IsFresh(ManagedHost host, IAgentDispatcher agents, TimeProvider time) =>
        agents.IsConnected(host.Id) && host.LastInventoryAt is { } at && time.GetUtcNow() - at <= InventoryEndpoints.StaleAfter;

    internal static string? IdempotencyKey(HttpContext http, string? fromBody)
    {
        var key = fromBody ?? http.Request.Headers["Idempotency-Key"].FirstOrDefault();
        return key is not null && !ApiSupport.IdempotencyKeyRegex().IsMatch(key) ? "!" : key;
    }

    internal static List<string> SwitchNames(ManagedHost host)
    {
        if (host.HostFactsJson is null)
        {
            return [];
        }
        var facts = JsonSerializer.Deserialize<HostFacts>(host.HostFactsJson);
        return facts?.VirtualSwitches.Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList() ?? [];
    }

    internal static async Task<IResult> QueueAsync(HttpContext http, JobService jobs, JobService.OperationJobSpec spec, string? idempotencyKey,
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
                annotate = await access.HasAsync(uid, Permissions.InventoryAnnotate, r, http.RequestAborted),
            },
            requiresOff = RequiresOffFields,
            switches = SwitchNames(host),
            // Networks of the portal on this host (switch + VLAN): choosing one sets both on the adapter (agent 0.7.0+ for a new adapter).
            networks = await db.PortalNetworks.AsNoTracking().Where(n => n.HostId == host.Id).OrderBy(n => n.Name)
                .Select(n => new { n.Id, n.Name, n.SwitchName, n.VlanId }).ToListAsync(http.RequestAborted),
            vlanOnAdd = agents.ProtocolVersionOf(host.Id) >= 9,
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
        JobService jobs, IAgentDispatcher agents, TimeProvider time, GroupPolicyService policies)
    {
        var vm = await ReadableVmAsync(id, http, db, access);
        if (vm is null)
        {
            return ApiSupport.NotFound();
        }
        var host = await db.Hosts.AsNoTracking().FirstAsync(h => h.Id == vm.HostId, http.RequestAborted);
        var roots = OperationPolicy.IsoRoots(host);
        // Server-controlled fields: the client cannot choose the target VM id or the ISO library.
        // Server-controlled fields: the client cannot choose the target VM id, the ISO library or the folder of a new disk.
        var change = req with
        {
            VmId = vm.HyperVId,
            IsoRoots = roots,
            NewName = string.IsNullOrWhiteSpace(req.NewName) || req.NewName == vm.Name ? null : req.NewName.Trim(),
            NewDisk = req.NewDisk is null ? null : req.NewDisk with { Folder = null },
        };
        // Networks of the portal: their switch and VLAN replace what the client sent for those adapters (ADR-030).
        var (networkError, withNetworks) = await Networks.NetworkRules.ResolveAsync(db, host.Id, change, http.RequestAborted);
        if (networkError is { } ne)
        {
            return ApiSupport.BadRequest(ne.Code, ne.Message);
        }
        change = withNetworks;
        if (change.AddNetworkAdapterVlanId is not null && agents.IsConnected(host.Id) && agents.ProtocolVersionOf(host.Id) < 9)
        {
            return ApiSupport.Error(StatusCodes.Status501NotImplemented, "agent_outdated", "Adding an adapter on a network with a VLAN needs agent 0.7.0 or later on this host. Update the agent.");
        }
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
        // Limits of the folders the VM lives in (networks, storage, quotas); the new disk folder is decided here.
        var (violation, limitedChange) = await policies.CheckConfigAsync(vm, change, http.RequestAborted);
        if (violation is not null)
        {
            await http.WriteAuditAsync("vm.config.update.request", AuditOutcome.Denied, "vm", id.ToString("D"), new { reason = violation.Code, violation.Message });
            return ApiSupport.Conflict(violation.Code, violation.Message);
        }
        change = limitedChange;
        var offOnly = OperationValidation.FieldsRequiringOff(change, vm.Generation);
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
            // Agent protocol 7+: a running VM is turned off by the agent before the checkpoint is applied.
            applyWhileRunning = agents.ProtocolVersionOf(vm.HostId) >= 7,
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
        if (vm.IsMissing)
        {
            return ApiSupport.Conflict("vm_missing", "The VM is not present in the latest inventory.");
        }
        // A running or paused VM is turned off by the agent before the checkpoint is applied (its current state is
        // discarded anyway). Agents before protocol 7 do not do that: there the VM must already be off or saved.
        if (IsFresh(host, agents, time) && vm.PowerState is not (VmPowerState.Off or VmPowerState.Saved) && agents.ProtocolVersionOf(vm.HostId) < 7)
        {
            return ApiSupport.Conflict("invalid_state", "Turn off (or save) the VM before applying a checkpoint, or update the agent on this host (0.5.0 or later).");
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

    private static async Task<IResult> HostDefaultsAsync(Guid id, Guid? groupId, HttpContext http, PortalDbContext db, AccessEvaluator access,
        IAgentDispatcher agents, GroupPolicyService policies)
    {
        var host = await HostAsync(id, http, db);
        if (host is null)
        {
            return ApiSupport.NotFound();
        }
        // On the host itself, or inside a folder whose limits open this host to its users (ADR-024).
        var viaGroup = groupId is { } g && await policies.HoldsViaGroupAsync(http.User.UserId(), Permissions.VmCreate, g, id, http.RequestAborted);
        if (!viaGroup && await http.GuardAsync(Permissions.VmCreate, ResourceRef.Host(id), "hosts.defaults") is { } denied)
        {
            return denied;
        }
        var folder = groupId is { } fg && await db.LogicalGroups.AnyAsync(x => x.Id == fg, http.RequestAborted)
            ? await policies.HostViewAsync(fg, id, SwitchNames(host), http.RequestAborted)
            : null;
        var (defaults, error) = await QueryAsync(agents, id, HvOperation.GetHostDefaults, new { }, null, http.RequestAborted);
        if (error is not null)
        {
            return error;
        }
        // Names already used on the host (the wizard warns early). A folder user learns only the names of VMs they can see.
        var hostVms = await db.VirtualMachines.AsNoTracking().Where(v => v.HostId == id && !v.IsMissing).Select(v => new { v.Id, v.Name }).ToListAsync(http.RequestAborted);
        var onHost = await access.HasAsync(http.User.UserId(), Permissions.VmCreate, ResourceRef.Host(id), http.RequestAborted);
        var seen = onHost ? null : await access.FilterVmsAsync(http.User.UserId(), Permissions.InventoryRead, hostVms.Select(v => v.Id), http.RequestAborted);
        var existingNames = hostVms.Where(v => seen is null || seen.Contains(v.Id)).Select(v => v.Name).ToList();
        var allowedSwitches = folder?.Switches ?? SwitchNames(host);
        // Storage folders of the host (agent 0.7.0+): the VM must go inside them, so the wizard proposes them; the
        // locations of a folder's limits count only where they are inside them (the agent refuses the others).
        var hostFolders = InventoryEndpoints.StorageFoldersOf(host) ?? [];
        var storageRoots = folder?.StorageRoots is { Count: > 0 } folderRoots
            ? folderRoots.Where(r => hostFolders.Count == 0 || StorageFolderRules.IsInside(r, hostFolders)).ToList()
            : hostFolders.ToList();
        return Results.Ok(new
        {
            defaults,
            // Networks of the portal (switch + VLAN) on the switches the user may use.
            networks = (await db.PortalNetworks.AsNoTracking().Where(n => n.HostId == id).OrderBy(n => n.Name).ToListAsync(http.RequestAborted))
                .Where(n => allowedSwitches.Contains(n.SwitchName, StringComparer.Ordinal)).Select(n => new { n.Id, n.Name, n.SwitchName, n.VlanId }),
            vlanSupported = agents.ProtocolVersionOf(id) >= 9,
            // In a folder: only the networks and the storage paths its limits allow (storageRoots empty = no restriction).
            switches = folder?.Switches ?? SwitchNames(host),
            networksRestricted = folder?.NetworksRestricted ?? false,
            storageRoots,
            isoLibrary = OperationPolicy.IsoRoots(host),
            existingNames,
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

    private static async Task<IResult> SwitchesAsync(Guid id, HttpContext http, PortalDbContext db, AccessEvaluator access, IAgentDispatcher agents,
        GroupPolicyService policies)
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
        var uid = http.User.UserId();
        var manage = await access.HasAsync(uid, Permissions.NetworkManage, ResourceRef.Host(id), http.RequestAborted);
        var owned = await db.GroupNetworks.AsNoTracking().Where(n => n.HostId == id).ToListAsync(http.RequestAborted);
        var groupNames = await db.LogicalGroups.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, http.RequestAborted);
        var list = new List<object>();
        foreach (var s in switches)
        {
            var owner = owned.FirstOrDefault(n => string.Equals(n.SwitchName, s.Name, StringComparison.Ordinal));
            list.Add(new
            {
                s.Id,
                s.Name,
                s.Type,
                s.NetAdapter,
                s.AllowManagementOs,
                s.EmbeddedTeaming,
                // VM names on a switch are filtered to the VMs the user can see.
                connectedVms = full ? s.ConnectedVms : s.ConnectedVms.Where(names.Contains).ToList(),
                // Private network created for a folder (it counts against the folder's network quota).
                ownerGroupId = owner?.GroupId,
                ownerGroupName = owner is null ? null : groupNames.GetValueOrDefault(owner.GroupId),
                canDelete = manage || (owner is not null && await policies.HoldsViaGroupAsync(uid, Permissions.NetworkManage, owner.GroupId, id, http.RequestAborted)),
            });
        }
        return Results.Ok(list);
    }

    private static async Task<IResult> NetAdaptersAsync(Guid id, HttpContext http, PortalDbContext db, IAgentDispatcher agents)
    {
        if (await HostAsync(id, http, db) is null)
        {
            return ApiSupport.NotFound();
        }
        // Physical adapters matter only for external switches, which only host-level network administrators create.
        if (await http.GuardAsync(Permissions.NetworkManage, ResourceRef.Host(id), "hosts.net_adapters") is { } denied)
        {
            return denied;
        }
        var (result, error) = await QueryAsync(agents, id, HvOperation.ListNetAdapters, new { }, null, http.RequestAborted);
        return error ?? Results.Ok(result);
    }

    /// <summary>
    /// New virtual switch (job). On the host (network.manage on the host): any type. For a folder (network.manage on the
    /// folder, host opened by the folder's limits): private networks only, within the folder's network quota; the
    /// network then belongs to the folder.
    /// </summary>
    private static async Task<IResult> CreateSwitchAsync(Guid id, CreateSwitchRequest req, HttpContext http, PortalDbContext db, JobService jobs,
        IAgentDispatcher agents, GroupPolicyService policies)
    {
        var host = await HostAsync(id, http, db);
        if (host is null)
        {
            return ApiSupport.NotFound();
        }
        var uid = http.User.UserId();
        var viaGroup = req.GroupId is { } g && await policies.HoldsViaGroupAsync(uid, Permissions.NetworkManage, g, id, http.RequestAborted);
        if (!viaGroup && await http.GuardAsync(Permissions.NetworkManage, ResourceRef.Host(id), "host.network.create.request") is { } denied)
        {
            return denied;
        }
        var spec = new NewSwitchRequest(req.Name?.Trim() ?? "", req.Type ?? "", string.IsNullOrWhiteSpace(req.NetAdapterName) ? null : req.NetAdapterName.Trim(),
            req.AllowManagementOs, string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim());
        if (OperationValidation.Validate(spec) is { } invalid)
        {
            return ApiSupport.BadRequest(invalid);
        }
        if (req.GroupId is { } groupId)
        {
            if (!await db.LogicalGroups.AnyAsync(x => x.Id == groupId, http.RequestAborted))
            {
                return ApiSupport.BadRequest("unknown_group");
            }
            if (spec.Type != SwitchTypes.Private)
            {
                return ApiSupport.BadRequest("folder_networks_are_private", "Networks of a folder are private (VM to VM). External and internal switches are created on the host by its administrators.");
            }
            if (await policies.CheckNewSwitchAsync(groupId, null, http.RequestAborted) is { } violation)
            {
                await http.WriteAuditAsync("host.network.create.request", AuditOutcome.Denied, "host", id.ToString("D"), new { reason = violation.Code, violation.Message, groupId });
                return ApiSupport.Conflict(violation.Code, violation.Message);
            }
        }
        if (SwitchNames(host).Contains(spec.Name, StringComparer.OrdinalIgnoreCase))
        {
            return ApiSupport.Conflict("switch_exists", "A virtual switch with this name already exists on the host.");
        }
        if (!agents.IsConnected(id))
        {
            return ApiSupport.Error(StatusCodes.Status503ServiceUnavailable, "host_unreachable", "The host agent is not connected.");
        }
        if (agents.ProtocolVersionOf(id) < 7)
        {
            return ApiSupport.Error(StatusCodes.Status501NotImplemented, "agent_outdated", "Creating networks needs agent 0.5.0 or later on this host. Update the agent.");
        }
        var key = IdempotencyKey(http, req.IdempotencyKey);
        if (key == "!")
        {
            return ApiSupport.BadRequest("invalid_idempotency_key");
        }
        return await QueueAsync(http, jobs, new JobService.OperationJobSpec(JobService.NetworkJobType, "Create", ScopeType.Host, id, spec.Name, id,
            Permissions.NetworkManage, new JobService.OperationParameters(HvOperations.ToElement(spec), req.GroupId), 300,
            $"Create {spec.Type.ToLowerInvariant()} switch {spec.Name} on {host.DisplayName}"), key, "host.network.create.request",
            new { spec.Name, spec.Type, spec.NetAdapterName, spec.AllowManagementOs, req.GroupId });
    }

    private static async Task<IResult> DeleteSwitchAsync(Guid id, DeleteSwitchRequest req, HttpContext http, PortalDbContext db, JobService jobs,
        IAgentDispatcher agents, GroupPolicyService policies)
    {
        var host = await HostAsync(id, http, db);
        if (host is null)
        {
            return ApiSupport.NotFound();
        }
        var target = new SwitchRef(req.SwitchId ?? "", req.Name);
        if ((string.IsNullOrEmpty(req.Name) ? "invalid_switch_name" : OperationValidation.Validate(target)) is { } invalid)
        {
            return ApiSupport.BadRequest(invalid);
        }
        var uid = http.User.UserId();
        // A folder user removes only the networks created for the folder; everything else needs the permission on the host.
        var owner = await db.GroupNetworks.AsNoTracking().FirstOrDefaultAsync(n => n.HostId == id && n.SwitchName == req.Name, http.RequestAborted);
        var viaGroup = owner is not null && await policies.HoldsViaGroupAsync(uid, Permissions.NetworkManage, owner.GroupId, id, http.RequestAborted);
        if (!viaGroup && await http.GuardAsync(Permissions.NetworkManage, ResourceRef.Host(id), "host.network.delete.request") is { } denied)
        {
            return denied;
        }
        if (!string.Equals(req.ConfirmName, req.Name, StringComparison.Ordinal))
        {
            return ApiSupport.BadRequest("confirmation_required", "Type the name of the switch to confirm.");
        }
        if (!agents.IsConnected(id))
        {
            return ApiSupport.Error(StatusCodes.Status503ServiceUnavailable, "host_unreachable", "The host agent is not connected.");
        }
        if (agents.ProtocolVersionOf(id) < 7)
        {
            return ApiSupport.Error(StatusCodes.Status501NotImplemented, "agent_outdated", "Removing networks needs agent 0.5.0 or later on this host. Update the agent.");
        }
        var key = IdempotencyKey(http, req.IdempotencyKey);
        if (key == "!")
        {
            return ApiSupport.BadRequest("invalid_idempotency_key");
        }
        return await QueueAsync(http, jobs, new JobService.OperationJobSpec(JobService.NetworkJobType, "Delete", ScopeType.Host, id, req.Name, id,
            Permissions.NetworkManage, new JobService.OperationParameters(HvOperations.ToElement(target), viaGroup ? owner!.GroupId : null), 300,
            $"Delete switch {req.Name} on {host.DisplayName}"), key, "host.network.delete.request", new { req.Name, req.SwitchId, ownerGroupId = owner?.GroupId });
    }

    private static async Task<IResult> DisksAsync(Guid id, HttpContext http, PortalDbContext db, AccessEvaluator access, IAgentDispatcher agents,
        GroupPolicyService policies)
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
        await policies.SaveDiskTotalsAsync(id, disks, http.RequestAborted);
        return Results.Ok(disks.Where(d => d.VmId is { } hv && portalIds.ContainsKey(hv))
            .Select(d => new { disk = d, portalVmId = portalIds[d.VmId!.Value] }));
    }

    private static async Task<IResult> IsosAsync(Guid id, Guid? groupId, HttpContext http, PortalDbContext db, AccessEvaluator access, IAgentDispatcher agents,
        GroupPolicyService policies)
    {
        var host = await HostAsync(id, http, db);
        if (host is null)
        {
            return ApiSupport.NotFound();
        }
        var uid = http.User.UserId();
        var allowed = await access.HasAsync(uid, Permissions.InventoryRead, ResourceRef.Host(id), http.RequestAborted)
                      || await access.HasAsync(uid, Permissions.VmCreate, ResourceRef.Host(id), http.RequestAborted);
        if (!allowed && groupId is { } g)
        {
            allowed = await policies.HoldsViaGroupAsync(uid, Permissions.VmCreate, g, id, http.RequestAborted);
        }
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

    /// <summary>
    /// Folder browser (agent 0.3.1+): local fixed drives and their sub-folders, system/hidden folders excluded by the agent.
    /// Folders only for the VM location (vm.create on the host); .iso files only for administrators (hosts.manage), who can
    /// add the folder to the host's ISO library. Read-only: nothing is created on the host.
    /// </summary>
    private static async Task<IResult> FoldersAsync(Guid id, string? path, bool? isos, HttpContext http, PortalDbContext db, AccessEvaluator access,
        IAgentDispatcher agents)
    {
        if (await HostAsync(id, http, db) is null)
        {
            return ApiSupport.NotFound();
        }
        var uid = http.User.UserId();
        var includeIsos = isos == true;
        var admin = await access.HasAsync(uid, Permissions.HostsManage, ResourceRef.Global, http.RequestAborted);
        if (includeIsos ? !admin : !admin && !await access.HasAsync(uid, Permissions.VmCreate, ResourceRef.Host(id), http.RequestAborted))
        {
            if (!await access.HasAsync(uid, Permissions.InventoryRead, ResourceRef.Host(id), http.RequestAborted))
            {
                return ApiSupport.NotFound();
            }
            await http.WriteAuditAsync("hosts.folders.browse", AuditOutcome.Denied, "host", id.ToString("D"), new { includeIsos });
            return ApiSupport.Forbidden("forbidden", $"Missing permission '{(includeIsos ? Permissions.HostsManage : Permissions.VmCreate)}' on host.");
        }
        var target = string.IsNullOrWhiteSpace(path) ? null : HostPaths.Normalize(path);
        var request = new FolderBrowseRequest(target, includeIsos);
        if (OperationValidation.Validate(request) is { } invalid)
        {
            return ApiSupport.BadRequest(invalid, @"Local folder such as D:\VMs (no UNC paths, no '..').");
        }
        if (target is not null && HostPaths.IsBlocked(target))
        {
            return ApiSupport.BadRequest("location_not_allowed", "System folders cannot be browsed.");
        }
        var (result, error) = await QueryAsync(agents, id, HvOperation.BrowseFolders, request, null, http.RequestAborted, timeoutSeconds: 30);
        return error ?? Results.Ok(result);
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
        JobService jobs, IAgentDispatcher agents, GroupPolicyService policies)
    {
        var host = await HostAsync(id, http, db);
        if (host is null)
        {
            return ApiSupport.NotFound();
        }
        var uid = http.User.UserId();
        // vm.create on the host, or on the target folder when the folder's limits open this host to its users (ADR-024).
        var viaGroup = req.GroupId is { } tg && await policies.HoldsViaGroupAsync(uid, Permissions.VmCreate, tg, id, http.RequestAborted);
        if (!viaGroup && await http.GuardAsync(Permissions.VmCreate, ResourceRef.Host(id), "vm.create.request") is { } denied)
        {
            return denied;
        }
        if (req.Vm is null)
        {
            return ApiSupport.BadRequest("invalid_request");
        }
        var roots = OperationPolicy.IsoRoots(host);
        var spec = req.Vm with
        {
            Name = req.Vm.Name?.Trim() ?? "",
            IsoRoots = roots,
            Location = string.IsNullOrWhiteSpace(req.Vm.Location) ? null : HostPaths.Normalize(req.Vm.Location),
        };
        // From a template (ADR-028): its disks, generation and sizes come from the template record of this host; a
        // request without a template never carries template fields. Done before the limits, which count those disks.
        VmTemplate? template = null;
        if (req.Vm.TemplateId is { } templateId)
        {
            template = await db.VmTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == templateId && t.HostId == id, http.RequestAborted);
            if (template is null)
            {
                return ApiSupport.BadRequest("unknown_template", "The template does not exist on this host.");
            }
            if (agents.IsConnected(id) && agents.ProtocolVersionOf(id) < TemplateEndpoints.TemplateProtocol)
            {
                return ApiSupport.Error(StatusCodes.Status501NotImplemented, "agent_outdated", "Templates need agent 0.6.0 or later on this host. Update the agent.");
            }
            if (template.Status != TemplateStatus.Ready || Templates.TemplateRules.Stamp(spec, template) is not { } fromTemplate)
            {
                return ApiSupport.Conflict("template_not_ready", "The template is not ready to be used (see its status in Templates).");
            }
            spec = fromTemplate;
        }
        else
        {
            spec = Templates.TemplateRules.WithoutTemplate(spec);
        }
        // The network of the wizard (switch + VLAN, ADR-030); checked by the folder limits below like any switch.
        var (networkError, withNetwork) = await Networks.NetworkRules.ResolveAsync(db, id, spec, http.RequestAborted);
        if (networkError is { } ne)
        {
            return ApiSupport.BadRequest(ne.Code, ne.Message);
        }
        spec = withNetwork;
        if (spec.VlanId is not null && agents.IsConnected(id) && agents.ProtocolVersionOf(id) < 9)
        {
            return ApiSupport.Error(StatusCodes.Status501NotImplemented, "agent_outdated", "Creating a VM on a network with a VLAN needs agent 0.7.0 or later on this host. Update the agent.");
        }
        var extra = new List<string>();
        if (req.GroupId is { } groupId)
        {
            if (await GroupEndpoints.CheckNewVmPlacementAsync(db, access, uid, groupId, id, http.RequestAborted) is { } placement)
            {
                await http.WriteAuditAsync("vm.create.request", AuditOutcome.Denied, "host", id.ToString("D"), new { reason = placement, groupId });
                return placement == "unknown_group" ? ApiSupport.BadRequest(placement) : ApiSupport.Forbidden(placement);
            }
            if (!await access.HasAsync(uid, Permissions.VmCreate, ResourceRef.Group(groupId), http.RequestAborted))
            {
                extra.Add(Permissions.GroupsManage);
            }
            // Limits of the folder: allowed network and storage (the location is set when the folder restricts it), quotas.
            var (violation, limited) = await policies.CheckNewVmAsync(groupId, id, spec, null, http.RequestAborted);
            if (violation is not null)
            {
                await http.WriteAuditAsync("vm.create.request", AuditOutcome.Denied, "host", id.ToString("D"), new { reason = violation.Code, violation.Message, groupId });
                return ApiSupport.Conflict(violation.Code, violation.Message);
            }
            spec = limited;
        }
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
        if (spec.Location is not null && agents.IsConnected(id) && agents.ProtocolVersionOf(id) < 3)
        {
            return ApiSupport.Error(StatusCodes.Status501NotImplemented, "agent_outdated",
                "Choosing the VM location needs agent 0.3.1 or later on this host. Update the agent or use the host default location.");
        }
        if (await db.VirtualMachines.AnyAsync(v => v.HostId == id && !v.IsMissing && v.Name == spec.Name, http.RequestAborted))
        {
            return ApiSupport.Conflict("vm_name_exists", "A VM with this name already exists on the host.");
        }
        if (!agents.IsConnected(id))
        {
            return ApiSupport.Error(StatusCodes.Status503ServiceUnavailable, "host_unreachable", "The host agent is not connected.");
        }
        var key = IdempotencyKey(http, req.IdempotencyKey);
        if (key == "!")
        {
            return ApiSupport.BadRequest("invalid_idempotency_key");
        }
        // From a template the disks are copied: the time depends on their size.
        var timeout = template is not null ? Templates.TemplateRules.CopyTimeoutSeconds(template.SizeBytes)
            : spec.DiskFixed ? Math.Min(1800, 300 + spec.DiskSizeGb * 2) : 600;
        var summary = new
        {
            spec.Name, template = template?.Name, spec.Generation, spec.ProcessorCount, spec.MemoryStartupMb, spec.DynamicMemory, spec.DiskSizeGb, spec.DiskFixed,
            spec.SwitchName, spec.VlanId, spec.NetworkId, spec.IsoPath, spec.SecureBootTemplate, spec.EnableTpm, spec.StartAfterCreate, spec.Location, req.GroupId,
        };
        return await QueueAsync(http, jobs, new JobService.OperationJobSpec(JobService.CreateJobType, template is null ? "New" : "FromTemplate", ScopeType.Host, id,
            spec.Name, id, Permissions.VmCreate, new JobService.OperationParameters(HvOperations.ToElement(spec), req.GroupId, extra), timeout,
            template is null ? $"Create VM {spec.Name} on {host.DisplayName}" : $"Create VM {spec.Name} from template {template.Name} on {host.DisplayName}"),
            key, "vm.create.request", summary);
    }
}
