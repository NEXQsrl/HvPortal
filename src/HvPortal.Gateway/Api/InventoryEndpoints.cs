using System.Text.Json;
using HvPortal.Contracts.Agent;
using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Agents;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Configuration;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Jobs;
using HvPortal.Gateway.Pki;
using HvPortal.Gateway.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HvPortal.Gateway.Api;

public static class InventoryEndpoints
{
    /// <summary>Inventory older than this (or a disconnected agent) is flagged as stale in API responses.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);

    public sealed record CreateHostRequest(string DisplayName);
    public sealed record PowerRequest(VmPowerOperation Operation, string? ConfirmName, string? IdempotencyKey);

    public static void Map(WebApplication app)
    {
        var hosts = app.MapGroup("/api/v1/hosts").WithTags("Hosts");
        hosts.MapGet("/", ListHostsAsync);
        hosts.MapGet("/{id:guid}", GetHostAsync);
        hosts.MapPost("/", CreateHostAsync);
        hosts.MapPost("/{id:guid}/enrollment-token", NewTokenAsync);
        hosts.MapPost("/{id:guid}/revoke", RevokeHostAsync);
        hosts.MapPost("/{id:guid}/refresh", RefreshHostAsync);
        hosts.MapGet("/connection-info", async (HttpContext http, InternalCertificateAuthority ca, IOptions<GatewayOptions> options) =>
            await http.GuardAsync(Permissions.HostsManage, ResourceRef.Global, "hosts.connection_info") ?? Results.Ok(GatewayConnectionInfo(http, ca, options.Value)));

        var vms = app.MapGroup("/api/v1/vms").WithTags("Virtual machines");
        vms.MapGet("/", ListVmsAsync);
        vms.MapGet("/{id:guid}", GetVmAsync);
        vms.MapGet("/{id:guid}/events", VmEventsAsync);
        vms.MapPost("/{id:guid}/power", PowerAsync);
    }

    // ------------------------------------------------------------------ hosts

    private static object HostDto(ManagedHost h, bool connected, int vmCount, DateTimeOffset now, bool canCreateVm = false,
        bool showRemote = false, string? managementAgentName = null, bool canManageNetworks = false)
    {
        var facts = h.HostFactsJson is null ? null : JsonSerializer.Deserialize<HostFacts>(h.HostFactsJson);
        var eval = HostCapabilities.Evaluate(facts, h.Access);
        return new
        {
            h.Id,
            h.DisplayName,
            h.Description,
            h.MachineName,
            h.DnsHostName,
            status = h.Status.ToString(),
            connected,
            stale = !connected || h.LastInventoryAt is null || now - h.LastInventoryAt > StaleAfter,
            h.LastSeenAt,
            h.LastInventoryAt,
            access = h.Access?.ToString(),
            h.AccessMessage,
            partial = h.InventoryPartial,
            warnings = h.InventoryWarningsJson is null ? [] : JsonSerializer.Deserialize<string[]>(h.InventoryWarningsJson),
            h.AgentVersion,
            h.EnrolledAt,
            vmCount,
            os = facts is null ? null : new { facts.OsCaption, facts.OsVersion, facts.OsBuildNumber, facts.Domain, facts.PartOfDomain },
            hardware = facts is null ? null : new { facts.LogicalProcessorCount, facts.TotalPhysicalMemoryBytes, facts.FreePhysicalMemoryBytes, facts.CpuName, facts.CpuSockets, facts.CpuCores, facts.CpuLoadPercent, facts.CpuLoadSource },
            lastBootUtc = facts?.LastBootUtc,
            volumes = facts?.Volumes ?? [],
            networkAdapters = facts?.NetworkAdapters ?? [],
            virtualSwitches = facts?.VirtualSwitches ?? [],
            edition = eval.Edition,
            platform = eval.Platform,
            support = eval.Support,
            capabilities = eval.Capabilities,
            canCreateVm = canCreateVm && connected && h.Status == HostStatus.Active && h.Access == HyperVAccessStatus.Ok,
            // Agent 0.5.0+ (protocol 7): create and remove virtual switches.
            canManageNetworks = canManageNetworks && connected && h.Status == HostStatus.Active && h.Access == HyperVAccessStatus.Ok
                                && HostCapabilities.AgentAtLeast(h.AgentVersion, 0, 5),
            isoLibrary = Jobs.OperationPolicy.IsoRoots(h),
            // Agent 0.6.0+ (protocol 8): VM templates, kept in this folder of the host (null = not chosen yet).
            templateLibraryPath = h.TemplateLibraryPath,
            templates = connected && h.Access == HyperVAccessStatus.Ok && HostCapabilities.AgentAtLeast(h.AgentVersion, 0, 6),
            // Agent 0.3.1+ (protocol 3): folder browser for the VM location and ISO files.
            canBrowseFolders = connected && h.Access == HyperVAccessStatus.Ok && HostCapabilities.AgentAtLeast(h.AgentVersion, 0, 3, 1),
            // Agent 0.3.2+ (protocol 4): portal groups mirrored as native Hyper-V VM groups.
            nativeVmGroups = h.Access == HyperVAccessStatus.Ok && HostCapabilities.AgentAtLeast(h.AgentVersion, 0, 3, 2),
            // Agent 0.3.3+ (protocol 5): file explorer (browse, download, upload).
            canBrowseFiles = connected && HostCapabilities.AgentAtLeast(h.AgentVersion, 0, 3, 3),
            vmGroupsSyncedAt = h.VmGroupsSyncedAt,
            vmGroupsSyncError = h.VmGroupsSyncError,
            connectionMode = h.ConnectionMode.ToString(),
            managementAgentId = h.ManagementAgentId,
            managementAgentName,
            // Connection details only for host administrators; the password is never returned.
            remote = h.ConnectionMode == HostConnectionMode.Remote && showRemote
                ? new { computerName = h.RemoteComputerName, userName = h.RemoteUserName, useHttps = h.RemoteUseHttps, credentialUpdatedAt = h.RemoteCredentialUpdatedAt }
                : null,
        };
    }

