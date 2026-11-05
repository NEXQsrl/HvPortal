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
        var caps = new List<Capability>
        {
            new("inventory", ok ? "available" : "unavailable", ok ? null : "Agent cannot access the Hyper-V WMI provider."),
            new("vm.power", ok ? "available" : "unavailable"),
            new("vm.guest-restart", ok && build >= 14393 ? "available" : "unavailable", "Msvm_ShutdownComponent.InitiateReboot requires WS2016+."),
            new("cluster", facts?.ClusterServicePresent == true ? "detected (management in Milestone 3)" : "not detected"),
            new("storage-spaces-direct", datacenter ? "edition allows (Milestone 4)" : "not available in this edition"),
            new("shielded-vms", datacenter ? "edition allows (not managed)" : "not available in this edition"),
        };
        return new Evaluation(edition, platform, support, caps);
    }
}
