using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using HvPortal.Contracts.Agent;
using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Audit;
using HvPortal.Gateway.Configuration;
using HvPortal.Gateway.Crypto;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Pki;
using HvPortal.Gateway.Realtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HvPortal.Gateway.Agents;

/// <summary>
/// Accepts agent WebSocket channels (agent-initiated, mutual TLS) and routes typed commands to them.
/// Authentication: TLS client certificate chained to the internal CA (validated by Kestrel) AND an
/// active, non-revoked, non-expired AgentCertificate row bound to an enrolled host or management agent.
/// Management agents (ADR-022): after Hello the gateway sends the list of remote hosts assigned to the agent, with their
/// credentials; every remote host id is then routed to that channel and each command carries its TargetHostId.
/// Inventory reported for a host that is not assigned to the reporting agent is discarded.
/// </summary>
public sealed class AgentConnectionManager : IAgentDispatcher
{
    /// <summary>Routable host id -> channel (the host's own agent, or the management agent that reaches it).</summary>
    private readonly ConcurrentDictionary<Guid, AgentSession> _sessions = new();
    /// <summary>Management agent id -> channel.</summary>
    private readonly ConcurrentDictionary<Guid, AgentSession> _managementSessions = new();
    // Rejected agents retry about once a minute: audit each rejected certificate at most once per hour.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _deniedAudited = new();
    private readonly IServiceScopeFactory _scopes;
    private readonly InternalCertificateAuthority _ca;
    private readonly KeyRing _keys;
    private readonly AuditLog _audit;
    private readonly IRealtimeNotifier _notifier;
    private readonly TimeProvider _time;
    private readonly GatewayOptions _options;
    private readonly ILogger<AgentConnectionManager> _logger;

    public AgentConnectionManager(IServiceScopeFactory scopes, InternalCertificateAuthority ca, KeyRing keys, AuditLog audit,
        IRealtimeNotifier notifier, TimeProvider time, IOptions<GatewayOptions> options, ILogger<AgentConnectionManager> logger)
    {
        _scopes = scopes;
        _ca = ca;
        _keys = keys;
        _audit = audit;
        _notifier = notifier;
        _time = time;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsConnected(Guid hostId) => _sessions.ContainsKey(hostId);

    public int ProtocolVersionOf(Guid hostId) => _sessions.TryGetValue(hostId, out var s) ? s.ProtocolVersion : 0;

    public IReadOnlyCollection<Guid> ConnectedHosts => _sessions.Keys.ToList();

    public bool IsManagementAgentConnected(Guid agentId) => _managementSessions.ContainsKey(agentId);

    public async Task HandleChannelAsync(HttpContext http)
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        var cert = await http.Connection.GetClientCertificateAsync(http.RequestAborted);
        if (cert is null || !_ca.ValidateClientCertificate(cert))
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        var thumbprint = InternalCertificateAuthority.Sha256Thumbprint(cert);
        Guid subjectId;
        bool isManagement;
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
            var now = _time.GetUtcNow();
            var row = await db.AgentCertificates.AsNoTracking().FirstOrDefaultAsync(c => c.ThumbprintSha256 == thumbprint, http.RequestAborted);
            var host = row is null ? null : await db.Hosts.AsNoTracking().FirstOrDefaultAsync(h => h.Id == row.HostId, http.RequestAborted);
            var management = row is null || host is not null ? null : await db.ManagementAgents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == row.HostId, http.RequestAborted);
            var active = host is { Status: HostStatus.Active, ConnectionMode: HostConnectionMode.Agent } || management is { Status: HostStatus.Active };
            if (row is null || row.RevokedAt is not null || row.NotAfter <= now || !active)
            {
                var last = _deniedAudited.GetValueOrDefault(thumbprint);
                if (now - last > TimeSpan.FromHours(1))
                {
                    _deniedAudited[thumbprint] = now;
                    await _audit.WriteAsync(new AuditRecord
                    {
                        Action = "agent.connect",
                        Outcome = AuditOutcome.Denied,
                        AuthMethod = "agent-mtls",
                        SourceIp = http.Connection.RemoteIpAddress?.ToString(),
                        ResourceType = management is not null ? "management_agent" : "host",
                        ResourceId = row?.HostId.ToString("D"),
                        Details = new
                        {
                            reason = row is null ? "unknown certificate (host deleted?)" : row.RevokedAt is not null ? "revoked certificate" : "inactive host/agent or expired",
                            note = "further attempts with this certificate are audited at most hourly",
                        },
                    });
                }
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            subjectId = row.HostId;
            isManagement = management is not null;
        }

