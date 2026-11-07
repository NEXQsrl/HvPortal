using System.Text.RegularExpressions;

namespace HvPortal.Contracts.HyperV;

/// <summary>Validation of the protocol 9 requests (VM move, folders, backups). Same rules on the gateway and on the agent.</summary>
public static partial class OperationValidation
{
    public const int MinVlanId = 1;
    public const int MaxVlanId = 4094;

    public static bool IsValidVlanId(int? vlan) => vlan is null or (>= MinVlanId and <= MaxVlanId);

    /// <summary>
    /// \\server\share[\folder...]: server = DNS name or IPv4 (RemoteHosts), share and folders made of safe characters.
    /// Hidden and administrative shares (name ending with '$') are refused. Segments separated by single backslashes,
    /// each character matched in one way only (no backtracking on long refused input).
    /// </summary>
    [GeneratedRegex(@"^\\\\([A-Za-z0-9.-]{1,253})\\([^\x00-\x1f""*?<>|:\\/$]{1,80})((?:\\[^\x00-\x1f""*?<>|:\\/]{1,120})*)$", RegexOptions.CultureInvariant)]
    private static partial Regex UncPathRegex();

    public static bool IsValidSharePath(string? path)
    {
        if (path is not { Length: >= 5 and <= HostPaths.MaxPathLength } || UncPathRegex().Match(path) is not { Success: true } m)
        {
            return false;
        }
        if (!RemoteHosts.IsValidComputerName(m.Groups[1].Value))
        {
            return false;
        }
        var segments = path[2..].Split('\\');
        return segments.Skip(1).All(s => s.Length > 0 && s is not ("." or "..") && !s.EndsWith(' ') && !s.EndsWith('.') && s.Trim() == s);
    }

    /// <summary>A local folder (not a system folder) or an SMB share path.</summary>
    public static bool IsValidRepositoryPath(string? path) =>
        path is not null && (path.StartsWith(@"\\", StringComparison.Ordinal)
            ? IsValidSharePath(path)
            : HostPaths.IsValidLocalFolder(path) && !HostPaths.IsBlocked(path) && HostPaths.Normalize(path).Length > 3);

    public static string? Validate(BackupTarget? t)
    {
        if (t is null || !IsValidRepositoryPath(t.Path)) return "invalid_repository_path";
        if (t.UserName is not null || t.Password is not null)
        {
            if (!t.IsShare) return "credentials_only_for_shares";
            if (!RemoteHosts.IsValidUserName(t.UserName) || !RemoteHosts.IsValidPassword(t.Password)) return "invalid_repository_credentials";
            if (t.StagingPath is null) return "staging_required";
        }
        if (t.StagingPath is not null && (HostPaths.ValidateLocation(t.StagingPath) is not null || HostPaths.Normalize(t.StagingPath).Length <= 3))
        {
            return "invalid_staging_path";
        }
        return null;
    }

    /// <summary>"&lt;vm key&gt;\&lt;point id&gt;": vm key of safe characters, then the restore point id (GUID "D" format).</summary>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,79}\\[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.CultureInvariant)]
    private static partial Regex PointFolderRegex();

    public static bool IsValidPointFolder(string? folder, Guid pointId) =>
        folder is not null && PointFolderRegex().IsMatch(folder) && pointId != Guid.Empty
        && folder.EndsWith(@"\" + pointId.ToString("D"), StringComparison.Ordinal);

    /// <summary>A file of a restore point: relative path without traversal, drive, stream or wildcard characters.</summary>
    public static bool IsValidPointFile(string? path)
    {
        if (path is not { Length: >= 1 and <= 400 } || path.StartsWith('\\') || path.Contains(':') || path.Contains('/'))
        {
            return false;
        }
        return path.Split('\\').All(s => s.Length > 0 && s is not ("." or "..") && HostPaths.IsValidFileName(s));
    }

    public static string? Validate(BackupVmRequest r)
    {
        if (r.VmId == Guid.Empty) return "invalid_vm";
        if (Validate(r.Target) is { } t) return t;
        if (!IsValidPointFolder(r.Folder, r.PointId)) return "invalid_backup_folder";
        if (!BackupCheckpointTypes.All.Contains(r.CheckpointType)) return "invalid_checkpoint_type";
        return null;
    }

    public static string? Validate(BackupPointRequest r, bool folderRequired = true)
    {
        if (Validate(r.Target) is { } t) return t;
        if (folderRequired ? !IsValidPointFolder(r.Folder, r.PointId) : r.Folder is not null) return "invalid_backup_folder";
        return null;
    }

    public static string? Validate(RestoreBackupRequest r)
    {
        if (Validate(r.Source) is { } t) return t;
        if (!IsValidPointFolder(r.Folder, r.PointId)) return "invalid_backup_folder";
        if (!RestoreModes.All.Contains(r.Mode)) return "invalid_restore_mode";
        if (!RestoreNetworks.All.Contains(r.Network)) return "invalid_restore_network";
        if (r.Network == RestoreNetworks.Switch ? !IsValidSwitchName(r.SwitchName) : r.SwitchName is not null) return "invalid_switch_name";
        if (r.Mode == RestoreModes.Replace)
        {
            if (r.ReplaceVmId is null || r.ReplaceVmId == Guid.Empty) return "invalid_vm";
            if (r.NewName is not null || r.Location is not null) return "replace_keeps_name_and_location";
        }
        else
        {
            if (r.ReplaceVmId is not null) return "invalid_vm";
            if (!IsValidVmName(r.NewName)) return "invalid_vm_name";
            if (HostPaths.ValidateLocation(r.Location) is { } loc) return loc;
        }
        if (r.Mode == RestoreModes.Test && (r.Network != RestoreNetworks.Disconnected || r.StartAfter)) return "test_restore_is_isolated";
        if (r.TestTimeoutSeconds is < 60 or > 3600) return "invalid_timeout";
        return null;
    }

    public static string? Validate(RestoreDiskRequest r)
    {
        if (Validate(r.Source) is { } t) return t;
        if (!IsValidPointFolder(r.Folder, r.PointId)) return "invalid_backup_folder";
        if (!IsValidPointFile(r.Disk) || !IsVirtualDiskFile(r.Disk)) return "invalid_disk";
        if (HostPaths.ValidateLocation(r.DestinationFolder) is { } loc) return loc;
        if (r.AttachVmId == Guid.Empty) return "invalid_vm";
        return null;
    }

    public static string? Validate(MountBackupDiskRequest r)
    {
        if (Validate(r.Source) is { } t) return t;
        if (!IsValidPointFolder(r.Folder, r.PointId)) return "invalid_backup_folder";
        if (!IsValidPointFile(r.Disk) || !IsVirtualDiskFile(r.Disk)) return "invalid_disk";
        if (r.Source.IsShare && r.Source.UserName is not null && r.Source.StagingPath is null) return "staging_required";
        return null;
    }

    private static bool IsVirtualDiskFile(string path) =>
        path.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".vhd", StringComparison.OrdinalIgnoreCase);

