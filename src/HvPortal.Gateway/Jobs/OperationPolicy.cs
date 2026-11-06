using System.Text.Json;
using System.Text.RegularExpressions;
using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Data;

namespace HvPortal.Gateway.Jobs;

/// <summary>
/// Server-side rules shared by the API (request time) and the job runner (execution time, re-validated):
/// which permissions a configuration change needs, and which ISO library folders a host exposes.
/// </summary>
public static partial class OperationPolicy
{
    public const int MaxIsoRoots = 16;

    /// <summary>Local absolute folder (drive letter), no traversal, no device paths, no UNC (the agent runs as a virtual account).</summary>
    // Segments separated by single backslashes, written so that every character can be matched in one way only:
    // the earlier form, "(segment + optional backslash)*", took exponential time on a long name followed by a
    // refused character (a request could keep a thread busy for hours).
    [GeneratedRegex(@"^[A-Za-z]:\\(?:[^\x00-\x1f""*?<>|:\\/]+(?:\\[^\x00-\x1f""*?<>|:\\/]+)*\\?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex LibraryPathRegex();

    public static IReadOnlyList<string> PermissionsFor(VmConfigChange c)
    {
        var list = new List<string>();
        var compute = c.NewName is not null || c.ProcessorCount is not null || c.ProcessorReserve is not null
                      || c.ProcessorLimit is not null || c.ProcessorWeight is not null || c.MemoryStartupMb is not null
                      || c.DynamicMemoryEnabled is not null || c.MemoryMinimumMb is not null || c.MemoryMaximumMb is not null
                      || c.MemoryBuffer is not null || c.MemoryPriority is not null || c.AutomaticStartAction is not null
                      || c.AutomaticStartDelaySeconds is not null || c.AutomaticStopAction is not null || c.CheckpointType is not null
                      || c.SecureBootEnabled is not null || c.SecureBootTemplate is not null;
        var storage = c.DvdIsoPath is not null || c.NewDisk is not null || c.ExpandDisk is not null;
        var network = c.NetworkAdapterChanges is { Count: > 0 } || c.AddNetworkAdapterSwitch is not null;
        // The Hyper-V notes are the VM's annotation (the portal has no description of its own): inventory.annotate.
        if (c.Notes is not null)
        {
            list.Add(Permissions.InventoryAnnotate);
        }
        if (compute)
        {
            list.Add(Permissions.VmConfigCompute);
        }
        if (storage)
        {
            list.Add(Permissions.VmConfigStorage);
        }
        if (network)
        {
            list.Add(Permissions.VmConfigNetwork);
        }
        return list;
    }

    public static IReadOnlyList<string> IsoRoots(ManagedHost host)
    {
        if (string.IsNullOrWhiteSpace(host.IsoLibraryPathsJson))
        {
            return [];
        }
        try
        {
            return (JsonSerializer.Deserialize<string[]>(host.IsoLibraryPathsJson) ?? []).Where(IsValidLibraryPath).Take(MaxIsoRoots).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static bool IsValidLibraryPath(string? path) =>
        path is { Length: >= 3 and <= 240 } && LibraryPathRegex().IsMatch(path) && !path.Contains("..", StringComparison.Ordinal);

    /// <summary>Gateway-side containment check (the agent re-checks with the real file system, rejecting reparse points).</summary>
    public static bool IsInsideLibrary(string isoPath, IReadOnlyList<string> roots)
    {
        if (!OperationValidation.IsValidIsoPath(isoPath))
        {
            return false;
        }
        foreach (var r in roots)
        {
            var root = r.TrimEnd('\\') + "\\";
            if (isoPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) && isoPath.Length > root.Length)
            {
                return true;
            }
        }
        return false;
    }
}