        using var ws = await http.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        var session = new AgentSession(subjectId, thumbprint, ws, isManagement);
        if (isManagement)
        {
            if (_managementSessions.TryGetValue(subjectId, out var previous))
            {
                UnmapTargets(previous);
                previous.Abort("superseded by a new connection");
            }
            _managementSessions[subjectId] = session;
            await TouchManagementAgentAsync(subjectId, null);
        }
        else
        {
            if (_sessions.TryGetValue(subjectId, out var previous))
            {
                previous.Abort("superseded by a new connection");
            }
            _sessions[subjectId] = session;
            await TouchHostAsync(subjectId, null);
        }
        _logger.LogInformation("Agent {Kind} {SubjectId} connected from {Ip}", isManagement ? "management" : "host", subjectId, http.Connection.RemoteIpAddress);
        await _audit.WriteAsync(new AuditRecord
        {
            Action = "agent.connected",
            AuthMethod = "agent-mtls",
            SourceIp = http.Connection.RemoteIpAddress?.ToString(),
            ResourceType = isManagement ? "management_agent" : "host",
            ResourceId = subjectId.ToString("D"),
        });
        if (!isManagement)
        {
            await _notifier.HostChangedAsync(subjectId);
        }

        try
        {
            await ReceiveLoopAsync(session, http.RequestAborted);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
        {
            _logger.LogInformation("Agent channel {SubjectId} closed: {Message}", subjectId, ex.Message);
        }
        finally
        {
            if (isManagement)
            {
                _managementSessions.TryRemove(new KeyValuePair<Guid, AgentSession>(subjectId, session));
                foreach (var hostId in UnmapTargets(session))
                {
                    await _notifier.HostChangedAsync(hostId);
                }
            }
            else
            {
                _sessions.TryRemove(new KeyValuePair<Guid, AgentSession>(subjectId, session));
                await _notifier.HostChangedAsync(subjectId);
            }
            session.FailPending(new AgentChannelLostException(subjectId));
        }
    }

