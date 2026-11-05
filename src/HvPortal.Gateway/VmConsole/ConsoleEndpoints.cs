using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using HvPortal.Contracts.Agent;
using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Agents;
using HvPortal.Gateway.Api;
using HvPortal.Gateway.Audit;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Security;
using Microsoft.EntityFrameworkCore;

namespace HvPortal.Gateway.VmConsole;

/// <summary>
/// Hyper-V VM console in the browser (not RDP): the screen comes from the hypervisor (works before the guest OS boots),
/// keyboard and mouse go to the VM's emulated/synthetic devices through typed agent commands.
///
/// Flow: POST .../console/tickets (vm.console) -> one-time ticket (30 s) -> WebSocket .../console/ws on the same origin,
/// first message {"t":"auth","ticket":"..."}. The socket requires the session cookie and an Origin equal to the portal.
/// While open: permission and session are re-checked every 60 s, idle timeout 15 min (no input), hard limit 8 h.
/// Open and close are audited.
/// </summary>
public static class ConsoleEndpoints
{
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(8);
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(60);

    public static void Map(WebApplication app)
    {
        app.MapPost("/api/v1/vms/{id:guid}/console/tickets", IssueTicketAsync).WithTags("Console");
        // HTTP/1.1 WebSockets use GET, HTTP/2 WebSockets (RFC 8441, used by browsers over h2) use CONNECT.
        app.MapMethods("/api/v1/vms/{id:guid}/console/ws", ["GET", "CONNECT"], ConnectAsync).WithTags("Console").ExcludeFromDescription();
        app.MapGet("/api/v1/console/sessions", ListSessionsAsync).WithTags("Console");
    }

