using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using HvPortal.Contracts.Agent;
using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Audit;
using HvPortal.Gateway.Configuration;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Pki;
using HvPortal.Gateway.Realtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HvPortal.Gateway.Agents;

/// <summary>
/// Accepts agent WebSocket channels (agent-initiated, mutual TLS) and routes typed commands to them.
/// Authentication: TLS client certificate chained to the internal CA (validated by Kestrel) AND an
/// active, non-revoked, non-expired AgentCertificate row bound to an enrolled host.
/// </summary>
public sealed class AgentConnectionManager : IAgentDispatcher
{
    private readonly ConcurrentDictionary<Guid, AgentSession> _sessions = new();
    // Rejected agents retry about once a minute: audit each rejected certificate at most once per hour.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _deniedAudited = new();
    private readonly IServiceScopeFactory _scopes;
    private readonly InternalCertificateAuthority _ca;
    private readonly AuditLog _audit;
    private readonly IRealtimeNotifier _notifier;
    private readonly TimeProvider _time;
    private readonly GatewayOptions _options;
    private readonly ILogger<AgentConnectionManager> _logger;

    public AgentConnectionManager(IServiceScopeFactory scopes, InternalCertificateAuthority ca, AuditLog audit,
        IRealtimeNotifier notifier, TimeProvider time, IOptions<GatewayOptions> options, ILogger<AgentConnectionManager> logger)
    {
        _scopes = scopes;
        _ca = ca;
        _audit = audit;
        _notifier = notifier;
        _time = time;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsConnected(Guid hostId) => _sessions.ContainsKey(hostId);

    public IReadOnlyCollection<Guid> ConnectedHosts => _sessions.Keys.ToList();

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
        Guid hostId;
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
            var now = _time.GetUtcNow();
            var row = await db.AgentCertificates.AsNoTracking().FirstOrDefaultAsync(c => c.ThumbprintSha256 == thumbprint, http.RequestAborted);
            var host = row is null ? null : await db.Hosts.AsNoTracking().FirstOrDefaultAsync(h => h.Id == row.HostId, http.RequestAborted);
            if (row is null || host is null || row.RevokedAt is not null || row.NotAfter <= now || host.Status != HostStatus.Active)
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
                    ResourceType = "host",
                    ResourceId = row?.HostId.ToString("D"),
                    Details = new { reason = row is null ? "unknown certificate (host deleted?)" : row.RevokedAt is not null ? "revoked certificate" : "inactive host or expired", note = "further attempts with this certificate are audited at most hourly" },
                });
                }
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            hostId = host.Id;
        }

        using var ws = await http.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        var session = new AgentSession(hostId, thumbprint, ws);
        if (_sessions.TryGetValue(hostId, out var previous))
        {
            previous.Abort("superseded by a new connection");
        }
        _sessions[hostId] = session;
        _logger.LogInformation("Agent for host {HostId} connected from {Ip}", hostId, http.Connection.RemoteIpAddress);
        await TouchHostAsync(hostId, null);
        await _audit.WriteAsync(new AuditRecord
        {
            Action = "agent.connected",
            AuthMethod = "agent-mtls",
            SourceIp = http.Connection.RemoteIpAddress?.ToString(),
            ResourceType = "host",
            ResourceId = hostId.ToString("D"),
        });
        await _notifier.HostChangedAsync(hostId);

        try
        {
            await ReceiveLoopAsync(session, http.RequestAborted);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
        {
            _logger.LogInformation("Agent channel for host {HostId} closed: {Message}", hostId, ex.Message);
        }
        finally
        {
            _sessions.TryRemove(new KeyValuePair<Guid, AgentSession>(hostId, session));
            session.FailPending(new AgentChannelLostException(hostId));
            await _notifier.HostChangedAsync(hostId);
        }
    }

    public async Task<AgentCommandResult> SendAsync(Guid hostId, AgentCommand command, Action<AgentCommandProgress>? onProgress,
        TimeSpan timeout, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(hostId, out var session))
        {
            throw new HostNotConnectedException(hostId);
        }
        // Older agents cannot even parse newer command kinds (and would drop the channel): refuse locally, nothing is sent.
        if (command.Kind is AgentCommandKind.HyperVOperation or AgentCommandKind.ConsoleFrame or AgentCommandKind.ConsoleInput && session.ProtocolVersion < 2)
        {
            return new AgentCommandResult
            {
                CommandId = command.CommandId,
                Status = AgentCommandStatus.Rejected,
                Message = "The agent on this host does not support this operation (agent protocol " + session.ProtocolVersion + "). Update the agent to 0.3 or later.",
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
        if (_sessions.TryRemove(hostId, out var s))
        {
            s.Abort(reason);
        }
    }

    /// <summary>Drops sessions whose certificate was revoked or expired (defense in depth besides explicit Disconnect).</summary>
    public async Task EnforceCertificateValidityAsync(CancellationToken ct)
    {
        if (_sessions.IsEmpty)
        {
            return;
        }
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
        var now = _time.GetUtcNow();
        foreach (var (hostId, session) in _sessions)
        {
            var ok = await db.AgentCertificates.AsNoTracking().AnyAsync(c => c.ThumbprintSha256 == session.Thumbprint && c.RevokedAt == null && c.NotAfter > now, ct)
                     && await db.Hosts.AsNoTracking().AnyAsync(h => h.Id == hostId && h.Status == HostStatus.Active, ct);
            if (!ok)
            {
                Disconnect(hostId, "certificate no longer valid");
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
                _logger.LogWarning("Malformed message from host {HostId}", session.HostId);
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
                await TouchHostAsync(session.HostId, hello.AgentVersion);
                break;

            case AgentMessageType.Inventory when env.Read<HostInventory>() is { } inv:
                using (var scope = _scopes.CreateScope())
                {
                    var ingestor = scope.ServiceProvider.GetRequiredService<InventoryIngestor>();
                    await ingestor.ApplyAsync(session.HostId, inv, ct);
                }
                await _notifier.HostChangedAsync(session.HostId);
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

    private async Task RenewAsync(AgentSession session, RenewCertificateRequest renew, CancellationToken ct)
    {
        try
        {
            var issued = _ca.IssueAgentCertificate(renew.CsrPem, session.HostId, TimeSpan.FromDays(_options.Security.AgentCertificateDays));
            using (var scope = _scopes.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
                db.AgentCertificates.Add(new AgentCertificate
                {
                    HostId = session.HostId,
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
                ResourceType = "host",
                ResourceId = session.HostId.ToString("D"),
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
            host.AgentVersion = agentVersion.Length > 32 ? agentVersion[..32] : agentVersion;
        }
        await db.SaveChangesAsync();

        // Revoke certificates older than the one in use once the agent has proven possession of a newer one.
        if (_sessions.TryGetValue(hostId, out var s))
        {
            var current = await db.AgentCertificates.AsNoTracking().FirstOrDefaultAsync(c => c.ThumbprintSha256 == s.Thumbprint);
            if (current is not null)
            {
                var now = _time.GetUtcNow();
                await db.AgentCertificates
                    .Where(c => c.HostId == hostId && c.RevokedAt == null && c.Id != current.Id && c.IssuedAt < current.IssuedAt)
                    .ExecuteUpdateAsync(u => u.SetProperty(c => c.RevokedAt, now).SetProperty(c => c.RevokedReason, "superseded"));
            }
        }
    }

    private sealed class AgentSession(Guid hostId, string thumbprint, WebSocket socket)
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly ConcurrentDictionary<Guid, (TaskCompletionSource<AgentCommandResult> Tcs, Action<AgentCommandProgress>? Progress)> _pending = new();

        public Guid HostId { get; } = hostId;
        public string Thumbprint { get; } = thumbprint;
        /// <summary>From the agent's Hello; 0 until it arrives.</summary>
        public int ProtocolVersion { get; set; }
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
            FailPending(new AgentChannelLostException(HostId));
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