    private static async Task<Dictionary<Guid, string>> ManagementAgentNamesAsync(PortalDbContext db, CancellationToken ct) =>
        await db.ManagementAgents.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.DisplayName, ct);

    private static async Task<IResult> ListHostsAsync(HttpContext http, PortalDbContext db, AccessEvaluator access, IAgentDispatcher agents, TimeProvider time)
    {
        var uid = http.User.UserId();
        var manageAll = await access.HasAsync(uid, Permissions.HostsManage, ResourceRef.Global, http.RequestAborted);
        var hosts = await db.Hosts.AsNoTracking().OrderBy(h => h.DisplayName).ToListAsync(http.RequestAborted);
        var counts = await db.VirtualMachines.AsNoTracking().Where(v => !v.IsMissing).GroupBy(v => v.HostId)
            .Select(g => new { g.Key, n = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.n, http.RequestAborted);
        var now = time.GetUtcNow();
        var agentNames = await ManagementAgentNamesAsync(db, http.RequestAborted);
        var result = new List<object>();
        foreach (var h in hosts)
        {
            if (manageAll || await access.CanSeeHostAsync(uid, h.Id, http.RequestAborted))
            {
                result.Add(HostDto(h, agents.IsConnected(h.Id), counts.GetValueOrDefault(h.Id), now,
                    await access.HasAsync(uid, Permissions.VmCreate, ResourceRef.Host(h.Id), http.RequestAborted),
                    manageAll, h.ManagementAgentId is { } ma ? agentNames.GetValueOrDefault(ma) : null,
                    await access.HasAsync(uid, Permissions.NetworkManage, ResourceRef.Host(h.Id), http.RequestAborted)));
            }
        }
        return Results.Ok(result);
    }

    private static async Task<IResult> GetHostAsync(Guid id, HttpContext http, PortalDbContext db, AccessEvaluator access, IAgentDispatcher agents, TimeProvider time)
    {
        var uid = http.User.UserId();
        var h = await db.Hosts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, http.RequestAborted);
        var manageAll = await access.HasAsync(uid, Permissions.HostsManage, ResourceRef.Global, http.RequestAborted);
        if (h is null || !(manageAll || await access.CanSeeHostAsync(uid, id, http.RequestAborted)))
        {
            return ApiSupport.NotFound();
        }
        var agentName = h.ManagementAgentId is { } ma
            ? await db.ManagementAgents.AsNoTracking().Where(a => a.Id == ma).Select(a => a.DisplayName).FirstOrDefaultAsync(http.RequestAborted)
            : null;
        var count = await db.VirtualMachines.CountAsync(v => v.HostId == id && !v.IsMissing, http.RequestAborted);
        var events = await db.InventoryEvents.AsNoTracking().Where(e => e.HostId == id).OrderByDescending(e => e.Id).Take(50).ToListAsync(http.RequestAborted);
        return Results.Ok(new { host = HostDto(h, agents.IsConnected(id), count, time.GetUtcNow(),
            await access.HasAsync(uid, Permissions.VmCreate, ResourceRef.Host(id), http.RequestAborted), manageAll, agentName,
            await access.HasAsync(uid, Permissions.NetworkManage, ResourceRef.Host(id), http.RequestAborted)), events });
    }

    /// <summary>Connection facts an agent needs: names valid in the gateway certificate, port, CA pin, reachability.</summary>
    public sealed record GatewayConnection(List<string> GatewayNames, string PreferredName, int AgentPort, string GatewayAgentUrl,
        string CaFingerprintSha256, bool ListensOnNetwork, string BindAddress);

    internal static GatewayConnection GatewayConnectionInfo(HttpContext http, InternalCertificateAuthority ca, GatewayOptions options)
    {
        var names = ca.ServerCertificateNames().Where(n => !IsLoopback(n)).ToList();
        // Prefer: first configured public name, then the name used in the browser (if valid in the certificate), then the machine DNS name.
        var requestHost = http.Request.Host.Host;
        var preferred = options.PublicNames.FirstOrDefault(n => !IsLoopback(n))
                        ?? (names.Contains(requestHost, StringComparer.OrdinalIgnoreCase) && !IsLoopback(requestHost) ? requestHost : null)
                        ?? names.FirstOrDefault(n => n.Contains('.', StringComparison.Ordinal) && !System.Net.IPAddress.TryParse(n, out _))
                        ?? names.FirstOrDefault()
                        ?? MachineDnsName();
        var bind = System.Net.IPAddress.TryParse(options.BindAddress, out var ip) ? ip : System.Net.IPAddress.Any;
        // ListensOnNetwork == false: the gateway listens on loopback only and no remote host can connect.
        return new GatewayConnection(names, preferred, options.AgentPort, $"https://{preferred}:{options.AgentPort}",
            ca.CaFingerprintSha256, !System.Net.IPAddress.IsLoopback(bind), options.BindAddress);
    }

    private static bool IsLoopback(string name) =>
        name is "localhost" or "::1" or "[::1]" || (System.Net.IPAddress.TryParse(name, out var ip) && System.Net.IPAddress.IsLoopback(ip));

    internal static object EnrollmentInfo(HttpContext http, InternalCertificateAuthority ca, GatewayOptions options, string token, DateTimeOffset expires,
        bool management = false)
    {
        var c = GatewayConnectionInfo(http, ca, options);
        var url = c.GatewayAgentUrl;
        return new
        {
            token,
            expiresAt = expires,
            caFingerprintSha256 = ca.CaFingerprintSha256,
            gatewayAgentUrl = url,
            gatewayNames = c.GatewayNames,
            agentPort = options.AgentPort,
            listensOnNetwork = c.ListensOnNetwork,
            bindAddress = c.BindAddress,
            command = $"$env:HVP_ENROLLMENT_TOKEN = '<token>'; .\\Install-Agent.ps1 -Source ..\\agent -Gateway {url} -CaSha256 {ca.CaFingerprintSha256}"
                      + (management ? " -Management" : ""),
            management,
            note = "The token is shown only once and expires quickly. Verify the CA fingerprint out of band.",
        };
    }
    private static string MachineDnsName()
    {
        try
        {
            return System.Net.Dns.GetHostEntry(Environment.MachineName).HostName;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return Environment.MachineName;
        }
    }

    private static async Task<IResult> CreateHostAsync(CreateHostRequest req, HttpContext http, PortalDbContext db, EnrollmentService enrollment,
        InternalCertificateAuthority ca, IOptions<GatewayOptions> options, TimeProvider time)
    {
        if ((await http.GuardAsync(Permissions.HostsManage, ResourceRef.Global, "hosts.create") ?? await http.RequireRecentReauthAsync()) is { } denied)
        {
            return denied;
        }
        if (req.DisplayName is null || !ApiSupport.DisplayNameRegex().IsMatch(req.DisplayName))
        {
            return ApiSupport.BadRequest("invalid_display_name");
        }
        if (await db.Hosts.AnyAsync(h => h.DisplayName == req.DisplayName, http.RequestAborted))
        {
            return ApiSupport.Conflict("host_exists");
        }
        var host = new ManagedHost { DisplayName = req.DisplayName, CreatedAt = time.GetUtcNow() };
        db.Hosts.Add(host);
        await db.SaveChangesAsync(http.RequestAborted);
        var (token, expires) = await enrollment.CreateTokenAsync(host.Id, http.User.UserId(), http.RequestAborted);
        await http.WriteAuditAsync("hosts.create", resourceType: "host", resourceId: host.Id.ToString("D"), details: new { host.DisplayName, tokenExpires = expires });
        return Results.Ok(new { hostId = host.Id, enrollment = EnrollmentInfo(http, ca, options.Value, token, expires) });
    }

    private static async Task<IResult> NewTokenAsync(Guid id, HttpContext http, PortalDbContext db, EnrollmentService enrollment,
        InternalCertificateAuthority ca, IOptions<GatewayOptions> options)
    {
        if ((await http.GuardAsync(Permissions.HostsManage, ResourceRef.Global, "hosts.enrollment_token") ?? await http.RequireRecentReauthAsync()) is { } denied)
        {
            return denied;
        }
        var mode = await db.Hosts.Where(h => h.Id == id).Select(h => (HostConnectionMode?)h.ConnectionMode).FirstOrDefaultAsync(http.RequestAborted);
        if (mode is null)
        {
            return ApiSupport.NotFound();
        }
        if (mode == HostConnectionMode.Remote)
        {
            return ApiSupport.BadRequest("remote_host_not_enrolled", "This host is reached through a management agent: it has no agent to enroll.");
        }
        var (token, expires) = await enrollment.CreateTokenAsync(id, http.User.UserId(), http.RequestAborted);
        await http.WriteAuditAsync("hosts.enrollment_token", resourceType: "host", resourceId: id.ToString("D"), details: new { expires });
        return Results.Ok(EnrollmentInfo(http, ca, options.Value, token, expires));
    }

    private static async Task<IResult> RevokeHostAsync(Guid id, HttpContext http, PortalDbContext db, IAgentDispatcher agents, TimeProvider time)
    {
        if ((await http.GuardAsync(Permissions.HostsManage, ResourceRef.Global, "hosts.revoke") ?? await http.RequireRecentReauthAsync()) is { } denied)
        {
            return denied;
        }
        var host = await db.Hosts.FirstOrDefaultAsync(h => h.Id == id, http.RequestAborted);
        if (host is null)
        {
            return ApiSupport.NotFound();
        }
        var now = time.GetUtcNow();
        host.Status = HostStatus.Revoked;
        await db.SaveChangesAsync(http.RequestAborted);
        var certs = await db.AgentCertificates.Where(c => c.HostId == id && c.RevokedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.RevokedAt, now).SetProperty(c => c.RevokedReason, "host revoked"), http.RequestAborted);
        await db.EnrollmentTokens.Where(t => t.HostId == id && t.ConsumedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(t => t.ExpiresAt, now), http.RequestAborted);
        agents.Disconnect(id, "host revoked");
        if (host.ManagementAgentId is { } managementAgent)
        {
            // Remote host: the management agent drops its credentials from memory.
            await agents.ReconfigureManagementAgentAsync(managementAgent, http.RequestAborted);
        }
        await http.WriteAuditAsync("hosts.revoke", resourceType: "host", resourceId: id.ToString("D"), details: new { certificatesRevoked = certs });
        return Results.NoContent();
    }

    private static async Task<IResult> RefreshHostAsync(Guid id, HttpContext http, IAgentDispatcher agents, IServiceScopeFactory scopes)
    {
        if (await http.GuardAsync(Permissions.InventoryRead, ResourceRef.Host(id), "hosts.refresh") is { } denied)
        {
            return denied;
        }
        try
        {
            var result = await agents.SendAsync(id, new AgentCommand { CommandId = Guid.NewGuid(), Kind = AgentCommandKind.CollectInventory },
                null, TimeSpan.FromSeconds(90), http.RequestAborted);
            if (result.Inventory is { } inv)
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<InventoryIngestor>().ApplyAsync(id, inv, http.RequestAborted);
            }
            return Results.Ok(new { status = result.Status.ToString(), access = result.Inventory?.Access.ToString() });
        }
        catch (HostNotConnectedException)
        {
            return ApiSupport.Error(StatusCodes.Status503ServiceUnavailable, "host_unreachable", "The host agent is not connected.");
        }
        catch (Exception ex) when (ex is TimeoutException or AgentChannelLostException)
        {
            return ApiSupport.Error(StatusCodes.Status504GatewayTimeout, "host_timeout", ex.Message);
        }
    }

    // ------------------------------------------------------------------ VMs

    private static async Task<object> VmDtoAsync(VirtualMachine v, ManagedHost? host, bool connected, DateTimeOffset now, Guid uid, AccessEvaluator access, CancellationToken ct)
    {
        var canPower = await access.HasAsync(uid, Permissions.VmPower, ResourceRef.Vm(v.Id), ct);
        var canDelete = await access.HasAsync(uid, Permissions.VmDelete, ResourceRef.Vm(v.Id), ct);
        var canAnnotate = await access.HasAsync(uid, Permissions.InventoryAnnotate, ResourceRef.Vm(v.Id), ct);
        var canForce = await access.HasAsync(uid, Permissions.VmPowerForce, ResourceRef.Vm(v.Id), ct);
        var r = ResourceRef.Vm(v.Id);
        var canConsole = await access.HasAsync(uid, Permissions.VmConsole, r, ct);
        var canConfigure = await access.HasAsync(uid, Permissions.VmConfigCompute, r, ct) || await access.HasAsync(uid, Permissions.VmConfigStorage, r, ct)
                           || await access.HasAsync(uid, Permissions.VmConfigNetwork, r, ct);
        var canCheckpoint = await access.HasAsync(uid, Permissions.VmCheckpointCreate, r, ct);
        // Templates are a matter of the host: created by who manages them there (agent 0.6.0+).
        var canTemplate = await access.HasAsync(uid, Permissions.TemplatesManage, ResourceRef.Host(v.HostId), ct);
        var canApplyCheckpoint = await access.HasAsync(uid, Permissions.VmCheckpointApply, r, ct);
        var allowed = Enum.GetValues<VmPowerOperation>()
            .Where(op => VmPowerRules.IsAllowed(op, v.PowerState) && (VmPowerRules.IsDestructive(op) ? canForce : canPower))
            .Where(op => op is not (VmPowerOperation.Shutdown or VmPowerOperation.Restart) || v.ShutdownServiceAvailable == true)
            .Select(op => op.ToString());
        return new
        {
            v.Id,
            v.HyperVId,
            v.Name,
            v.CheckpointCount,
            v.HostId,
            hostName = host?.DisplayName,
            hostConnected = connected,
            stale = !connected || host?.LastInventoryAt is null || now - host.LastInventoryAt > StaleAfter || v.IsMissing,
            powerState = v.PowerState.ToString(),
            v.IsCritical,
            v.Generation,
            v.ConfigurationVersion,
            v.ProcessorCount,
            v.MemoryStartupMb,
            v.MemoryAssignedMb,
            v.DynamicMemoryEnabled,
            v.UptimeMs,
            v.Heartbeat,
            v.HealthState,
            v.ProcessorLoadPercent,
            v.GuestOperatingSystem,
            v.ShutdownServiceAvailable,
            hyperVCreatedAt = v.HyperVCreatedAt,
            notes = v.HyperVNotes,
            v.ConfigurationPath,
            v.MemoryAvailablePercent,
            v.MemoryBufferPercent,
            // Memory the guest actually needs (dynamic memory): assigned x (100 - available %) / 100.
            memoryDemandMb = v.DynamicMemoryEnabled == true && v.MemoryAssignedMb is { } assigned && v.MemoryAvailablePercent is { } avail && v.PowerState == VmPowerState.Running
                ? (long?)Math.Max(0, assigned * (100 - avail) / 100) : null,
            v.ReplicationMode,
            v.ReplicationState,
            v.ReplicationHealth,
            v.LastSeenAt,
            v.StateChangedAt,
            v.IsMissing,
            v.MissingSince,
            allowedOperations = connected && !v.IsMissing ? allowed.ToArray() : [],
            // Hyper-V notes of the VM (written on the host through a configuration job).
            canAnnotate = canAnnotate && !v.IsMissing,
            canDelete = canDelete && connected && !v.IsMissing && v.PowerState is VmPowerState.Off or VmPowerState.Saved,
            canForget = canDelete && v.IsMissing,
            canConsole = canConsole && !v.IsMissing,
            canConfigure = canConfigure && !v.IsMissing,
            canCheckpoint = canCheckpoint && !v.IsMissing,
            canTemplate = canTemplate && connected && !v.IsMissing && HostCapabilities.AgentAtLeast(host?.AgentVersion, 0, 6),
            canApplyCheckpoint = canApplyCheckpoint && !v.IsMissing,
            destructiveOperations = Enum.GetValues<VmPowerOperation>().Where(VmPowerRules.IsDestructive).Select(o => o.ToString()),
        };
    }

    private static async Task<IResult> ListVmsAsync(HttpContext http, PortalDbContext db, AccessEvaluator access, IAgentDispatcher agents, TimeProvider time,
        Guid? hostId, string? search, string? state)
    {
        var uid = http.User.UserId();
        var q = db.VirtualMachines.AsNoTracking().AsQueryable();
        if (hostId is { } h)
        {
            q = q.Where(v => v.HostId == h);
        }
        if (!string.IsNullOrWhiteSpace(search) && search.Length <= 100)
        {
            q = q.Where(v => v.Name.Contains(search));
        }
        if (Enum.TryParse<VmPowerState>(state, true, out var s))
        {
            q = q.Where(v => v.PowerState == s);
        }
        var all = await q.OrderBy(v => v.Name).Take(5000).ToListAsync(http.RequestAborted);
        var visible = await access.FilterVmsAsync(uid, Permissions.InventoryRead, all.Select(v => v.Id), http.RequestAborted);
        var hosts = await db.Hosts.AsNoTracking().ToDictionaryAsync(x => x.Id, http.RequestAborted);
        var now = time.GetUtcNow();
        var list = new List<object>();
        foreach (var v in all.Where(v => visible.Contains(v.Id)))
        {
            list.Add(await VmDtoAsync(v, hosts.GetValueOrDefault(v.HostId), agents.IsConnected(v.HostId), now, uid, access, http.RequestAborted));
        }
        return Results.Ok(list);
    }

    private static async Task<VirtualMachine?> ReadableVmAsync(Guid id, HttpContext http, PortalDbContext db, AccessEvaluator access)
    {
        var vm = await db.VirtualMachines.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id, http.RequestAborted);
        // Unauthorized and nonexistent VMs are indistinguishable (404) to avoid disclosing inventory.
        return vm is not null && await access.HasAsync(http.User.UserId(), Permissions.InventoryRead, ResourceRef.Vm(id), http.RequestAborted) ? vm : null;
    }

    private static async Task<IResult> GetVmAsync(Guid id, HttpContext http, PortalDbContext db, AccessEvaluator access, IAgentDispatcher agents, TimeProvider time)
    {
        var vm = await ReadableVmAsync(id, http, db, access);
        if (vm is null)
        {
            return ApiSupport.NotFound();
        }
        var host = await db.Hosts.AsNoTracking().FirstOrDefaultAsync(h => h.Id == vm.HostId, http.RequestAborted);
        var uid = http.User.UserId();
        var seeAll = await access.HasAsync(uid, Permissions.JobsReadAll, ResourceRef.Global, http.RequestAborted);
        var jobs = await db.Jobs.AsNoTracking().Where(j => j.TargetId == id && (seeAll || j.RequestedByUserId == uid))
            .OrderByDescending(j => j.CreatedAt).Take(20).ToListAsync(http.RequestAborted);
        return Results.Ok(new
        {
            vm = await VmDtoAsync(vm, host, agents.IsConnected(vm.HostId), time.GetUtcNow(), uid, access, http.RequestAborted),
            recentJobs = jobs.Select(JobEndpoints.Dto),
        });
    }

    private static async Task<IResult> VmEventsAsync(Guid id, HttpContext http, PortalDbContext db, AccessEvaluator access)
    {
        if (await ReadableVmAsync(id, http, db, access) is null)
        {
            return ApiSupport.NotFound();
        }
        var events = await db.InventoryEvents.AsNoTracking().Where(e => e.VmId == id).OrderByDescending(e => e.Id).Take(100).ToListAsync(http.RequestAborted);
        return Results.Ok(events);
    }

    private static async Task<IResult> PowerAsync(Guid id, PowerRequest req, HttpContext http, PortalDbContext db, AccessEvaluator access,
        JobService jobs, IAgentDispatcher agents, TimeProvider time)
    {
        var vm = await ReadableVmAsync(id, http, db, access);
        if (vm is null)
        {
            return ApiSupport.NotFound();
        }
        if (!Enum.IsDefined(req.Operation))
        {
            return ApiSupport.BadRequest("invalid_operation");
        }
        var action = "vm.power." + req.Operation.ToString().ToLowerInvariant() + ".request";
        if (await http.GuardAsync(JobService.PermissionFor(req.Operation), ResourceRef.Vm(id), action) is { } denied)
        {
            return denied;
        }
        var idempotencyKey = req.IdempotencyKey ?? http.Request.Headers["Idempotency-Key"].FirstOrDefault();
        if (idempotencyKey is not null && !ApiSupport.IdempotencyKeyRegex().IsMatch(idempotencyKey))
        {
            return ApiSupport.BadRequest("invalid_idempotency_key");
        }
        if (VmPowerRules.IsDestructive(req.Operation) && !string.Equals(req.ConfirmName, vm.Name, StringComparison.Ordinal))
        {
            return ApiSupport.BadRequest("confirmation_required",
                $"{req.Operation} is immediate and can lose unsaved guest data. Type the VM name exactly to confirm.");
        }
        if (vm.IsMissing)
        {
            return ApiSupport.Conflict("vm_missing", "The VM is not present in the latest inventory.");
        }
        var host = await db.Hosts.AsNoTracking().FirstAsync(h => h.Id == vm.HostId, http.RequestAborted);
        var fresh = agents.IsConnected(vm.HostId) && host.LastInventoryAt is { } at && time.GetUtcNow() - at <= StaleAfter;
        // The agent re-checks the live state; here we only reject requests that are invalid against fresh data.
        if (fresh && !VmPowerRules.IsAllowed(req.Operation, vm.PowerState) && !VmPowerRules.IsAlreadyInTargetState(req.Operation, vm.PowerState))
        {
            return ApiSupport.Conflict("invalid_state", $"{req.Operation} is not available while the VM is {vm.PowerState}.");
        }
        try
        {
            var (job, created) = await jobs.CreateVmPowerJobAsync(http.User.UserId(), vm, req.Operation, idempotencyKey, http.RequestAborted);
            if (created)
            {
                await http.WriteAuditAsync(action, resourceType: "vm", resourceId: id.ToString("D"), details: new { jobId = job.Id, vm = vm.Name });
            }
            return Results.Accepted($"/api/v1/jobs/{job.Id}", new { jobId = job.Id, created });
        }
        catch (IdempotencyConflictException ex)
        {
            return ApiSupport.Conflict("idempotency_conflict", ex.Message);
        }
    }
}
