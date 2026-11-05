using System.Collections.Concurrent;
using System.IO.Compression;
using System.Management;
using HvPortal.Contracts.Agent;
using HvPortal.Contracts.HyperV;

namespace HvPortal.Agent.HyperV;

/// <summary>
/// Basic VM console through documented Hyper-V WMI APIs (works before the guest OS boots: BIOS/UEFI, setup):
///  - screen: Msvm_VirtualSystemManagementService.GetVirtualSystemThumbnailImage (RGB565), sized from Msvm_VideoHead;
///  - keyboard: Msvm_Keyboard.TypeScancodes (set-1 scancodes, layout-independent), TypeCtrlAltDel, TypeText;
///  - mouse: Msvm_SyntheticMouse.SetAbsolutePosition / SetButtonState (only when the guest supports it).
/// Frames are zlib-compressed and sent only when the pixels change. Expected rate: a few frames per second.
/// </summary>
public sealed class ConsoleService
{
    private const string Ns = @"\\.\root\virtualization\v2";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();
    private readonly ILogger<ConsoleService> _logger;

    public ConsoleService(ILogger<ConsoleService> logger)
    {
        _logger = logger;
    }

    private sealed class Session
    {
        public required ManagementScope Scope { get; init; }
        public required ManagementObject Vsms { get; init; }
        public required string VssdPath { get; init; }
        public ManagementObject? Keyboard { get; set; }
        public ManagementObject? Mouse { get; set; }
        public byte[]? LastFrame { get; set; }
        public int GuestWidth { get; set; }
        public int GuestHeight { get; set; }
        public DateTime ResolutionReadAt { get; set; }
        public DateTime LastUsed { get; set; }
        public readonly SemaphoreSlim Gate = new(1, 1);
    }

