using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using HvPortal.Agent.Commands;
using HvPortal.Agent.HyperV;
using HvPortal.Agent.Identity;
using HvPortal.Contracts.Agent;
using HvPortal.Contracts.HyperV;

namespace HvPortal.Agent.Channel;

public sealed class AgentRuntimeOptions
{
    public int InventoryIntervalSeconds { get; set; } = 30;
    public int ReconnectMaxDelaySeconds { get; set; } = 60;
}

/// <summary>
/// Maintains the outbound mTLS WebSocket to the gateway (agent-initiated: the host needs no inbound port).
/// Reconnects with capped exponential backoff + jitter. Renews the client certificate when a third of its
/// lifetime remains.
/// Role Management (protocol 6): no local Hyper-V; the gateway sends the remote hosts to manage (TargetsConfiguration,
/// kept in memory for the life of the channel), every command names its target host and inventories are sent per host
/// (TargetInventory). A host agent refuses commands that name a target, a management agent refuses commands without one.
/// </summary>
public sealed class GatewayChannelService : BackgroundService
{
    private readonly AgentIdentityStore _store;
    private readonly CommandExecutor _executor;
    private readonly IHyperVHost _host;
    private readonly AgentRuntimeOptions _options;
    private readonly ILogger<GatewayChannelService> _logger;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly RemoteTargetRegistry _targets;
    private readonly AgentUpdater _updater;
    private AgentRole _role = AgentRole.Host;