    /// <summary>A path inside a mounted backup volume: "F:\" or "F:\folder\..." (the agent checks it belongs to the mount).</summary>
    public static bool IsValidMountedPath(string? path) => HostPaths.IsValidLocalFolder(path) || HostPaths.IsValidFilePath(path);

    public static string? Validate(BackupFileListRequest r) =>
        r.MountId == Guid.Empty ? "invalid_mount" : !HostPaths.IsValidLocalFolder(r.Path) ? "invalid_path" : null;

    public static string? Validate(BackupFileReadRequest r) =>
        r.MountId == Guid.Empty ? "invalid_mount"
        : !HostPaths.IsValidFilePath(r.Path) ? "invalid_path"
        : r.Offset < 0 || r.Length is < 1 or > FileTransfer.ChunkBytes ? "invalid_range" : null;

    public static string? Validate(MoveVmRequest r)
    {
        if (r.VmId == Guid.Empty) return "invalid_vm";
        if (!RemoteHosts.IsValidComputerName(r.DestinationHost)) return "invalid_destination_host";
        if (r.IncludeStorage)
        {
            if (r.DestinationPath is null) return "destination_path_required";
            if (HostPaths.ValidateLocation(r.DestinationPath) is { } loc) return loc;
        }
        else if (r.DestinationPath is not null)
        {
            return "destination_path_only_with_storage";
        }
        return null;
    }

    public static string? Validate(MoveVmStorageRequest r) =>
        r.VmId == Guid.Empty ? "invalid_vm"
        : r.DestinationPath is null ? "destination_path_required"
        : HostPaths.ValidateLocation(r.DestinationPath);

    public static string? Validate(NewFolderRequest r) =>
        !HostPaths.IsValidLocalFolder(r.Parent) ? "invalid_path"
        : HostPaths.IsBlocked(r.Parent) ? "location_not_allowed"
        : !HostPaths.IsValidFileName(r.Name) || r.Name.StartsWith('$') || r.Name.StartsWith('.') ? "invalid_folder_name"
        : HostPaths.Normalize(r.Parent).Length == 3 && HostPaths.IsBlockedRootFolder(r.Name) ? "location_not_allowed"
        : (HostPaths.Normalize(r.Parent).TrimEnd('\\') + @"\" + r.Name).Length > HostPaths.MaxPathLength ? "path_too_long" : null;

    public static string? Validate(BackupReadinessRequest r) =>
        r.Target is not null && Validate(r.Target) is { } t ? t
        : r.VmIds.Count > 500 || r.VmIds.Contains(Guid.Empty) ? "invalid_vm" : null;
}

/// <summary>
/// Allowed storage folders of a host (Install-Agent -StorageFolders, protocol 9): when the list is not empty, the portal
/// can use only paths inside these folders. Text containment here; the agent also checks the real file system.
/// </summary>
public static class StorageFolderRules
{
    public const int MaxFolders = 16;

    public static bool IsInside(string path, IReadOnlyList<string> folders)
    {
        if (folders.Count == 0)
        {
            return true;
        }
        if (path.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }
        var p = HostPaths.Normalize(path).TrimEnd('\\');
        foreach (var f in folders)
        {
            var root = HostPaths.Normalize(f).TrimEnd('\\');
            if (p.Equals(root, StringComparison.OrdinalIgnoreCase)
                || p.StartsWith(root + @"\", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Null when the list is acceptable (local folders, no drive roots, not system folders, no duplicates).</summary>
    public static string? Validate(IReadOnlyList<string> folders)
    {
        if (folders.Count > MaxFolders)
        {
            return "too_many_storage_folders";
        }
        foreach (var f in folders)
        {
            if (!HostPaths.IsValidLocalFolder(f) || HostPaths.IsBlocked(f) || f.Contains("..", StringComparison.Ordinal))
            {
                return "invalid_storage_folder";
            }
        }
        return folders.Select(f => HostPaths.Normalize(f).TrimEnd('\\')).Distinct(StringComparer.OrdinalIgnoreCase).Count() == folders.Count
            ? null : "duplicate_storage_folder";
    }
}
