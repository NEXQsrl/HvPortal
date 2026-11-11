namespace HvPortal.Contracts.HyperV;

/// <summary>Validation of the protocol 13 requests (rename / delete in the file explorer). Same rules on the gateway and on the agent.</summary>
public static partial class OperationValidation
{
    /// <summary>
    /// A file or folder the explorer may rename or delete, by its text: a valid local path that is not a drive root, not
    /// inside (or itself) a system folder, not a name starting with '$' or '.', not a Hyper-V file. Null when acceptable.
    /// </summary>
    public static string? ValidateEntryPath(string? path)
    {
        if (path is null || (!HostPaths.IsValidLocalFolder(path) && !HostPaths.IsValidFilePath(path)))
        {
            return "invalid_path";
        }
        var normalized = HostPaths.Normalize(path);
        if (normalized.Length <= 3)
        {
            return "location_not_allowed"; // a drive
        }
        if (HostPaths.IsBlocked(normalized))
        {
            return "location_not_allowed";
        }
        var name = normalized[(normalized.LastIndexOf('\\') + 1)..];
        if (name.StartsWith('$') || name.StartsWith('.'))
        {
            return "location_not_allowed";
        }
        return ProtectedHostFiles.IsProtected(name) ? "protected_file" : null;
    }

    public static string? Validate(DeletePathRequest r) => ValidateEntryPath(r.Path);

    public static string? Validate(RenamePathRequest r)
    {
        if (ValidateEntryPath(r.Path) is { } invalid)
        {
            return invalid;
        }
        if (!HostPaths.IsValidFileName(r.NewName) || r.NewName.StartsWith('$') || r.NewName.StartsWith('.'))
        {
            return "invalid_file_name";
        }
        if (ProtectedHostFiles.IsProtected(r.NewName))
        {
            return "protected_file";
        }
        var normalized = HostPaths.Normalize(r.Path);
        var cut = normalized.LastIndexOf('\\');
        var parent = cut == 2 ? normalized[..3] : normalized[..cut];
        if (string.Equals(normalized[(cut + 1)..], r.NewName, StringComparison.Ordinal))
        {
            return "same_name";
        }
        if (parent.Length == 3 && HostPaths.IsBlockedRootFolder(r.NewName))
        {
            return "location_not_allowed";
        }
        return (parent.TrimEnd('\\') + @"\" + r.NewName).Length > HostPaths.MaxPathLength ? "path_too_long" : null;
    }
}
