using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using HvPortal.Contracts.Agent;
using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Agents;
using HvPortal.Gateway.Audit;
using HvPortal.Gateway.Configuration;
using HvPortal.Gateway.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HvPortal.Gateway.VmGroups;

/// <summary>Wake-up signal for the mirroring (no dependencies, so the realtime notifier can raise it). Null = all hosts.</summary>
public sealed class VmGroupSyncSignal
{
    private readonly Channel<Guid?> _channel = Channel.CreateBounded<Guid?>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
    public void Request(Guid? hostId = null) => _channel.Writer.TryWrite(hostId);
    public ChannelReader<Guid?> Reader => _channel.Reader;
}

public sealed record VmGroupSyncOutcome(string Status, IReadOnlyList<string> Applied, IReadOnlyList<string> Errors, string? Message = null);

/// <summary>
/// Mirrors portal groups to native Hyper-V VM groups on every connected host with agent protocol 4 (ADR-017).
/// The portal is the source of truth; only native groups linked in HostVmGroupLinks are changed or removed.
/// Runs after group/inventory changes (debounced; skipped when the plan did not change) and periodically to correct
/// changes made directly in Hyper-V.
/// </summary>
public sealed class VmGroupSyncService : BackgroundService
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);
    private readonly IServiceScopeFactory _scopes;
    private readonly IAgentDispatcher _agents;
    private readonly AuditLog _audit;
    private readonly VmGroupSyncSignal _signal;
    private readonly TimeProvider _time;
    private readonly VmGroupOptions _options;
    private readonly ILogger<VmGroupSyncService> _logger;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _hostLocks = new();

    public VmGroupSyncService(IServiceScopeFactory scopes, IAgentDispatcher agents, AuditLog audit, VmGroupSyncSignal signal, TimeProvider time,
        IOptions<GatewayOptions> options, ILogger<VmGroupSyncService> logger)
    {
        _scopes = scopes;
        _agents = agents;
        _audit = audit;
        _signal = signal;
        _time = time;
        _options = options.Value.VmGroups;
        _logger = logger;
    }

    public static bool Supported(IAgentDispatcher agents, Guid hostId) => agents.ProtocolVersionOf(hostId) >= 4;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.SyncEnabled)
        {
            return;
        }
        var periodic = TimeSpan.FromMinutes(Math.Max(1, _options.PeriodicMinutes));
        var nextFull = _time.GetUtcNow() + TimeSpan.FromSeconds(30);
        while (!stoppingToken.IsCancellationRequested)
        {
            var targets = new HashSet<Guid>();
            var all = false;
            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var delay = nextFull - _time.GetUtcNow();
                wait.CancelAfter(delay > TimeSpan.Zero ? delay : TimeSpan.Zero);
                var first = await _signal.Reader.ReadAsync(wait.Token);
                await Task.Delay(Debounce, stoppingToken);
                if (first is { } h) targets.Add(h); else all = true;
                while (_signal.Reader.TryRead(out var more))
                {
                    if (more is { } m) targets.Add(m); else all = true;
                }
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // periodic full pass: re-apply even if the plan did not change (drift made in Hyper-V Manager)
            }
            var force = _time.GetUtcNow() >= nextFull;
            if (force)
            {
                all = true;
                nextFull = _time.GetUtcNow() + periodic;
            }
            try
            {
                if (all)
                {
                    using var scope = _scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
                    targets.UnionWith(await db.Hosts.AsNoTracking().Where(x => x.Status == HostStatus.Active).Select(x => x.Id).ToListAsync(stoppingToken));
                }
                foreach (var hostId in targets)
                {
                    if (_agents.IsConnected(hostId) && Supported(_agents, hostId))
                    {
                        await SyncHostAsync(hostId, force, stoppingToken);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "VM group mirroring pass failed");
            }
        }
    }

    /// <summary>Plan of a host from the current portal state.</summary>
    public static async Task<VmGroupSyncRequest> PlanAsync(PortalDbContext db, Guid hostId, CancellationToken ct)
    {
        var groups = await db.LogicalGroups.AsNoTracking().Select(g => new VmGroupPlanner.GroupNode(g.Id, g.ParentId, g.Name)).ToListAsync(ct);
        var hostVms = await db.VirtualMachines.AsNoTracking().Where(v => v.HostId == hostId && !v.IsMissing)
            .Select(v => new { v.Id, v.HyperVId }).ToListAsync(ct);
        var ids = hostVms.ToDictionary(v => v.Id, v => v.HyperVId);
        var vmIds = ids.Keys.ToList();
        var members = (await db.LogicalGroupMembers.AsNoTracking().Where(m => vmIds.Contains(m.VmId)).ToListAsync(ct))
            .Select(m => (m.GroupId, m.VmId)).ToList();
        var links = await db.HostVmGroupLinks.AsNoTracking().Where(l => l.HostId == hostId)
            .Select(l => new VmGroupPlanner.Link(l.GroupId, l.Role, l.NativeId)).ToListAsync(ct);
        return VmGroupPlanner.Plan(groups, members, ids, links);
    }

    public async Task<VmGroupSyncOutcome> SyncHostAsync(Guid hostId, bool force, CancellationToken ct)
    {
        var gate = _hostLocks.GetOrAdd(hostId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
            var host = await db.Hosts.FirstOrDefaultAsync(h => h.Id == hostId && h.Status == HostStatus.Active, ct);
            if (host is null)
            {
                return new VmGroupSyncOutcome("not_found", [], []);
            }
            if (!_agents.IsConnected(hostId))
            {
                return new VmGroupSyncOutcome("host_unreachable", [], []);
            }
            if (!Supported(_agents, hostId))
            {
                return new VmGroupSyncOutcome("agent_outdated", [], [], "Native VM groups need agent 0.3.2 or later.");
            }
            if (host.Access != HyperVAccessStatus.Ok)
            {
                return new VmGroupSyncOutcome("no_access", [], [], "The agent has no access to Hyper-V on this host.");
            }
            var plan = await PlanAsync(db, hostId, ct);
            var hash = VmGroupPlanner.Hash(plan);
            if (!force && host.VmGroupsPlanHash == hash && host.VmGroupsSyncError is null)
            {
                return new VmGroupSyncOutcome("unchanged", [], []);
            }
            // After a failure, automatic passes wait before retrying (each attempt starts PowerShell on the host).
            if (!force && host.VmGroupsSyncError is not null && host.VmGroupsSyncedAt is { } last && _time.GetUtcNow() - last < RetryAfterFailure)
            {
                return new VmGroupSyncOutcome("backoff", [], [host.VmGroupsSyncError]);
            }
            var (outcome, error) = await SendAsync(hostId, HvOperation.SyncVmGroups, plan, 180, ct);
            host.VmGroupsSyncedAt = _time.GetUtcNow();
            if (outcome is null)
            {
                host.VmGroupsSyncError = error;
                host.VmGroupsPlanHash = null;
                await db.SaveChangesAsync(ct);
                return new VmGroupSyncOutcome("failed", [], [error ?? "failed"]);
            }
            var result = HvOperations.FromElement<VmGroupSyncResult>(outcome.Result) ?? new VmGroupSyncResult();
            await ApplyLinksAsync(db, hostId, result, ct);
            host.VmGroupsSyncError = result.Errors.Count == 0 ? null : Truncate(string.Join(" | ", result.Errors), 4000);
            await db.SaveChangesAsync(ct);
            // Hash of the plan as it is now (with the native ids just linked): the next pass sends only if something changes.
            host.VmGroupsPlanHash = result.Errors.Count == 0 ? VmGroupPlanner.Hash(await PlanAsync(db, hostId, ct)) : null;
            await db.SaveChangesAsync(ct);
            if (outcome.Applied.Count > 0 || result.Errors.Count > 0)
            {
                await _audit.WriteAsync(new AuditRecord
                {
                    Action = "hosts.vmgroups.sync",
                    Outcome = result.Errors.Count == 0 ? AuditOutcome.Success : AuditOutcome.Failure,
                    ActorName = "system",
                    AuthMethod = "system",
                    ResourceType = "host",
                    ResourceId = hostId.ToString("D"),
                    Details = new { applied = outcome.Applied.Take(100), errors = result.Errors.Take(20) },
                }, ct);
            }
            return new VmGroupSyncOutcome(result.Errors.Count == 0 ? "ok" : "partial", outcome.Applied, result.Errors);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task ApplyLinksAsync(PortalDbContext db, Guid hostId, VmGroupSyncResult result, CancellationToken ct)
    {
        var links = await db.HostVmGroupLinks.Where(l => l.HostId == hostId).ToListAsync(ct);
        var removed = result.Removed.ToHashSet();
        db.HostVmGroupLinks.RemoveRange(links.Where(l => removed.Contains(l.NativeId)));
        foreach (var (key, nativeId) in result.Ids)
        {
            var parts = key.Split(':');
            if (parts.Length != 2 || !Guid.TryParse(parts[0], out var groupId) || parts[1] is not (VmGroupPlanner.RoleVms or VmGroupPlanner.RoleMgmt))
            {
                continue;
            }
            var existing = links.FirstOrDefault(l => l.GroupId == groupId && l.Role == parts[1] && !removed.Contains(l.NativeId));
            if (existing is null)
            {
                db.HostVmGroupLinks.Add(new HostVmGroupLink { HostId = hostId, GroupId = groupId, Role = parts[1], NativeId = nativeId, CreatedAt = _time.GetUtcNow() });
            }
            else if (existing.NativeId != nativeId)
            {
                // Recreated (e.g. deleted in Hyper-V Manager): the old native id no longer exists.
                existing.NativeId = nativeId;
            }
        }
    }

    /// <summary>Sends one typed operation; returns the outcome or a short error.</summary>
    public async Task<(HvOperationOutcome? Outcome, string? Error)> SendAsync(Guid hostId, HvOperation op, object args, int timeoutSeconds, CancellationToken ct)
    {
        try
        {
            var r = await _agents.SendAsync(hostId, new AgentCommand
            {
                CommandId = Guid.NewGuid(),
                Kind = AgentCommandKind.HyperVOperation,
                Operation = op,
                OperationArgs = HvOperations.ToElement(args),
                TimeoutSeconds = timeoutSeconds,
            }, null, TimeSpan.FromSeconds(timeoutSeconds + 15), ct);
            if (r.Operation is { Ok: true } ok)
            {
                return (ok, null);
            }
            return (null, r.Operation?.Error ?? r.Message ?? r.Status.ToString());
        }
        catch (Exception ex) when (ex is HostNotConnectedException or AgentChannelLostException or TimeoutException)
        {
            return (null, ex.Message);
        }
    }

    public async Task<(IReadOnlyList<NativeVmGroup>? Groups, string? Error)> ListAsync(Guid hostId, CancellationToken ct)
    {
        var (outcome, error) = await SendAsync(hostId, HvOperation.ListVmGroups, new { }, 60, ct);
        if (outcome is null)
        {
            return (null, error);
        }
        return (outcome.Result is { } r ? JsonSerializer.Deserialize<List<NativeVmGroup>>(r.GetRawText(), AgentProtocol.Json) ?? [] : [], null);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
