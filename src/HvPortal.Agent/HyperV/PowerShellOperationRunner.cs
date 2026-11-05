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
/// </summary>
public sealed class PowerShellOperationRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);
    private readonly ILogger<PowerShellOperationRunner> _logger;
    private readonly string _scriptPath;
    private readonly string _powershell;

    public PowerShellOperationRunner(ILogger<PowerShellOperationRunner> logger)
    {
        _logger = logger;
        _scriptPath = Path.Combine(AppContext.BaseDirectory, "Ops", "HvPortal.Ops.ps1");
        _powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
    }

    public async Task<HvOperationOutcome> RunAsync(HvOperation operation, JsonElement? args, TimeSpan? timeout, CancellationToken ct)
    {
        var validation = Validate(operation, args);
        if (validation is not null)
        {
            return new HvOperationOutcome { Ok = false, Error = "Rejected by agent validation: " + validation };
        }
        if (operation == HvOperation.BrowseFolders)
        {
            // Plain file-system listing: done in-process (no PowerShell, nothing modified).
            try
            {
                var listing = HostFolderBrowser.Browse(HvOperations.FromElement<FolderBrowseRequest>(args)!);
                return new HvOperationOutcome { Ok = true, Result = HvOperations.ToElement(listing) };
            }
            catch (Exception ex) when (ex is HostPathException or IOException or UnauthorizedAccessException)
            {
                return new HvOperationOutcome { Ok = false, Error = ex is UnauthorizedAccessException ? "Access denied to the folder for the agent service account." : ex.Message };
            }
        }
        if (!File.Exists(_scriptPath))
        {
            return new HvOperationOutcome { Ok = false, Error = "Operations script missing from the agent installation." };
        }

        var request = JsonSerializer.Serialize(new { op = operation.ToString(), args }, AgentProtocol.Json);
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
        foreach (var a in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", _scriptPath })
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
        _logger.LogInformation("Hyper-V operation {Operation} finished in {Ms} ms (exit {Code})", operation, sw.ElapsedMilliseconds, process.ExitCode);
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

    internal static string? Validate(HvOperation operation, JsonElement? args)
    {
        try
        {
            switch (operation)
            {
                case HvOperation.GetHostDefaults:
                case HvOperation.ListSwitches:
                case HvOperation.ListHostDisks:
                case HvOperation.ListVmGroups:
                    return null;
                case HvOperation.SyncVmGroups:
                    return HvOperations.FromElement<VmGroupSyncRequest>(args) is { } sync ? OperationValidation.Validate(sync) : "invalid_arguments";
                case HvOperation.GetVmConfig:
                    return HvOperations.FromElement<VmRef>(args) is { VmId: var id } && id != Guid.Empty ? null : "invalid_vm";
                case HvOperation.SetVmConfig:
                    return HvOperations.FromElement<VmConfigChange>(args) is { } c ? OperationValidation.Validate(c) : "invalid_arguments";
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
                        HostFolderBrowser.CheckFolder(location, mustExist: false);
                    }
                    return null;
                case HvOperation.BrowseFolders:
                    return HvOperations.FromElement<FolderBrowseRequest>(args) is { } b ? OperationValidation.Validate(b) : "invalid_arguments";
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
