using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HvPortal.Contracts.Agent;
using HvPortal.Contracts.HyperV;

namespace HvPortal.Agent.HyperV;

/// <summary>
/// Runs the fixed operations script (Ops\HvPortal.Ops.ps1, shipped with the agent and read-only for the service)
/// in Windows PowerShell 5.1 with the Hyper-V module. The request is a JSON document on stdin; the script never
/// evaluates caller-provided code. Arguments are validated here first (same rules as the gateway).
/// Remote target (management agent): the fixed launcher Ops\HvPortal.Remote.ps1 runs the same operations script on the
/// host with Invoke-Command -FilePath (WinRM); the credentials travel on the launcher's stdin together with the request.
/// File-system operations go through <see cref="HostFileAccess"/> (administrative shares for remote hosts).
/// </summary>
public sealed class PowerShellOperationRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);
    private readonly ILogger _logger;
    private readonly string _scriptPath;
    private readonly string _remoteLauncherPath;
    private readonly string _powershell;
    private readonly HostTarget _target;
    private readonly HostFileAccess _files;

    public PowerShellOperationRunner(ILogger<PowerShellOperationRunner> logger) : this(logger, HostTarget.Local, HostFileAccess.Local)
    {
    }

    public PowerShellOperationRunner(ILogger logger, HostTarget target, HostFileAccess files)
    {
        _logger = logger;
        _target = target;
        _files = files;
        _scriptPath = Path.Combine(AppContext.BaseDirectory, "Ops", "HvPortal.Ops.ps1");
        _remoteLauncherPath = Path.Combine(AppContext.BaseDirectory, "Ops", "HvPortal.Remote.ps1");
        _powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
    }

    public async Task<HvOperationOutcome> RunAsync(HvOperation operation, JsonElement? args, TimeSpan? timeout, CancellationToken ct)
    {
        var validation = Validate(operation, args, _files);
        if (validation is not null)
        {
            return new HvOperationOutcome { Ok = false, Error = "Rejected by agent validation: " + validation };
        }
        if (operation is HvOperation.ListFolder or HvOperation.ReadFile or HvOperation.WriteFileBegin or HvOperation.WriteFileChunk
            or HvOperation.WriteFileCommit or HvOperation.WriteFileAbort)
        {
            // Host file explorer: plain file-system I/O in-process (no PowerShell).
            try
            {
                object? result = operation switch
                {
                    HvOperation.ListFolder => HostFileService.List(HvOperations.FromElement<FileListRequest>(args)!, _files),
                    HvOperation.ReadFile => HostFileService.Read(HvOperations.FromElement<FileReadRequest>(args)!, _files),
                    HvOperation.WriteFileBegin => HostFileService.Begin(HvOperations.FromElement<FileWriteBeginRequest>(args)!, _files),
                    HvOperation.WriteFileChunk => new { written = await HostFileService.ChunkAsync(HvOperations.FromElement<FileWriteChunkRequest>(args)!, ct) },
                    HvOperation.WriteFileCommit => HostFileService.Commit(HvOperations.FromElement<FileWriteCommitRequest>(args)!),
                    _ => Aborted(HvOperations.FromElement<FileWriteAbortRequest>(args)!),
                };
                return new HvOperationOutcome { Ok = true, Result = HvOperations.ToElement(result) };
            }
            catch (Exception ex) when (ex is HostPathException or IOException or UnauthorizedAccessException or FormatException
                                           or System.Management.ManagementException or System.Runtime.InteropServices.COMException)
            {
                return new HvOperationOutcome
                {
                    Ok = false,
                    Error = ex switch
                    {
                        UnauthorizedAccessException when _target.IsLocal => "access_denied: access denied for the agent service account.",
                        UnauthorizedAccessException => $"access_denied: access denied by {_target.Display} for {_target.UserName} (administrative share).",
                        System.Management.ManagementException or System.Runtime.InteropServices.COMException => $"host_unreachable: {ex.Message}",
                        FormatException => "invalid_data: chunk is not valid base64.",
                        _ => ex.Message,
                    },
                };
            }
        }
        if (operation == HvOperation.BrowseFolders)
        {
            // Plain file-system listing: done in-process (no PowerShell, nothing modified).
            try
            {
                var listing = HostFolderBrowser.Browse(HvOperations.FromElement<FolderBrowseRequest>(args)!, _files);
                return new HvOperationOutcome { Ok = true, Result = HvOperations.ToElement(listing) };
            }
            catch (Exception ex) when (ex is HostPathException or IOException or UnauthorizedAccessException
                                           or System.Management.ManagementException or System.Runtime.InteropServices.COMException)
            {
                return new HvOperationOutcome
                {
                    Ok = false,
                    Error = ex is UnauthorizedAccessException
                        ? (_target.IsLocal ? "Access denied to the folder for the agent service account." : $"Access denied by {_target.Display} (administrative share).")
                        : ex.Message,
                };
            }
        }
        if (!File.Exists(_scriptPath) || (!_target.IsLocal && !File.Exists(_remoteLauncherPath)))
        {
            return new HvOperationOutcome { Ok = false, Error = "Operations script missing from the agent installation." };
        }

        var request = JsonSerializer.Serialize(new { op = operation.ToString(), args }, AgentProtocol.Json);
        if (!_target.IsLocal)
        {
            // The launcher receives the target (with the password) and the unchanged request on stdin: nothing on the command line.
            request = JsonSerializer.Serialize(new
            {
                target = new { computerName = _target.ComputerName, userName = _target.UserName, password = _target.RevealPassword(), useHttps = _target.UseHttps },
                request,
            }, AgentProtocol.Json);
        }
        var psi = new ProcessStartInfo
        {
            FileName = _powershell,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        // Fixed argument list (no caller data on the command line).
        foreach (var a in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", _target.IsLocal ? _scriptPath : _remoteLauncherPath })
        {
            psi.ArgumentList.Add(a);
        }

        using var process = new Process { StartInfo = psi };
        var sw = Stopwatch.StartNew();
        process.Start();
        await process.StandardInput.WriteAsync(request.AsMemory(), ct);
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? DefaultTimeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            return new HvOperationOutcome { Ok = false, Error = $"Operation {operation} timed out after {(timeout ?? DefaultTimeout).TotalSeconds:0}s." };
        }

        var output = (await stdout).Trim();
        var errors = (await stderr).Trim();
        _logger.LogInformation("Hyper-V operation {Operation} on {Host} finished in {Ms} ms (exit {Code})", operation, _target.Display, sw.ElapsedMilliseconds, process.ExitCode);
        if (output.Length == 0)
        {
            return new HvOperationOutcome { Ok = false, Error = "No output from the operations script. " + Truncate(errors, 500) };
        }
        try
        {
            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;
            var ok = root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
            return new HvOperationOutcome
            {
                Ok = ok,
                Error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } msg ? Truncate(msg, 2000) : null,
                Applied = root.TryGetProperty("applied", out var ap) && ap.ValueKind == JsonValueKind.Array
                    ? ap.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                    : [],
                Result = root.TryGetProperty("result", out var r) && r.ValueKind != JsonValueKind.Null ? r.Clone() : null,
            };
        }
        catch (JsonException)
        {
            return new HvOperationOutcome { Ok = false, Error = "Invalid output from the operations script: " + Truncate(output, 300) };
        }
    }

    private static object Aborted(FileWriteAbortRequest r)
    {
        HostFileService.Abort(r);
        return new { aborted = true };
    }

    internal static string? Validate(HvOperation operation, JsonElement? args, HostFileAccess? files = null)
    {
        try
        {
            switch (operation)
            {
                case HvOperation.GetHostDefaults:
                case HvOperation.ListSwitches:
                case HvOperation.ListHostDisks:
                case HvOperation.ListVmGroups:
                case HvOperation.ListNetAdapters:
                    return null;
                case HvOperation.NewSwitch:
                    return HvOperations.FromElement<NewSwitchRequest>(args) is { } ns ? OperationValidation.Validate(ns) : "invalid_arguments";
                case HvOperation.RemoveSwitch:
                    return HvOperations.FromElement<SwitchRef>(args) is { } rs ? OperationValidation.Validate(rs) : "invalid_arguments";
                case HvOperation.SyncVmGroups:
                    return HvOperations.FromElement<VmGroupSyncRequest>(args) is { } sync ? OperationValidation.Validate(sync) : "invalid_arguments";
                case HvOperation.GetVmConfig:
                    return HvOperations.FromElement<VmRef>(args) is { VmId: var id } && id != Guid.Empty ? null : "invalid_vm";
                case HvOperation.SetVmConfig:
                    if (HvOperations.FromElement<VmConfigChange>(args) is not { } c)
                    {
                        return "invalid_arguments";
                    }
                    if (OperationValidation.Validate(c) is { } invalidChange)
                    {
                        return invalidChange;
                    }
                    if (c.NewDisk?.Folder is { } diskFolder)
                    {
                        HostFolderBrowser.CheckFolder(diskFolder, mustExist: false, files);
                    }
                    return null;
                case HvOperation.NewVm:
                    if (HvOperations.FromElement<NewVmRequest>(args) is not { } n)
                    {
                        return "invalid_arguments";
                    }
                    if (OperationValidation.Validate(n) is { } invalidVm)
                    {
                        return invalidVm;
                    }
                    if (n.Location is { } location)
                    {
                        HostFolderBrowser.CheckFolder(location, mustExist: false, files);
                    }
                    return null;
                case HvOperation.BrowseFolders:
                    return HvOperations.FromElement<FolderBrowseRequest>(args) is { } b ? OperationValidation.Validate(b) : "invalid_arguments";
                case HvOperation.ListFolder:
                    return HvOperations.FromElement<FileListRequest>(args) is { } lf && HostPaths.IsValidLocalFolder(lf.Path)
                        ? (HostPaths.IsBlocked(lf.Path) ? "location_not_allowed" : null) : "invalid_path";
                case HvOperation.ReadFile:
                    return HvOperations.FromElement<FileReadRequest>(args) is { } rf ? OperationValidation.Validate(rf) : "invalid_arguments";
                case HvOperation.WriteFileBegin:
                    return HvOperations.FromElement<FileWriteBeginRequest>(args) is { } wb ? OperationValidation.Validate(wb) : "invalid_arguments";
                case HvOperation.WriteFileChunk:
                    return HvOperations.FromElement<FileWriteChunkRequest>(args) is { } wc && wc.UploadId != Guid.Empty && wc.Offset >= 0
                        && wc.Data is { Length: > 0 and <= FileTransfer.MaxChunkBase64 } ? null : "invalid_chunk";
                case HvOperation.WriteFileCommit:
                    return HvOperations.FromElement<FileWriteCommitRequest>(args) is { } cm && cm.UploadId != Guid.Empty
                        && cm.Sha256 is { Length: 64 } h && h.All(Uri.IsHexDigit) ? null : "invalid_commit";
                case HvOperation.WriteFileAbort:
                    return HvOperations.FromElement<FileWriteAbortRequest>(args) is { UploadId: var ua } && ua != Guid.Empty ? null : "invalid_arguments";
                case HvOperation.ListIsos:
                    return HvOperations.FromElement<IsoListRequest>(args) is { } i && i.Roots.Count <= 16 && i.Roots.All(r => r.Length <= 260) ? null : "invalid_iso_roots";
                case HvOperation.ListCheckpoints:
                    return HvOperations.FromElement<CheckpointRequest>(args) is { VmId: var v } && v != Guid.Empty ? null : "invalid_vm";
                case HvOperation.NewCheckpoint:
                    return HvOperations.FromElement<CheckpointRequest>(args) is { } nc && nc.VmId != Guid.Empty && OperationValidation.IsValidCheckpointName(nc.Name) ? null : "invalid_checkpoint_name";
                case HvOperation.RestoreCheckpoint:
                case HvOperation.RemoveCheckpoint:
                    return HvOperations.FromElement<CheckpointRequest>(args) is { } rc && rc.VmId != Guid.Empty && Guid.TryParse(rc.CheckpointId, out _) ? null : "invalid_checkpoint";
                default:
                    return "unsupported_operation";
            }
        }
        catch (JsonException)
        {
            return "invalid_arguments";
        }
        catch (HostPathException ex)
        {
            return "invalid_location (" + ex.Message + ")";
        }
    }

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}