    public async Task<AgentCommandResult> SendAsync(Guid hostId, AgentCommand command, Action<AgentCommandProgress>? onProgress,
        TimeSpan timeout, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(hostId, out var session))
        {
            throw new HostNotConnectedException(hostId);
        }
        // Remote host: the management agent needs to know which host the command is for (server-controlled field).
        command = command with { TargetHostId = session.IsManagement ? hostId : null };
        // Older agents cannot even parse newer command kinds (and would drop the channel): refuse locally, nothing is sent.
        var required = AgentProtocol.RequiredVersion(command);
        if (session.ProtocolVersion < required)
        {
            var minAgent = required switch
            {
                >= 6 => "0.4.0",
                5 => "0.3.3",
                4 => "0.3.2",
                3 => "0.3.1",
                _ => "0.3",
            };
            return new AgentCommandResult
            {
                CommandId = command.CommandId,
                Status = AgentCommandStatus.Rejected,
                Message = $"The agent on this host does not support this operation (agent protocol {session.ProtocolVersion}). Update the agent to {minAgent} or later.",
            };
        }
        var pending = session.Register(command.CommandId, onProgress);
        try
        {
            if (!await session.TrySendAsync(AgentEnvelope.Create(AgentMessageType.Command, command.CommandId.ToString("D"), command), ct))
            {
                throw new HostNotConnectedException(hostId);
            }
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            try
            {
                return await pending.Task.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"No result from host {hostId} within {timeout.TotalSeconds:0}s.");
            }
        }
        finally
        {
            session.Unregister(command.CommandId);
        }
    }

    public void Disconnect(Guid hostId, string reason)
    {
        if (!_sessions.TryGetValue(hostId, out var s))
        {
            return;
        }
        if (s.IsManagement)
        {
            // A remote host: stop routing to it, keep the management agent connected for its other hosts.
            s.Targets.TryRemove(hostId, out _);
            _sessions.TryRemove(new KeyValuePair<Guid, AgentSession>(hostId, s));
            return;
        }
        if (_sessions.TryRemove(hostId, out var removed))
        {
            removed.Abort(reason);
        }
    }

    public void DisconnectManagementAgent(Guid agentId, string reason)
    {
        if (_managementSessions.TryRemove(agentId, out var s))
        {
            UnmapTargets(s);
            s.Abort(reason);
        }
    }

    public async Task ReconfigureManagementAgentAsync(Guid agentId, CancellationToken ct)
    {
        if (_managementSessions.TryGetValue(agentId, out var s) && s.ProtocolVersion > 0)
        {
            await PushTargetsAsync(s, ct);
        }
    }

    /// <summary>Drops sessions whose certificate was revoked or expired (defense in depth besides explicit Disconnect).</summary>
    public async Task EnforceCertificateValidityAsync(CancellationToken ct)
    {
        if (_sessions.IsEmpty && _managementSessions.IsEmpty)
        {
            return;
        }
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
        var now = _time.GetUtcNow();
        foreach (var (hostId, session) in _sessions)
        {
            if (session.IsManagement)
            {
                continue;
            }
            var ok = await db.AgentCertificates.AsNoTracking().AnyAsync(c => c.ThumbprintSha256 == session.Thumbprint && c.RevokedAt == null && c.NotAfter > now, ct)
                     && await db.Hosts.AsNoTracking().AnyAsync(h => h.Id == hostId && h.Status == HostStatus.Active, ct);
            if (!ok)
            {
                Disconnect(hostId, "certificate no longer valid");
            }
        }
        foreach (var (agentId, session) in _managementSessions)
        {
            var ok = await db.AgentCertificates.AsNoTracking().AnyAsync(c => c.ThumbprintSha256 == session.Thumbprint && c.RevokedAt == null && c.NotAfter > now, ct)
                     && await db.ManagementAgents.AsNoTracking().AnyAsync(a => a.Id == agentId && a.Status == HostStatus.Active, ct);
            if (!ok)
            {
                DisconnectManagementAgent(agentId, "certificate no longer valid");
            }
        }
    }

    private async Task ReceiveLoopAsync(AgentSession session, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (session.Socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult r;
            do
            {
                r = await session.Socket.ReceiveAsync(buffer, ct);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }
                ms.Write(buffer, 0, r.Count);
                if (ms.Length > AgentProtocol.MaxMessageBytes)
                {
                    session.Abort("message too large");
                    return;
                }
            } while (!r.EndOfMessage);

            AgentEnvelope? env;
            try
            {
                env = JsonSerializer.Deserialize<AgentEnvelope>(ms.ToArray(), AgentProtocol.Json);
            }
            catch (JsonException)
            {
                _logger.LogWarning("Malformed message from agent {SubjectId}", session.SubjectId);
                continue;
            }
            if (env is not null)
            {
                await HandleMessageAsync(session, env, ct);
            }
        }
    }

    private async Task HandleMessageAsync(AgentSession session, AgentEnvelope env, CancellationToken ct)
    {
        switch (env.Type)
        {
            case AgentMessageType.Hello when env.Read<AgentHello>() is { } hello:
                session.ProtocolVersion = hello.ProtocolVersion;
                session.AgentVersion = hello.AgentVersion is { Length: > 32 } v ? v[..32] : hello.AgentVersion;
                if (session.IsManagement)
                {
                    await TouchManagementAgentAsync(session.SubjectId, session.AgentVersion);
                    await PushTargetsAsync(session, ct);
                }
                else
                {
                    await TouchHostAsync(session.SubjectId, session.AgentVersion);
                }
                break;

            case AgentMessageType.Inventory when !session.IsManagement && env.Read<HostInventory>() is { } inv:
                await IngestAsync(session.SubjectId, inv, ct);
                break;

            case AgentMessageType.TargetInventory when session.IsManagement && env.Read<TargetInventory>() is { } ti:
                if (ti.HostId != Guid.Empty && session.Targets.ContainsKey(ti.HostId))
                {
                    await IngestAsync(ti.HostId, ti.Inventory, ct);
                }
                else
                {
                    // Never accept data for a host this agent was not given: it could overwrite another host's inventory.
                    _logger.LogWarning("Management agent {AgentId} reported inventory for unassigned host {HostId}: discarded", session.SubjectId, ti.HostId);
                }
                break;

            case AgentMessageType.CommandResult when env.Read<AgentCommandResult>() is { } result:
                session.Complete(result);
                break;

            case AgentMessageType.CommandProgress when env.Read<AgentCommandProgress>() is { } progress:
                session.Progress(progress);
                break;

            case AgentMessageType.RenewCertificate when env.Read<RenewCertificateRequest>() is { } renew:
                await RenewAsync(session, renew, ct);
                break;
        }
    }

    private async Task IngestAsync(Guid hostId, HostInventory inv, CancellationToken ct)
    {
        using (var scope = _scopes.CreateScope())
        {
            var ingestor = scope.ServiceProvider.GetRequiredService<InventoryIngestor>();
            await ingestor.ApplyAsync(hostId, inv, ct);
        }
        await _notifier.HostChangedAsync(hostId);
    }

    /// <summary>
    /// Sends the management agent its remote hosts (with decrypted credentials, over its mTLS channel only) and updates
    /// the routing table. Hosts no longer assigned stop being routed to it.
    /// </summary>
    private async Task PushTargetsAsync(AgentSession session, CancellationToken ct)
    {
        await session.ConfigLock.WaitAsync(ct);
        try
        {
            if (session.ProtocolVersion < 6)
            {
                _logger.LogWarning("Management agent {AgentId} announced protocol {Version}: remote hosts need 0.4.0 or later", session.SubjectId, session.ProtocolVersion);
                return;
            }
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
            var hosts = await db.Hosts.AsNoTracking()
                .Where(h => h.ManagementAgentId == session.SubjectId && h.ConnectionMode == HostConnectionMode.Remote
                            && h.Status == HostStatus.Active && h.RemotePasswordEncrypted != null)
                .ToListAsync(ct);
            var targets = new List<RemoteTarget>();
            foreach (var h in hosts)
            {
                try
                {
                    targets.Add(new RemoteTarget
                    {
                        HostId = h.Id,
                        ComputerName = h.RemoteComputerName!,
                        UserName = h.RemoteUserName!,
                        Password = RemoteHostCredentials.Unprotect(_keys, h.Id, h.RemotePasswordEncrypted!),
                        UseHttps = h.RemoteUseHttps,
                    });
                }
                catch (CryptographicException ex)
                {
                    _logger.LogWarning("Cannot decrypt the stored credentials of remote host {HostId}: {Message}", h.Id, ex.Message);
                }
            }
            if (!await session.TrySendAsync(AgentEnvelope.Create(AgentMessageType.TargetsConfiguration, null, new TargetsConfiguration { Targets = targets }), ct))
            {
                return;
            }

            var wanted = targets.Select(t => t.HostId).ToHashSet();
            var changed = new List<Guid>();
            foreach (var old in session.Targets.Keys.Where(id => !wanted.Contains(id)).ToList())
            {
                session.Targets.TryRemove(old, out _);
                _sessions.TryRemove(new KeyValuePair<Guid, AgentSession>(old, session));
                changed.Add(old);
            }
            foreach (var id in wanted)
            {
                if (_sessions.TryGetValue(id, out var other) && !ReferenceEquals(other, session))
                {
                    other.Targets.TryRemove(id, out _); // reassigned from another management agent
                }
                session.Targets[id] = 0;
                if (!ReferenceEquals(_sessions.GetValueOrDefault(id), session))
                {
                    _sessions[id] = session;
                    changed.Add(id);
                }
            }
            if (wanted.Count > 0 && session.AgentVersion is { } version)
            {
                // Capability checks (file explorer, VM groups, ...) read the version of the agent that runs the operations.
                await db.Hosts.Where(h => wanted.Contains(h.Id)).ExecuteUpdateAsync(u => u.SetProperty(h => h.AgentVersion, version), ct);
            }
            foreach (var id in changed)
            {
                await _notifier.HostChangedAsync(id);
            }
        }
        finally
        {
            session.ConfigLock.Release();
        }
    }

    /// <summary>Removes every remote host routed to this management channel; returns them.</summary>
    private List<Guid> UnmapTargets(AgentSession session)
    {
        var removed = new List<Guid>();
        foreach (var id in session.Targets.Keys.ToList())
        {
            session.Targets.TryRemove(id, out _);
            if (_sessions.TryRemove(new KeyValuePair<Guid, AgentSession>(id, session)))
            {
                removed.Add(id);
            }
        }
        return removed;
    }

    private async Task RenewAsync(AgentSession session, RenewCertificateRequest renew, CancellationToken ct)
    {
        try
        {
            var issued = _ca.IssueAgentCertificate(renew.CsrPem, session.SubjectId, TimeSpan.FromDays(_options.Security.AgentCertificateDays));
            using (var scope = _scopes.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
                db.AgentCertificates.Add(new AgentCertificate
                {
                    HostId = session.SubjectId,
                    ThumbprintSha256 = issued.ThumbprintSha256,
                    SerialNumber = issued.SerialNumber,
                    NotBefore = issued.NotBefore,
                    NotAfter = issued.NotAfter,
                    IssuedAt = _time.GetUtcNow(),
                });
                await db.SaveChangesAsync(ct);
                // The old certificate stays valid until it expires or the agent reconnects with the new one;
                // on the next successful connect with the new certificate older ones are revoked.
            }
            await session.TrySendAsync(AgentEnvelope.Create(AgentMessageType.RenewCertificateResult, null,
                new RenewCertificateResponse { CertificatePem = issued.CertificatePem }), ct);
            await _audit.WriteAsync(new AuditRecord
            {
                Action = "agent.certificate.renewed",
                AuthMethod = "agent-mtls",
                ResourceType = session.IsManagement ? "management_agent" : "host",
                ResourceId = session.SubjectId.ToString("D"),
                Details = new { serial = issued.SerialNumber, notAfter = issued.NotAfter },
            }, ct);
        }
        catch (CertificateRequestRejectedException ex)
        {
            await session.TrySendAsync(AgentEnvelope.Create(AgentMessageType.Error, null, new { code = "renew_rejected", message = ex.Message }), ct);
        }
    }

    private async Task TouchHostAsync(Guid hostId, string? agentVersion)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
        var host = await db.Hosts.FirstOrDefaultAsync(h => h.Id == hostId);
        if (host is null)
        {
            return;
        }
        host.LastSeenAt = _time.GetUtcNow();
        if (agentVersion is not null)
        {
            host.AgentVersion = agentVersion;
        }
        await db.SaveChangesAsync();
        if (_sessions.TryGetValue(hostId, out var s))
        {
            await RevokeSupersededAsync(db, hostId, s.Thumbprint);
        }
    }

    private async Task TouchManagementAgentAsync(Guid agentId, string? agentVersion)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
        var agent = await db.ManagementAgents.FirstOrDefaultAsync(a => a.Id == agentId);
        if (agent is null)
        {
            return;
        }
        agent.LastSeenAt = _time.GetUtcNow();
        if (agentVersion is not null)
        {
            agent.AgentVersion = agentVersion;
        }
        await db.SaveChangesAsync();
        if (_managementSessions.TryGetValue(agentId, out var s))
        {
            await RevokeSupersededAsync(db, agentId, s.Thumbprint);
        }
    }

    /// <summary>Revokes certificates older than the one in use once the agent has proven possession of a newer one.</summary>
    private async Task RevokeSupersededAsync(PortalDbContext db, Guid subjectId, string thumbprint)
    {
        var current = await db.AgentCertificates.AsNoTracking().FirstOrDefaultAsync(c => c.ThumbprintSha256 == thumbprint);
        if (current is not null)
        {
            var now = _time.GetUtcNow();
            await db.AgentCertificates
                .Where(c => c.HostId == subjectId && c.RevokedAt == null && c.Id != current.Id && c.IssuedAt < current.IssuedAt)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.RevokedAt, now).SetProperty(c => c.RevokedReason, "superseded"));
        }
    }

    private sealed class AgentSession(Guid subjectId, string thumbprint, WebSocket socket, bool isManagement)
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly ConcurrentDictionary<Guid, (TaskCompletionSource<AgentCommandResult> Tcs, Action<AgentCommandProgress>? Progress)> _pending = new();

        /// <summary>Host id, or management agent id.</summary>
        public Guid SubjectId { get; } = subjectId;
        public string Thumbprint { get; } = thumbprint;
        public bool IsManagement { get; } = isManagement;
        /// <summary>Management agents: remote host ids currently routed to this channel.</summary>
        public ConcurrentDictionary<Guid, byte> Targets { get; } = new();
        public SemaphoreSlim ConfigLock { get; } = new(1, 1);
        /// <summary>From the agent's Hello; 0 until it arrives.</summary>
        public int ProtocolVersion { get; set; }
        public string? AgentVersion { get; set; }
        public WebSocket Socket { get; } = socket;

        public TaskCompletionSource<AgentCommandResult> Register(Guid id, Action<AgentCommandProgress>? progress)
        {
            var tcs = new TaskCompletionSource<AgentCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = (tcs, progress);
            return tcs;
        }

        public void Unregister(Guid id) => _pending.TryRemove(id, out _);

        public void Complete(AgentCommandResult result)
        {
            if (_pending.TryGetValue(result.CommandId, out var p))
            {
                p.Tcs.TrySetResult(result);
            }
        }

        public void Progress(AgentCommandProgress progress)
        {
            if (_pending.TryGetValue(progress.CommandId, out var p))
            {
                p.Progress?.Invoke(progress);
            }
        }

        public void FailPending(Exception ex)
        {
            foreach (var p in _pending.Values)
            {
                p.Tcs.TrySetException(ex);
            }
        }

        public async Task<bool> TrySendAsync(AgentEnvelope env, CancellationToken ct)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(env, AgentProtocol.Json);
            await _sendLock.WaitAsync(ct);
            try
            {
                if (Socket.State != WebSocketState.Open)
                {
                    return false;
                }
                await Socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
                return true;
            }
            catch (WebSocketException)
            {
                return false;
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public void Abort(string reason)
        {
            FailPending(new AgentChannelLostException(SubjectId));
            try
            {
                Socket.Abort();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}

/// <summary>Periodically re-checks certificates of live agent sessions.</summary>
public sealed class AgentSessionWatchdog(AgentConnectionManager manager, ILogger<AgentSessionWatchdog> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await manager.EnforceCertificateValidityAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Agent session watchdog failed");
            }
        }
    }
}
