using System.Security.AccessControl;
using System.Security.Principal;

namespace HvPortal.Shared;

/// <summary>
/// Creates directories and files readable only by SYSTEM, BUILTIN\Administrators and the
/// identity of the current process (e.g. the service virtual account NT SERVICE\HvPortalGateway).
/// Uses native Windows ACLs; inheritance from the parent is removed.
/// </summary>
internal static class SecureFileSystem
{
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    public static DirectoryInfo EnsurePrivateDirectory(string path, params SecurityIdentifier[] additionalFullControl)
    {
        var dir = Directory.CreateDirectory(path);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in Principals(additionalFullControl))
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }
        try
        {
            security.SetOwner(CurrentUser());
        }
        catch (InvalidOperationException)
        {
            // Not allowed to change owner; the protected DACL is the security boundary.
        }

        try
        {
            dir.SetAccessControl(security);
        }
        catch (InvalidOperationException)
        {
            security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            foreach (var sid in Principals(additionalFullControl))
            {
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            }
            dir.SetAccessControl(security);
        }
        return dir;
    }

    /// <summary>Writes a file atomically (temp + replace). The file inherits the private directory ACL.</summary>
    public static void WriteAllBytesAtomic(string path, ReadOnlySpan<byte> content)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var tmp = Path.Combine(dir, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            fs.Write(content);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    public static void WriteAllTextAtomic(string path, string content) =>
        WriteAllBytesAtomic(path, System.Text.Encoding.UTF8.GetBytes(content));

    /// <summary>True if the path (or any parent up to the root) is a reparse point; used to refuse symlink/junction tricks.</summary>
    public static bool ContainsReparsePoint(string path)
    {
        var current = new DirectoryInfo(Path.GetFullPath(path));
        while (current is not null)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return true;
            }
            current = current.Parent;
        }
        return false;
    }

    /// <summary>
    /// Windows service name whose virtual account (NT SERVICE\&lt;name&gt;) must keep access, also when an
    /// administrator runs a CLI command that rewrites the ACL. Set once at process start.
    /// </summary>
    public static string? ServiceName { get; set; }

    private static SecurityIdentifier? ServiceSid()
    {
        if (string.IsNullOrEmpty(ServiceName))
        {
            return null;
        }
        try
        {
            return (SecurityIdentifier)new NTAccount("NT SERVICE", ServiceName).Translate(typeof(SecurityIdentifier));
        }
        catch (IdentityNotMappedException)
        {
            return null; // service not installed (development)
        }
    }

    private static IEnumerable<SecurityIdentifier> Principals(SecurityIdentifier[] additional)
    {
        var set = new HashSet<SecurityIdentifier> { LocalSystem, Administrators, CurrentUser() };
        if (ServiceSid() is { } svc)
        {
            set.Add(svc);
        }
        foreach (var s in additional)
        {
            set.Add(s);
        }
        return set;
    }

    private static SecurityIdentifier CurrentUser()
    {
        using var id = WindowsIdentity.GetCurrent();
        return id.User ?? throw new InvalidOperationException("Cannot resolve current process SID.");
    }
}
