using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using HvPortal.Agent.Commands;
using HvPortal.Agent.HyperV;
using HvPortal.Agent.Identity;
using HvPortal.Contracts.Agent;

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
/// </summary>
public sealed class GatewayChannelService : BackgroundService
{
    private readonly AgentIdentityStore _store;
    private readonly CommandExecutor _executor;
    private readonly IHyperVHost _host;
    private readonly AgentRuntimeOptions _options;
    private readonly ILogger<GatewayChannelService> _logger;
    private readonly IHostApplicationLifetime _lifetime;

    public GatewayChannelService(AgentIdentityStore store, CommandExecutor executor, IHyperVHost host,
        Microsoft.Extensions.Options.IOptions<AgentRuntimeOptions> options, ILogger<GatewayChannelService> logger,
        IHostApplicationLifetime lifetime)
    {
        _store = store;
        _executor = executor;
        _host = host;
        _options = options.Value;
        _logger = logger;
        _lifetime = lifetime;
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
                var inv = await _host.CollectInventoryAsync(ct);
                await sender.SendAsync(AgentEnvelope.Create(AgentMessageType.Inventory, null, inv), ct);
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

    private async Task HandleAsync(AgentEnvelope env, ChannelSender sender, CancellationToken ct)
    {
        switch (env.Type)
        {
            case AgentMessageType.Ping:
                await sender.SendAsync(new AgentEnvelope(AgentMessageType.Pong, env.CorrelationId, null), ct);
                break;

            case AgentMessageType.Command when env.Read<AgentCommand>() is { } cmd:
                _ = Task.Run(async () =>
                {
                    var progress = new Progress<AgentCommandProgress>(p =>
                        _ = sender.SendAsync(AgentEnvelope.Create(AgentMessageType.CommandProgress, env.CorrelationId, p), ct));
                    var result = await _executor.ExecuteAsync(cmd, progress, ct);
                    await sender.SendAsync(AgentEnvelope.Create(AgentMessageType.CommandResult, env.CorrelationId, result), ct);
                    if (cmd.Kind == AgentCommandKind.VmPower)
                    {
                        // Push a fresh inventory so the portal reflects the change without waiting for the next cycle.
                        var inv = await _host.CollectInventoryAsync(ct);
                        await sender.SendAsync(AgentEnvelope.Create(AgentMessageType.Inventory, null, inv), ct);
                    }
                }, ct);
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
