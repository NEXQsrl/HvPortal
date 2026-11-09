using HvPortal.Contracts.HyperV;

namespace HvPortal.Gateway.Api;

/// <summary>
/// Edition / version evaluation used by the UI and the compatibility matrix. Values come from
/// Win32_OperatingSystem.OperatingSystemSKU / BuildNumber / ProductType reported by the agent.
/// "Supported" is only claimed for the target platform; everything else is reported as untested.
/// </summary>
public static class HostCapabilities
{
    public sealed record Evaluation(string Edition, string Platform, string Support, IReadOnlyList<Capability> Capabilities);
    public sealed record Capability(string Name, string Status, string? Note = null);

    public static Evaluation Evaluate(HostFacts? facts, HyperVAccessStatus? access)
    {
        var edition = facts?.OsSku switch
        {
            7 or 13 or 79 => "Standard",
            8 or 12 or 80 => "Datacenter",
            407 => "Datacenter: Azure Edition",
            null => "Unknown",
            _ => facts.ProductType == 1 ? "Client" : $"SKU {facts.OsSku}",
        };
        var build = int.TryParse(facts?.OsBuildNumber, out var b) ? b : 0;
        var isServer = facts?.ProductType is 2 or 3;
        var (platform, support) = (build, isServer) switch
        {
            (26100, true) => ("Windows Server 2025", "target (validation on real hosts pending, see compatibility matrix)"),
            (20348, true) => ("Windows Server 2022", "under evaluation - not declared supported"),
            (_, false) when build > 0 => ($"Windows client build {build}", "development/test only - not supported"),
            _ => (build > 0 ? $"Build {build}" : "Unknown", "untested"),
        };
        var ok = access == HyperVAccessStatus.Ok;
        var datacenter = edition.StartsWith("Datacenter", StringComparison.Ordinal);
        // Typed operations (Hyper-V PowerShell module) and the console need agent 0.3 or later.
        var ops = ok && AgentAtLeast(facts?.AgentVersion, 0, 3);
        var opsNote = ok && !ops ? "Update the agent to 0.3 or later." : null;
        var caps = new List<Capability>
        {
            new("inventory", ok ? "available" : "unavailable", ok ? null : "Agent cannot access the Hyper-V WMI provider."),
            new("vm.power", ok ? "available" : "unavailable"),
            new("vm.guest-restart", ok && build >= 14393 ? "available" : "unavailable", "Msvm_ShutdownComponent.InitiateReboot requires WS2016+."),
            new("vm.configure", ops ? "available" : "unavailable", opsNote ?? "CPU, memory, firmware, DVD/ISO, network adapters, new/expanded disks."),
            new("vm.create", ops ? "available" : "unavailable", opsNote),
            new("vm.location", ops && AgentAtLeast(facts?.AgentVersion, 0, 3, 1) ? "available" : "unavailable",
                ops && !AgentAtLeast(facts?.AgentVersion, 0, 3, 1) ? "Update the agent to 0.3.1 or later." : "Folder browser for VM location and ISO files (system folders hidden)."),
            new("vm.checkpoints", ops ? "available" : "unavailable", opsNote ?? "Create, apply, delete. Checkpoints are not backups."),
            new("vm.console", ops ? "available" : "unavailable", opsNote ?? "Hypervisor screen capture + keyboard/mouse (not RDP)."),
            new("vm.groups.native", ops && AgentAtLeast(facts?.AgentVersion, 0, 3, 2) ? "available" : "unavailable",
                ops && !AgentAtLeast(facts?.AgentVersion, 0, 3, 2) ? "Update the agent to 0.3.2 or later." : "Portal groups mirrored as Hyper-V VM groups (Get-VMGroup)."),
            ClusterCapability(facts),
            new("storage-spaces-direct", datacenter ? "edition allows" : "not available in this edition",
                datacenter ? "Status, pool, volumes, disks and repair jobs read through the cluster page (read-only); creating pools and volumes is not managed." : null),
            new("shielded-vms", datacenter ? "edition allows (not managed)" : "not available in this edition"),
        };
        return new Evaluation(edition, platform, support, caps);
    }

    /// <summary>Failover cluster (ADR-041): what the agent of the host reads about its cluster.</summary>
    private static Capability ClusterCapability(HostFacts? facts)
    {
        if (facts?.ClusterServicePresent != true)
        {
            return new("cluster", "not detected");
        }
        if (!AgentAtLeast(facts.AgentVersion, 0, 9))
        {
            return new("cluster", "detected", "Update the agent to 0.9.0 or later to manage the cluster.");
        }
        return facts.Cluster switch
        {
            null => new("cluster", "detected", "The Cluster service is installed but not running."),
            { Error: { } error } => new("cluster", "unavailable", error),
            { } c => new("cluster", "available", $"Node {c.NodeName} of cluster {c.Name}: status, node maintenance, highly available VMs."),
        };
    }

    internal static bool AgentAtLeast(string? version, int major, int minor, int patch = 0)
    {
        var core = version?.Split('+', '-')[0];
        return Version.TryParse(core, out var v) && new Version(v.Major, v.Minor, Math.Max(v.Build, 0)) >= new Version(major, minor, patch);
    }
}
