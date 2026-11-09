using System.Management;
using HvPortal.Contracts.HyperV;

namespace HvPortal.Agent.HyperV;

/// <summary>
/// Protocol 11. Reads, with every inventory, the failover cluster of the local host through the documented WMI provider
/// root\MSCluster (MSCluster_Cluster, MSCluster_Node, MSCluster_Resource) and the cluster shared volumes through
/// Win32_Volume (file system CSVFS). Read-only, constant WQL (no input is interpolated).
/// The cluster provider requires packet privacy (the scopes of HostTarget use it) and the right to read the cluster:
/// the agent account gets it with Grant-ClusterAccess (Install-Agent -ClusterAccess). When it is missing, Error says so
/// and nothing else is reported (never guessed).
/// </summary>
public static class ClusterReader
{
    private const string ClusterNamespace = @"root\MSCluster";
    private const string CimV2Namespace = @"root\cimv2";
    private const int MaxClusteredVms = 4096;

    public static ClusterFacts Read(string computerName, Func<string, ManagementScope> connect, ILogger logger)
    {
        var sharedVolumes = ReadSharedVolumes(connect, logger);
        try
        {
            var scope = connect(ClusterNamespace);
            string? name = null, fqdn = null;
            foreach (var o in Query(scope, "SELECT * FROM MSCluster_Cluster"))
            {
                using (o)
                {
                    name = Text(o, "Name");
                    fqdn = Text(o, "Fqdn");
                }
            }
            string? nodeName = null, nodeState = null, drain = null;
            foreach (var o in Query(scope, "SELECT * FROM MSCluster_Node"))
            {
                using (o)
                {
                    var n = Text(o, "Name");
                    if (n is null || !n.Equals(computerName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    nodeName = n;
                    nodeState = NodeState(Number(o, "State"));
                    drain = DrainStatus(Number(o, "NodeDrainStatus"));
                }
            }
            var vmIds = new List<Guid>();
            foreach (var o in Query(scope, "SELECT * FROM MSCluster_Resource WHERE Type = 'Virtual Machine'"))
            {
                using (o)
                {
                    if (vmIds.Count < MaxClusteredVms && VmIdOf(o) is { } id)
                    {
                        vmIds.Add(id);
                    }
                }
            }
            return new ClusterFacts
            {
                Name = name,
                Fqdn = fqdn,
                NodeName = nodeName,
                NodeState = nodeState ?? "Unknown",
                DrainStatus = drain,
                ClusteredVmIds = vmIds.Distinct().ToList(),
                SharedVolumes = sharedVolumes,
            };
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            logger.LogDebug(ex, "Cluster information not readable");
            var denied = ex is UnauthorizedAccessException || (ex is ManagementException me && me.ErrorCode == ManagementStatus.AccessDenied);
            return new ClusterFacts
            {
                SharedVolumes = sharedVolumes,
                Error = denied
                    ? "access_denied: the agent account cannot read the cluster (Grant-ClusterAccess -User 'NT SERVICE\\HvPortalAgent' -ReadOnly, or -Full for maintenance and highly available VMs)."
                    : "cluster_unreadable: " + ex.Message,
            };
        }
    }

    /// <summary>Cluster shared volumes mounted on this node (C:\ClusterStorage\VolumeN).</summary>
    private static List<HostVolume> ReadSharedVolumes(Func<string, ManagementScope> connect, ILogger logger)
    {
        var list = new List<HostVolume>();
        try
        {
            var scope = connect(CimV2Namespace);
            foreach (var o in Query(scope, "SELECT Name, Label, FileSystem, Capacity, FreeSpace FROM Win32_Volume WHERE FileSystem = 'CSVFS'"))
            {
                using (o)
                {
                    var path = (o["Name"] as string)?.TrimEnd('\\');
                    if (ClusterNames.SharedVolumeRoot(path) is not { } root || !root.Equals(path, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    list.Add(new HostVolume(root, o["Label"] as string, "CSVFS", o["Capacity"] is ulong c ? (long)c : null,
                        o["FreeSpace"] is ulong f ? (long)f : null));
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            logger.LogDebug(ex, "Cluster shared volumes not readable");
        }
        return list.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static Guid? VmIdOf(ManagementBaseObject resource)
    {
        try
        {
            if (resource["PrivateProperties"] is ManagementBaseObject props)
            {
                using (props)
                {
                    return Guid.TryParse(Text(props, "VmID"), out var id) && id != Guid.Empty ? id : null;
                }
            }
        }
        catch (ManagementException)
        {
        }
        return null;
    }

    /// <summary>MSCluster_Node.State: -1 Unknown, 0 Up, 1 Down, 2 Paused, 3 Joining.</summary>
    internal static string NodeState(long? state) => state switch
    {
        0 => "Up",
        1 => "Down",
        2 => "Paused",
        3 => "Joining",
        _ => "Unknown",
    };

    /// <summary>MSCluster_Node.NodeDrainStatus: 0 NotInitiated, 1 InProgress, 2 Completed, 3 Failed.</summary>
    internal static string? DrainStatus(long? status) => status switch
    {
        0 => "NotInitiated",
        1 => "InProgress",
        2 => "Completed",
        3 => "Failed",
        _ => null,
    };

    private static string? Text(ManagementBaseObject o, string property)
    {
        try
        {
            return o[property] as string;
        }
        catch (ManagementException)
        {
            return null; // property not present in this version of the provider
        }
    }

    private static long? Number(ManagementBaseObject o, string property)
    {
        try
        {
            return o[property] switch
            {
                int i => i,
                uint u => u,
                long l => l,
                ulong ul => (long)ul,
                short s => s,
                ushort us => us,
                _ => null,
            };
        }
        catch (ManagementException)
        {
            return null;
        }
    }

    private static List<ManagementBaseObject> Query(ManagementScope scope, string wql)
    {
        using var s = new ManagementObjectSearcher(scope, new ObjectQuery(wql), new System.Management.EnumerationOptions { ReturnImmediately = true, Rewindable = false, Timeout = TimeSpan.FromSeconds(20) });
        using var r = s.Get();
        return WmiHyperVHost.Materialize(r);
    }
}