    private static async Task<IResult> IssueTicketAsync(Guid id, HttpContext http, PortalDbContext db, AccessEvaluator access, IAgentDispatcher agents,
        ConsoleTicketStore tickets, ConsoleSessionRegistry registry)
    {
        var uid = http.User.UserId();
        var vm = await db.VirtualMachines.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id, http.RequestAborted);
        if (vm is null || !await access.HasAsync(uid, Permissions.InventoryRead, ResourceRef.Vm(id), http.RequestAborted))
        {
            return ApiSupport.NotFound();
        }
        if (await http.GuardAsync(Permissions.VmConsole, ResourceRef.Vm(id), "vm.console.ticket") is { } denied)
        {
            return denied;
        }
        if (vm.IsMissing)
        {
            return ApiSupport.Conflict("vm_missing");
        }
        if (!agents.IsConnected(vm.HostId))
        {
            return ApiSupport.Error(StatusCodes.Status503ServiceUnavailable, "host_unreachable", "The host agent is not connected.");
        }
        if (registry.CountForUser(uid) >= ConsoleSessionRegistry.MaxPerUser)
        {
            return ApiSupport.Conflict("too_many_consoles", $"At most {ConsoleSessionRegistry.MaxPerUser} console sessions per user.");
        }
        var (token, expires) = tickets.Issue(uid, http.User.SessionId(), id);
        return Results.Ok(new { ticket = token, expiresAt = expires, path = $"/api/v1/vms/{id}/console/ws" });
    }

    private static async Task<IResult> ListSessionsAsync(HttpContext http, ConsoleSessionRegistry registry)
    {
        if (await http.GuardAsync(Permissions.AuditRead, ResourceRef.Global, "console.sessions.list") is { } denied)
        {
            return denied;
        }
        return Results.Ok(registry.All.Select(e => new { e.Id, e.UserId, e.UserName, e.VmId, e.VmName, e.StartedAt }));
    }

    private static async Task ConnectAsync(Guid id, HttpContext http, PortalDbContext db, AccessEvaluator access, IAgentDispatcher agents,
        ConsoleTicketStore tickets, ConsoleSessionRegistry registry, IServiceScopeFactory scopes, AuditLog audit, TimeProvider time,
        ILoggerFactory loggers)
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            await ApiSupport.BadRequest("websocket_required").ExecuteAsync(http);
            return;
        }
        // Browsers always send Origin on WebSocket handshakes: require it (the cross-site middleware already rejects mismatches).
        var origin = http.Request.Headers.Origin.ToString();
        if (origin.Length == 0)
        {
            await ApiSupport.Forbidden("origin_required").ExecuteAsync(http);
            return;
        }
        var uid = http.User.UserId();
        var sid = http.User.SessionId();
        var vm = await db.VirtualMachines.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id, http.RequestAborted);
        if (vm is null)
        {
            await ApiSupport.NotFound().ExecuteAsync(http);
            return;
        }

        using var ws = await http.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        var log = loggers.CreateLogger("HvPortal.Console");

        // First message: the one-time ticket.
        var first = await ReceiveTextAsync(ws, 4096, TimeSpan.FromSeconds(10), http.RequestAborted);
        string? ticketValue = null;
        try
        {
            using var doc = first is null ? null : JsonDocument.Parse(first);
            if (doc?.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("ticket", out var t) && t.ValueKind == JsonValueKind.String)
            {
                ticketValue = t.GetString();
            }
        }
        catch (JsonException)
        {
        }
        var ticket = tickets.Redeem(ticketValue, uid, sid, id);
        if (ticket is null || !await access.HasAsync(uid, Permissions.VmConsole, ResourceRef.Vm(id), http.RequestAborted))
        {
            await audit.WriteAsync(http.Audit("vm.console.open", AuditOutcome.Denied, "vm", id.ToString("D"), new { reason = ticket is null ? "invalid ticket" : "permission" }));
            await CloseAsync(ws, WebSocketCloseStatus.PolicyViolation, "unauthorized");
            return;
        }

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        var entry = new ConsoleSessionRegistry.Entry(Guid.NewGuid(), uid, http.User.Identity?.Name, id, vm.Name, time.GetUtcNow(), lifetime);
        if (!registry.TryAdd(entry))
        {
            await CloseAsync(ws, WebSocketCloseStatus.PolicyViolation, "too many console sessions");
            return;
        }
        await audit.WriteAsync(http.Audit("vm.console.open", AuditOutcome.Success, "vm", id.ToString("D"), new { consoleSession = entry.Id, vm = vm.Name }));
        using var session = new ConsoleRelay(ws, vm, uid, sid, agents, scopes, time, log);
        var reason = "closed";
        try
        {
            reason = await session.RunAsync(lifetime.Token);
        }
        finally
        {
            registry.Remove(entry.Id);
            await audit.WriteAsync(http.Audit("vm.console.close", AuditOutcome.Success, "vm", id.ToString("D"),
                new { consoleSession = entry.Id, reason, seconds = (int)(time.GetUtcNow() - entry.StartedAt).TotalSeconds }));
            await CloseAsync(ws, WebSocketCloseStatus.NormalClosure, reason);
        }
    }

    internal static async Task<string?> ReceiveTextAsync(WebSocket ws, int maxBytes, TimeSpan? timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t)
        {
            cts.CancelAfter(t);
        }
        var buffer = new byte[Math.Min(maxBytes, 8192)];
        using var ms = new MemoryStream();
        try
        {
            while (true)
            {
                var r = await ws.ReceiveAsync(buffer, cts.Token);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }
                if (r.MessageType != WebSocketMessageType.Text)
                {
                    return null;
                }
                ms.Write(buffer, 0, r.Count);
                if (ms.Length > maxBytes)
                {
                    return null;
                }
                if (r.EndOfMessage)
                {
                    return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            return null;
        }
    }

    internal static async Task CloseAsync(WebSocket ws, WebSocketCloseStatus status, string reason)
    {
        try
        {
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await ws.CloseOutputAsync(status, reason.Length > 100 ? reason[..100] : reason, cts.Token);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }
}