    public GatewayChannelService(AgentIdentityStore store, CommandExecutor executor, IHyperVHost host,
        Microsoft.Extensions.Options.IOptions<AgentRuntimeOptions> options, ILogger<GatewayChannelService> logger,
        IHostApplicationLifetime lifetime, RemoteTargetRegistry targets, AgentUpdater updater)
    {
        _store = store;
        _executor = executor;
        _host = host;
        _options = options.Value;
        _logger = logger;
        _lifetime = lifetime;
        _targets = targets;
        _updater = updater;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var state = _store.LoadState();
        if (state is null || !_store.IsEnrolled)
        {
            _logger.LogCritical("Agent is not enrolled. Run 'HvPortal.Agent.exe enroll --gateway <url> --token <token> --ca-sha256 <fingerprint>' as administrator.");
            _lifetime.StopApplication();
            return;
        }
        _role = Enum.IsDefined(state.Role) ? state.Role : AgentRole.Host;
        if (_role == AgentRole.Management)
        {
            _logger.LogInformation("Management agent: remote hosts are assigned by the gateway");
        }

        var attempt = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var cert = _store.LoadCertificate() ?? throw new InvalidOperationException("Agent identity missing.");
                if (cert.NotAfter.ToUniversalTime() < DateTime.UtcNow)
                {
                    _logger.LogCritical("Agent certificate expired on {NotAfter:u}. Re-enroll the agent.", cert.NotAfter.ToUniversalTime());
                    _lifetime.StopApplication();
                    return;
                }
                using var ca = X509Certificate2.CreateFromPem(state.CaCertificatePem);
                await RunSessionAsync(state, cert, ca, () => attempt = 0, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Gateway channel error: {Message}", ex.Message);
            }

            attempt++;
            var delay = Math.Min(_options.ReconnectMaxDelaySeconds, Math.Pow(2, Math.Min(attempt, 6)));
            var jitter = Random.Shared.NextDouble() * 0.3 * delay;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delay + jitter), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RunSessionAsync(AgentState state, X509Certificate2 cert, X509Certificate2 ca, Action onConnected, CancellationToken stoppingToken)
    {
        using var ws = new ClientWebSocket();
        ws.Options.ClientCertificates.Add(cert);
        ws.Options.RemoteCertificateValidationCallback = GatewayTrust.CreateValidator(ca);
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        var uri = new UriBuilder(state.GatewayUrl) { Scheme = "wss", Path = AgentProtocol.ChannelPath }.Uri;

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        connectCts.CancelAfter(TimeSpan.FromSeconds(30));
        await ws.ConnectAsync(uri, connectCts.Token);
        _logger.LogInformation("Connected to gateway {Gateway} as host {HostId}", state.GatewayUrl, state.HostId);
        onConnected();

        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _renew.PendingKey?.Dispose();
        _renew.PendingKey = null;
        var sender = new ChannelSender(ws);

        await sender.SendAsync(AgentEnvelope.Create(AgentMessageType.Hello, null, new AgentHello
        {
            ProtocolVersion = AgentProtocol.Version,
            AgentVersion = typeof(GatewayChannelService).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            MachineName = Environment.MachineName,
            OsVersion = Environment.OSVersion.VersionString,
            Role = _role,
            UpdaterInstalled = AgentUpdater.UpdaterInstalled(),
            LastUpdateResult = AgentUpdater.LastResult(_store.DataDirectory),
        }), sessionCts.Token);

        var inventoryLoop = InventoryLoopAsync(sender, sessionCts.Token);
        var renewLoop = RenewalLoopAsync(sender, cert, sessionCts.Token);
        try
        {
            await ReceiveLoopAsync(ws, sender, sessionCts.Token);
        }
        finally
        {
            await sessionCts.CancelAsync();
            await Task.WhenAll(Swallow(inventoryLoop), Swallow(renewLoop));
            // Remote credentials live only as long as the channel: the gateway sends them again after reconnecting.
            _targets.Clear();
            if (ws.State == WebSocketState.Open)
            {
                try
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "agent stopping", CancellationToken.None);
                }
                catch (WebSocketException)
                {
                }
            }
        }
    }

    private static async Task Swallow(Task t)
    {
        try
        {
            await t;
        }
        catch (Exception)
        {
        }
    }

    private async Task InventoryLoopAsync(ChannelSender sender, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(_options.InventoryIntervalSeconds, 5, 3600));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_role == AgentRole.Management)
                {
                    await Parallel.ForEachAsync(_targets.All, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                        async (target, token) => await SendTargetInventoryAsync(sender, target, token));
                }
                else
                {
                    var inv = await _host.CollectInventoryAsync(ct);
                    await sender.SendAsync(AgentEnvelope.Create(AgentMessageType.Inventory, null, inv), ct);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Inventory collection failed");
            }
            await Task.Delay(interval, ct);
        }
    }

    /// <summary>
    /// Inventory of one remote host. A host that cannot be reached still produces a report (Access = Error, no VMs):
    /// the gateway then keeps the last known VMs and shows the reason, instead of a silent stale host.
    /// </summary>
    private async Task SendTargetInventoryAsync(ChannelSender sender, RemoteTargetContext target, CancellationToken ct)
    {
        HostInventory inv;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            inv = await target.Host.CollectInventoryAsync(timeout.Token).WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Inventory of remote host {Host} failed: {Message}", target.Target.Display, ex.Message);
            inv = new HostInventory
            {
                CollectedAtUtc = DateTimeOffset.UtcNow,
                Access = HyperVAccessStatus.Error,
                AccessMessage = ex is OperationCanceledException ? $"No answer from {target.Target.Display} within 2 minutes." : $"{target.Target.Display}: {ex.Message}",
                Host = new HostFacts
                {
                    ComputerName = target.Target.Display,
                    AgentVersion = typeof(GatewayChannelService).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                },
                Partial = true,
            };
        }
        await sender.SendAsync(AgentEnvelope.Create(AgentMessageType.TargetInventory, null, new TargetInventory { HostId = target.HostId, Inventory = inv }), ct);
    }

    /// <summary>Executor and Hyper-V host for a command, or the reason it is refused.</summary>
    internal static (CommandExecutor? Executor, IHyperVHost? Host, RemoteTargetContext? Target, string? Error) Route(
        AgentRole role, AgentCommand command, CommandExecutor localExecutor, IHyperVHost localHost, RemoteTargetRegistry targets)
    {
        if (role == AgentRole.Host)
        {
            return command.TargetHostId is null
                ? (localExecutor, localHost, null, null)
                : (null, null, null, "This agent manages only the host it runs on.");
        }
        if (command.TargetHostId is not { } id || id == Guid.Empty)
        {
            return (null, null, null, "A management agent needs the target host of the command.");
        }
        return targets.TryGet(id, out var ctx)
            ? (ctx.Executor, ctx.Host, ctx, null)
            : (null, null, null, "This remote host is not configured on the management agent (yet).");
    }

    private readonly RenewState _renew = new();

    private sealed class RenewState
    {
        public System.Security.Cryptography.ECDsa? PendingKey;
    }

    private async Task RenewalLoopAsync(ChannelSender sender, X509Certificate2 cert, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var lifetime = cert.NotAfter.ToUniversalTime() - cert.NotBefore.ToUniversalTime();
            var remaining = cert.NotAfter.ToUniversalTime() - DateTime.UtcNow;
            if (remaining < lifetime / 3 && _renew.PendingKey is null)
            {
                var (key, csr) = AgentIdentityStore.CreateKeyAndCsr(Environment.MachineName);
                _renew.PendingKey = key;
                _logger.LogInformation("Requesting certificate renewal ({Remaining} remaining)", remaining);
                await sender.SendAsync(AgentEnvelope.Create(AgentMessageType.RenewCertificate, null, new RenewCertificateRequest { CsrPem = csr }), ct);
            }
            await Task.Delay(TimeSpan.FromHours(1), ct);
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, ChannelSender sender, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(buffer, ct);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogInformation("Gateway closed the channel: {Status} {Description}", r.CloseStatus, r.CloseStatusDescription);
                    return;
                }
                ms.Write(buffer, 0, r.Count);
                if (ms.Length > AgentProtocol.MaxMessageBytes)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.MessageTooBig, "message too big", CancellationToken.None);
                    return;
                }
            } while (!r.EndOfMessage);

            if (r.MessageType != WebSocketMessageType.Text)
            {
                continue;
            }
            AgentEnvelope? env;
            try
            {
                env = JsonSerializer.Deserialize<AgentEnvelope>(ms.ToArray(), AgentProtocol.Json);
            }
            catch (JsonException)
            {
                _logger.LogWarning("Discarding malformed message from gateway");
                continue;
            }
            if (env is null)
            {
                continue;
            }
            await HandleAsync(env, sender, ct);
        }
    }

    internal static AgentCommand? TryReadCommand(AgentEnvelope env)
    {
        try
        {
            return env.Read<AgentCommand>();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    private async Task HandleAsync(AgentEnvelope env, ChannelSender sender, CancellationToken ct)
    {
        switch (env.Type)
        {
            case AgentMessageType.Ping:
                await sender.SendAsync(new AgentEnvelope(AgentMessageType.Pong, env.CorrelationId, null), ct);
                break;

            case AgentMessageType.Command:
                if (TryReadCommand(env) is not { } cmd)
                {
                    // A command this agent cannot parse (e.g. an operation added in a newer protocol): refuse it, keep the channel.
                    _logger.LogWarning("Rejecting a command this agent version cannot read");
                    if (Guid.TryParse(env.CorrelationId, out var unknownId))
                    {
                        await sender.SendAsync(AgentEnvelope.Create(AgentMessageType.CommandResult, env.CorrelationId, new AgentCommandResult
                        {
                            CommandId = unknownId,
                            Status = AgentCommandStatus.Rejected,
                            Message = $"Command not supported by agent protocol {AgentProtocol.Version}. Update the agent.",
                        }), ct);
                    }
                    break;
                }
                if (cmd.Kind == AgentCommandKind.UpdateAgent)
                {
                    // About this agent itself (host or management agent): never routed to a remote host.
                    _ = Task.Run(async () =>
                    {
                        var result = cmd.TargetHostId is null
                            ? await _updater.RunAsync(cmd, ct)
                            : new AgentCommandResult { CommandId = cmd.CommandId, Status = AgentCommandStatus.Rejected, Message = "An agent update is not for a remote host." };
                        await sender.SendAsync(AgentEnvelope.Create(AgentMessageType.CommandResult, env.CorrelationId, result), ct);
                    }, ct);
                    break;
                }
                var (executor, host, target, refusal) = Route(_role, cmd, _executor, _host, _targets);
                if (refusal is not null)
                {
                    _logger.LogWarning("Rejecting command {CommandId}: {Reason}", cmd.CommandId, refusal);
                    await sender.SendAsync(AgentEnvelope.Create(AgentMessageType.CommandResult, env.CorrelationId, new AgentCommandResult
                    {
                        CommandId = cmd.CommandId,
                        Status = AgentCommandStatus.Rejected,
                        Message = refusal,
                    }), ct);
                    break;
                }
                _ = Task.Run(async () =>
                {
                    var progress = new Progress<AgentCommandProgress>(p =>
                        _ = sender.SendAsync(AgentEnvelope.Create(AgentMessageType.CommandProgress, env.CorrelationId, p), ct));
                    var result = await executor!.ExecuteAsync(cmd, progress, ct);
                    await sender.SendAsync(AgentEnvelope.Create(AgentMessageType.CommandResult, env.CorrelationId, result), ct);
                    if (CommandExecutor.IsMutating(cmd))
                    {
                        // Push a fresh inventory so the portal reflects the change without waiting for the next cycle.
                        if (target is not null)
                        {
                            await SendTargetInventoryAsync(sender, target, ct);
                        }
                        else
                        {
                            var inv = await host!.CollectInventoryAsync(ct);
                            await sender.SendAsync(AgentEnvelope.Create(AgentMessageType.Inventory, null, inv), ct);
                        }
                    }
                }, ct);
                break;

            case AgentMessageType.TargetsConfiguration when _role == AgentRole.Management && env.Read<TargetsConfiguration>() is { } config:
                _targets.Apply(config);
                // First inventory of new targets right away (the regular loop may be up to one interval away).
                _ = Task.Run(async () =>
                {
                    foreach (var t in _targets.All)
                    {
                        await SendTargetInventoryAsync(sender, t, ct);
                    }
                }, ct);
                break;

            case AgentMessageType.TargetsConfiguration:
                _logger.LogWarning("Ignoring a remote host configuration: this agent is enrolled as a host agent");
                break;

            case AgentMessageType.RenewCertificateResult when env.Read<RenewCertificateResponse>() is { } renewed && _renew.PendingKey is { } key:
                _store.SaveIdentity(AgentIdentityStore.CombineToPfx(renewed.CertificatePem, key));
                key.Dispose();
                _renew.PendingKey = null;
                _logger.LogInformation("Certificate renewed; reconnecting with the new certificate");
                throw new OperationCanceledException("reconnect after renewal");

            case AgentMessageType.Error:
                _logger.LogWarning("Gateway reported an error: {Payload}", env.Payload?.GetRawText());
                break;
        }
    }

    private sealed class ChannelSender
    {
        private readonly WebSocket _ws;
        private readonly SemaphoreSlim _lock = new(1, 1);

        public ChannelSender(WebSocket ws) => _ws = ws;

        public async Task SendAsync(AgentEnvelope env, CancellationToken ct)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(env, AgentProtocol.Json);
            await _lock.WaitAsync(ct);
            try
            {
                if (_ws.State == WebSocketState.Open)
                {
                    await _ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
                }
            }
            finally
            {
                _lock.Release();
            }
        }
    }
}
