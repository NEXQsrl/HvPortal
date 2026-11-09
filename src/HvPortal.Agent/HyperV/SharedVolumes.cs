using System.Runtime.InteropServices;
using HvPortal.Contracts.HyperV;

namespace HvPortal.Agent.HyperV;

/// <summary>
/// Protocol 11. Cluster shared volumes (CSV) of the local host. Windows mounts them as &lt;system drive&gt;:\ClusterStorage\&lt;name&gt;,
/// which are mount points (reparse points): the folder rules refuse reparse points everywhere, and this is the only
/// exception. A folder is accepted as a CSV only when it is exactly &lt;drive&gt;:\ClusterStorage\&lt;name&gt; AND Windows reports
/// the file system of the volume mounted there as CSVFS (GetVolumeInformation). Junctions or symbolic links placed there
/// by someone else are not CSVFS volumes and stay refused.
/// </summary>
public static class SharedVolumes
{
    /// <summary>True when <paramref name="path"/> is the mount point of a cluster shared volume of this host.</summary>
    public static bool IsRoot(string path)
    {
        var trimmed = path.TrimEnd('\\');
        if (ClusterNames.SharedVolumeRoot(trimmed) is not { } root || !root.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return FileSystemOf(root) is { } fs && fs.Equals("CSVFS", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The CSV that contains the path (its mount point), null when the path is not on a CSV of this host.</summary>
    public static string? RootOf(string path) => ClusterNames.SharedVolumeRoot(path.TrimEnd('\\')) is { } root && IsRoot(root) ? root : null;

    /// <summary>Free space of the volume mounted at a CSV mount point.</summary>
    public static long? FreeSpace(string root)
    {
        return GetDiskFreeSpaceExW(root.TrimEnd('\\') + "\\", out var available, out _, out _) ? (long)available : null;
    }

    /// <summary>True for &lt;drive&gt;:\ClusterStorage (the folder whose sub-folders are the CSV mount points).</summary>
    public static bool IsClusterStorageFolder(string path)
    {
        var segments = path.TrimEnd('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 2 && segments[0].Length == 2 && segments[0][1] == ':'
               && segments[1].Equals(ClusterNames.SharedVolumeFolder, StringComparison.OrdinalIgnoreCase);
    }

    private static string? FileSystemOf(string root)
    {
        var name = new char[64];
        return GetVolumeInformationW(root + "\\", null, 0, out _, out _, out _, name, name.Length) ? new string(name).TrimEnd('\0') : null;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(string directoryName, out ulong freeBytesAvailable, out ulong totalBytes, out ulong totalFreeBytes);

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(string rootPathName, char[]? volumeName, int volumeNameSize, out uint serialNumber,
        out uint maximumComponentLength, out uint fileSystemFlags, [Out] char[] fileSystemName, int fileSystemNameSize);
}