/// <summary>
/// One console connection: frame pump (agent -> browser, binary), input pump (browser -> agent, typed and validated),
/// and a watchdog (permission/session re-check, idle and maximum duration).
///
/// Binary frame message (little-endian): [0]=1 (frame) [1]=flags (bit0 synthetic mouse available)
/// [2..3]=width [4..5]=height [6..7]=guest width [8..9]=guest height, then zlib-compressed RGB565 pixels.
/// Text messages from the gateway: {"t":"status","state":"running|off|host_offline|unsupported|error","message":...}.
/// </summary>
internal sealed class ConsoleRelay(WebSocket ws, VirtualMachine vm, Guid userId, Guid sessionId, IAgentDispatcher agents,
    IServiceScopeFactory scopes, TimeProvider time, ILogger log) : IDisposable
{
    public void Dispose() => _sendLock.Dispose();

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Channel<ConsoleInput> _inputs = Channel.CreateBounded<ConsoleInput>(new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Stopwatch _sinceInput = Stopwatch.StartNew();
    private volatile bool _paused;
    private volatile bool _forceFull = true;
    private int _maxWidth = 1920;
    private int _maxHeight = 1200;
    private string? _lastState;
    private string _endReason = "closed";

    public async Task<string> RunAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tasks = new[]
        {
            ReceiveLoopAsync(cts.Token),
            FramePumpAsync(cts.Token),
            InputPumpAsync(cts.Token),
            WatchdogAsync(cts.Token),
        };
        await Task.WhenAny(tasks);
        await cts.CancelAsync();
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Console relay for VM {VmId} failed", vm.Id);
            _endReason = "error";
        }
        return ct.IsCancellationRequested && _endReason == "closed" ? "terminated" : _endReason;
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            var text = await ConsoleEndpoints.ReceiveTextAsync(ws, 4096, null, ct);
            if (text is null)
            {
                _endReason = "client closed";
                return;
            }
            try
            {
                using var doc = JsonDocument.Parse(text);
                Handle(doc.RootElement);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException)
            {
                // Malformed input is ignored (never forwarded).
            }
        }
    }

    private void Handle(JsonElement m)
    {
        if (m.ValueKind != JsonValueKind.Object || !m.TryGetProperty("t", out var tEl) || tEl.ValueKind != JsonValueKind.String)
        {
            return;
        }
        var input = tEl.GetString() switch
        {
            "k" when m.TryGetProperty("c", out var c) && c.ValueKind == JsonValueKind.Array && c.GetArrayLength() is > 0 and <= 32 =>
                new ConsoleInput { VmId = vm.HyperVId, Kind = ConsoleInputKind.Scancodes, Scancodes = c.EnumerateArray().Select(x => checked((byte)x.GetInt32())).ToArray() },
            "m" => new ConsoleInput { VmId = vm.HyperVId, Kind = ConsoleInputKind.MouseMove, X = Clamp(m, "x"), Y = Clamp(m, "y") },
            "b" when m.TryGetProperty("b", out var b) && b.GetInt32() is >= 1 and <= 3 && m.TryGetProperty("d", out var d) =>
                new ConsoleInput { VmId = vm.HyperVId, Kind = ConsoleInputKind.MouseButton, Button = b.GetInt32(), Down = d.GetBoolean() },
            "cad" => new ConsoleInput { VmId = vm.HyperVId, Kind = ConsoleInputKind.CtrlAltDel },
            "type" when m.TryGetProperty("s", out var s) && s.GetString() is { Length: > 0 and <= 512 } str && str.All(ch => ch is >= ' ' and <= '~' or '\n' or '\t') =>
                new ConsoleInput { VmId = vm.HyperVId, Kind = ConsoleInputKind.TypeText, Text = str },
            _ => null,
        };
        switch (tEl.GetString())
        {
            case "size":
                _maxWidth = Math.Clamp(Clamp(m, "w"), 320, 1920);
                _maxHeight = Math.Clamp(Clamp(m, "h"), 200, 1200);
                _forceFull = true;
                return;
            case "refresh":
                _forceFull = true;
                return;
            case "pause":
                _paused = true;
                return;
            case "resume":
                _paused = false;
                _forceFull = true;
                return;
        }
        if (input is not null)
        {
            _sinceInput.Restart();
            _inputs.Writer.TryWrite(input);
        }
    }

    private static int Clamp(JsonElement m, string name) =>
        m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? Math.Clamp(i, 0, 16384) : 0;

    private async Task InputPumpAsync(CancellationToken ct)
    {
        var reader = _inputs.Reader;
        while (await reader.WaitToReadAsync(ct))
        {
            while (reader.TryRead(out var input))
            {
                // Coalesce mouse moves: only the latest position matters.
                if (input.Kind == ConsoleInputKind.MouseMove && reader.TryPeek(out var next) && next.Kind == ConsoleInputKind.MouseMove)
                {
                    continue;
                }
                try
                {
                    var r = await agents.SendAsync(vm.HostId, new AgentCommand
                    {
                        CommandId = Guid.NewGuid(),
                        Kind = AgentCommandKind.ConsoleInput,
                        Input = input,
                        TimeoutSeconds = 10,
                    }, null, TimeSpan.FromSeconds(10), ct);
                    if (r.Status is not (AgentCommandStatus.Succeeded))
                    {
                        log.LogDebug("Console input {Kind} for VM {VmId}: {Status} {Message}", input.Kind, vm.Id, r.Status, r.Message);
                    }
                }
                catch (Exception ex) when (ex is HostNotConnectedException or AgentChannelLostException or TimeoutException)
                {
                    await SendStatusAsync("host_offline", null, ct);
                }
            }
        }
    }

    private async Task FramePumpAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromMilliseconds(120);
        var unchanged = 0;
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            if (_paused)
            {
                await Task.Delay(300, ct);
                continue;
            }
            if (!agents.IsConnected(vm.HostId))
            {
                await SendStatusAsync("host_offline", null, ct);
                await Task.Delay(2000, ct);
                continue;
            }
            var started = Stopwatch.GetTimestamp();
            AgentCommandResult r;
            var force = _forceFull;
            try
            {
                r = await agents.SendAsync(vm.HostId, new AgentCommand
                {
                    CommandId = Guid.NewGuid(),
                    Kind = AgentCommandKind.ConsoleFrame,
                    FrameRequest = new ConsoleFrameRequest(vm.HyperVId, _maxWidth, _maxHeight, force),
                    TimeoutSeconds = 20,
                }, null, TimeSpan.FromSeconds(20), ct);
            }
            catch (Exception ex) when (ex is HostNotConnectedException or AgentChannelLostException or TimeoutException)
            {
                await SendStatusAsync("host_offline", null, ct);
                await Task.Delay(2000, ct);
                continue;
            }

            if (r.Status == AgentCommandStatus.Succeeded && r.Frame is { } f)
            {
                await SendStatusAsync("running", null, ct, f.MouseAvailable);
                if (!f.Unchanged && f.Data is { Length: > 0 } data)
                {
                    if (force)
                    {
                        _forceFull = false;
                    }
                    await SendFrameAsync(f, Convert.FromBase64String(data), ct);
                    unchanged = 0;
                }
                else
                {
                    unchanged++;
                }
                // Adaptive rate: fast while the screen changes or the user types, slower on a static screen.
                var target = _sinceInput.Elapsed < TimeSpan.FromSeconds(2) || unchanged < 5 ? interval : TimeSpan.FromMilliseconds(400);
                var elapsed = Stopwatch.GetElapsedTime(started);
                if (target > elapsed)
                {
                    await Task.Delay(target - elapsed, ct);
                }
            }
            else if (r.Status == AgentCommandStatus.InvalidState)
            {
                _forceFull = true;
                await SendStatusAsync("off", r.Message, ct);
                await Task.Delay(1000, ct);
            }
            else if (r.Status == AgentCommandStatus.Rejected)
            {
                await SendStatusAsync("unsupported", "The agent on this host does not support the console. Update the agent.", ct);
                _endReason = "agent unsupported";
                return;
            }
            else
            {
                await SendStatusAsync(r.Status == AgentCommandStatus.NotFound ? "not_found" : "error", r.Message, ct);
                await Task.Delay(2000, ct);
            }
        }
    }

    private async Task WatchdogAsync(CancellationToken ct)
    {
        var started = time.GetUtcNow();
        var lastTouch = DateTimeOffset.MinValue;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(ConsoleEndpoints.RecheckInterval, time, ct);
            var now = time.GetUtcNow();
            if (now - started > ConsoleEndpoints.MaxDuration)
            {
                _endReason = "maximum duration reached";
                await SendStatusAsync("closed", "The console reached its maximum duration (8 hours).", ct);
                return;
            }
            if (_sinceInput.Elapsed > ConsoleEndpoints.IdleTimeout)
            {
                _endReason = "idle timeout";
                await SendStatusAsync("closed", "The console was closed after 15 minutes without input.", ct);
                return;
            }
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
            var access = scope.ServiceProvider.GetRequiredService<AccessEvaluator>();
            var settings = await scope.ServiceProvider.GetRequiredService<SettingsStore>().GetSecurityAsync(ct);
            var session = await db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId, ct);
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
            var activeRecently = _sinceInput.Elapsed < ConsoleEndpoints.RecheckInterval;
            var idleOk = session is not null && (activeRecently || now - session.LastSeenAt <= TimeSpan.FromMinutes(settings.SessionIdleMinutes));
            var valid = session is not null && user is not null && user.IsEnabled && session.RevokedAt is null && session.ExpiresAt > now && idleOk
                        && Tokens.FixedTimeEqualsHex(user.SecurityStamp, session.SecurityStamp)
                        && await db.VirtualMachines.AsNoTracking().AnyAsync(v => v.Id == vm.Id, ct)
                        && await access.HasAsync(userId, Permissions.VmConsole, ResourceRef.Vm(vm.Id), ct);
            if (!valid)
            {
                _endReason = "authorization revoked or session ended";
                await SendStatusAsync("closed", "Access to this console is no longer authorized.", ct);
                return;
            }
            // Console activity keeps the portal session alive (input = user present).
            if (activeRecently && now - lastTouch > TimeSpan.FromSeconds(60))
            {
                lastTouch = now;
                await db.Sessions.Where(s => s.Id == sessionId).ExecuteUpdateAsync(u => u.SetProperty(s => s.LastSeenAt, now), ct);
            }
        }
    }

    private async Task SendFrameAsync(ConsoleFrame f, byte[] data, CancellationToken ct)
    {
        var buffer = new byte[10 + data.Length];
        buffer[0] = 1;
        buffer[1] = (byte)(f.MouseAvailable ? 1 : 0);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(2), (ushort)f.Width);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), (ushort)f.Height);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(6), (ushort)Math.Clamp(f.GuestWidth, 0, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8), (ushort)Math.Clamp(f.GuestHeight, 0, ushort.MaxValue));
        data.CopyTo(buffer, 10);
        await SendAsync(buffer, WebSocketMessageType.Binary, ct);
    }

    private async Task SendStatusAsync(string state, string? message, CancellationToken ct, bool? mouse = null)
    {
        var key = state + "|" + message + "|" + mouse;
        if (key == _lastState)
        {
            return;
        }
        _lastState = key;
        var json = JsonSerializer.SerializeToUtf8Bytes(new { t = "status", state, message, mouse });
        await SendAsync(json, WebSocketMessageType.Text, ct);
    }

    private async Task SendAsync(byte[] payload, WebSocketMessageType type, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try
        {
            if (ws.State == WebSocketState.Open)
            {
                await ws.SendAsync(payload, type, true, ct);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }
}