    public async Task<AgentCommandResult> FrameAsync(Guid commandId, ConsoleFrameRequest req, CancellationToken ct)
    {
        var session = GetSession(req.VmId);
        if (session is null)
        {
            return new AgentCommandResult { CommandId = commandId, Status = AgentCommandStatus.NotFound, Message = "VM not found." };
        }
        await session.Gate.WaitAsync(ct);
        try
        {
            return await Task.Run(() => ReadFrame(commandId, session, req), ct);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private AgentCommandResult ReadFrame(Guid commandId, Session s, ConsoleFrameRequest req)
    {
        s.LastUsed = DateTime.UtcNow;
        if (s.GuestWidth == 0 || DateTime.UtcNow - s.ResolutionReadAt > TimeSpan.FromSeconds(2))
        {
            ReadResolution(s, req.VmId);
        }
        var maxW = Math.Clamp(req.MaxWidth, 160, 1920);
        var maxH = Math.Clamp(req.MaxHeight, 120, 1200);
        var gw = s.GuestWidth > 0 ? s.GuestWidth : 1024;
        var gh = s.GuestHeight > 0 ? s.GuestHeight : 768;
        var scale = Math.Min(1.0, Math.Min(maxW / (double)gw, maxH / (double)gh));
        var w = Math.Max(16, (int)(gw * scale)) & ~1;
        var h = Math.Max(16, (int)(gh * scale)) & ~1;

        using var inParams = s.Vsms.GetMethodParameters("GetVirtualSystemThumbnailImage");
        inParams["TargetSystem"] = s.VssdPath;
        inParams["WidthPixels"] = (ushort)w;
        inParams["HeightPixels"] = (ushort)h;
        using var outParams = s.Vsms.InvokeMethod("GetVirtualSystemThumbnailImage", inParams, null);
        var rv = outParams["ReturnValue"] is uint u ? u : uint.MaxValue;
        if (rv != 0 || outParams["ImageData"] is not byte[] pixels)
        {
            return new AgentCommandResult
            {
                CommandId = commandId,
                Status = rv == 32775 ? AgentCommandStatus.InvalidState : AgentCommandStatus.Failed,
                Message = rv == 32775 ? "The VM is not running." : $"GetVirtualSystemThumbnailImage returned {rv}.",
                HyperVCode = rv,
            };
        }

        var mouseAvailable = s.Mouse is not null && IsMouseEnabled(s);
        if (!req.ForceFull && s.LastFrame is { } last && last.AsSpan().SequenceEqual(pixels))
        {
            return Result(commandId, new ConsoleFrame { Width = w, Height = h, Unchanged = true, MouseAvailable = mouseAvailable, GuestWidth = gw, GuestHeight = gh });
        }
        s.LastFrame = pixels;
        using var ms = new MemoryStream(pixels.Length / 4);
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            z.Write(pixels);
        }
        return Result(commandId, new ConsoleFrame
        {
            Width = w,
            Height = h,
            Data = Convert.ToBase64String(ms.GetBuffer(), 0, (int)ms.Length),
            MouseAvailable = mouseAvailable,
            GuestWidth = gw,
            GuestHeight = gh,
        });
    }

    public async Task<AgentCommandResult> InputAsync(Guid commandId, ConsoleInput input, CancellationToken ct)
    {
        var s = GetSession(input.VmId);
        if (s is null)
        {
            return new AgentCommandResult { CommandId = commandId, Status = AgentCommandStatus.NotFound };
        }
        await s.Gate.WaitAsync(ct);
        try
        {
            return await Task.Run(() =>
            {
                s.LastUsed = DateTime.UtcNow;
                uint rv;
                switch (input.Kind)
                {
                    case ConsoleInputKind.Scancodes when input.Scancodes is { Length: > 0 and <= 32 } codes && s.Keyboard is { } kb:
                        rv = Invoke(kb, "TypeScancodes", p => p["Scancodes"] = codes);
                        break;
                    case ConsoleInputKind.CtrlAltDel when s.Keyboard is { } kb2:
                        rv = Invoke(kb2, "TypeCtrlAltDel", null);
                        break;
                    case ConsoleInputKind.TypeText when input.Text is { Length: > 0 and <= 512 } text && s.Keyboard is { } kb3:
                        rv = Invoke(kb3, "TypeText", p => p["asciiText"] = text);
                        break;
                    case ConsoleInputKind.MouseMove when s.Mouse is { } m:
                        rv = Invoke(m, "SetAbsolutePosition", p =>
                        {
                            p["horizontalPosition"] = Math.Clamp(input.X, 0, 16384);
                            p["verticalPosition"] = Math.Clamp(input.Y, 0, 16384);
                        });
                        break;
                    case ConsoleInputKind.MouseButton when s.Mouse is { } m2 && input.Button is >= 1 and <= 5:
                        rv = Invoke(m2, "SetButtonState", p =>
                        {
                            p["buttonIndex"] = (uint)input.Button;
                            p["isDown"] = input.Down;
                        });
                        break;
                    default:
                        return new AgentCommandResult { CommandId = commandId, Status = AgentCommandStatus.Rejected, Message = "Input not supported for this VM." };
                }
                return new AgentCommandResult
                {
                    CommandId = commandId,
                    Status = rv == 0 ? AgentCommandStatus.Succeeded : AgentCommandStatus.Failed,
                    HyperVCode = rv,
                };
            }, ct);
        }
        catch (ManagementException ex)
        {
            Drop(input.VmId);
            return new AgentCommandResult { CommandId = commandId, Status = AgentCommandStatus.Failed, Message = ex.Message };
        }
        finally
        {
            s.Gate.Release();
        }
    }

    private static uint Invoke(ManagementObject target, string method, Action<ManagementBaseObject>? fill)
    {
        using var inParams = target.GetMethodParameters(method);
        fill?.Invoke(inParams);
        using var outParams = target.InvokeMethod(method, inParams, null);
        return outParams["ReturnValue"] is uint u ? u : uint.MaxValue;
    }

    private Session? GetSession(Guid vmId)
    {
        foreach (var (id, old) in _sessions)
        {
            if (DateTime.UtcNow - old.LastUsed > CacheTtl)
            {
                Drop(id);
            }
        }
        if (_sessions.TryGetValue(vmId, out var existing))
        {
            return existing;
        }
        var scope = new ManagementScope(Ns, new ConnectionOptions { Impersonation = ImpersonationLevel.Impersonate, Authentication = AuthenticationLevel.PacketPrivacy });
        scope.Connect();
        var vssd = First(scope, $"SELECT * FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemIdentifier = '{vmId:D}' AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'");
        var vsms = First(scope, "SELECT * FROM Msvm_VirtualSystemManagementService");
        if (vssd is null || vsms is null)
        {
            return null;
        }
        var session = new Session
        {
            Scope = scope,
            Vsms = vsms,
            VssdPath = vssd.Path.Path,
            Keyboard = First(scope, $"SELECT * FROM Msvm_Keyboard WHERE SystemName = '{vmId:D}'"),
            Mouse = First(scope, $"SELECT * FROM Msvm_SyntheticMouse WHERE SystemName = '{vmId:D}'"),
            LastUsed = DateTime.UtcNow,
        };
        vssd.Dispose();
        _logger.LogInformation("Console session opened for VM {VmId} (keyboard: {Kb}, synthetic mouse: {Mouse})", vmId, session.Keyboard is not null, session.Mouse is not null);
        return _sessions.GetOrAdd(vmId, session);
    }

    private static bool IsMouseEnabled(Session s)
    {
        try
        {
            s.Mouse!.Get();
            return s.Mouse["EnabledState"] is ushort es && es == 2;
        }
        catch (ManagementException)
        {
            return false;
        }
    }

    private static void ReadResolution(Session s, Guid vmId)
    {
        s.ResolutionReadAt = DateTime.UtcNow;
        using var head = First(s.Scope, $"SELECT CurrentHorizontalResolution, CurrentVerticalResolution FROM Msvm_VideoHead WHERE SystemName = '{vmId:D}'");
        if (head is not null && head["CurrentHorizontalResolution"] is uint hw && head["CurrentVerticalResolution"] is uint hh && hw > 0 && hh > 0)
        {
            s.GuestWidth = (int)hw;
            s.GuestHeight = (int)hh;
        }
    }

    private void Drop(Guid vmId)
    {
        if (_sessions.TryRemove(vmId, out var s))
        {
            s.Keyboard?.Dispose();
            s.Mouse?.Dispose();
            s.Vsms.Dispose();
        }
    }

    private static ManagementObject? First(ManagementScope scope, string wql)
    {
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql));
        using var r = searcher.Get();
        foreach (ManagementBaseObject o in r)
        {
            return o as ManagementObject;
        }
        return null;
    }

    private static AgentCommandResult Result(Guid id, ConsoleFrame frame) =>
        new() { CommandId = id, Status = AgentCommandStatus.Succeeded, Frame = frame };
}
