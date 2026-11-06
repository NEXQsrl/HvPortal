using System.Collections.Concurrent;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using HvPortal.Contracts.HyperV;
using Microsoft.Win32.SafeHandles;

namespace HvPortal.Agent.HyperV;

/// <summary>
/// How the folder browser and the file explorer reach the host's file system. Paths exchanged with the gateway are
/// always host-local ("D:\VMs"); <see cref="ToPhysical"/> maps them to what this process opens:
///  - local host: the same path;
///  - remote host (management agent): the administrative share \\host\D$\VMs, opened with the host credentials
///    (LogonUser LOGON32_LOGON_NEW_CREDENTIALS: they are used only for outbound network access).
/// All folder rules (fixed drives, no hidden/system/reparse entries, blocked top-level folders) are applied on the
/// host-local path and on what the host reports for the physical path, exactly as for a local agent.
/// </summary>
public abstract class HostFileAccess
{
    public static readonly HostFileAccess Local = new LocalHostFileAccess();

    public abstract bool IsLocal { get; }

    /// <summary>Host-local path (validated syntax) to the path opened by this process.</summary>
    public abstract string ToPhysical(string hostPath);

    /// <summary>Inverse of <see cref="ToPhysical"/>.</summary>
    /// <exception cref="HostPathException">The path is outside the host's administrative shares.</exception>
    public abstract string ToHost(string physicalPath);

    public abstract IReadOnlyList<DriveEntry> FixedDrives();

    /// <summary>True when "D:\" is a ready, local fixed drive of the host.</summary>
    public abstract bool IsFixedDrive(string root);

    public abstract long? FreeSpace(string root);

    /// <summary>Runs file-system code with the identity that must access the host (impersonation for remote hosts).</summary>
    public virtual T Run<T>(Func<T> action) => action();

    public virtual Task<T> RunAsync<T>(Func<Task<T>> action) => action();

    public void Run(Action action) => Run(() =>
    {
        action();
        return 0;
    });
}

internal sealed class LocalHostFileAccess : HostFileAccess
{
    public override bool IsLocal => true;

    public override string ToPhysical(string hostPath) => hostPath;

    public override string ToHost(string physicalPath) => physicalPath;

    public override IReadOnlyList<DriveEntry> FixedDrives()
    {
        var list = new List<DriveEntry>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType == DriveType.Fixed && d.IsReady)
                {
                    list.Add(new DriveEntry(d.RootDirectory.FullName, string.IsNullOrWhiteSpace(d.VolumeLabel) ? null : d.VolumeLabel, d.TotalSize, d.AvailableFreeSpace));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Drive vanished or locked (e.g. BitLocker): skip it.
            }
        }
        return list;
    }

    public override bool IsFixedDrive(string root)
    {
        try
        {
            var drive = new DriveInfo(root);
            return drive.DriveType == DriveType.Fixed && drive.IsReady;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public override long? FreeSpace(string root)
    {
        try
        {
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>Remote host through its administrative shares (X$). Requires SMB (TCP 445) and an administrator account.</summary>
public sealed class RemoteHostFileAccess : HostFileAccess, IDisposable
{
    private const int Logon32LogonNewCredentials = 9;
    private const int Logon32ProviderWinnt50 = 3;
    private static readonly TimeSpan DriveCacheTtl = TimeSpan.FromSeconds(60);

    private readonly HostTarget _target;
    private readonly string _uncHost;
    private readonly object _tokenGate = new();
    private SafeAccessTokenHandle? _token;
    private (DateTime At, HashSet<string> Roots)? _fixedRoots;

    public RemoteHostFileAccess(HostTarget target)
    {
        if (target.IsLocal)
        {
            throw new ArgumentException("Use HostFileAccess.Local for the local machine.", nameof(target));
        }
        _target = target;
        // DNS name or dotted IPv4 (RemoteHosts.IsValidComputerName): usable as is in a UNC path.
        _uncHost = target.ComputerName!;
    }

    public override bool IsLocal => false;

    public override string ToPhysical(string hostPath)
    {
        if (hostPath.Length < 3 || !char.IsAsciiLetter(hostPath[0]) || hostPath[1] != ':' || hostPath[2] != '\\')
        {
            throw new HostPathException("Invalid folder: use a local path such as D:\\VMs.");
        }
        return $@"\\{_uncHost}\{char.ToUpperInvariant(hostPath[0])}$\{hostPath[3..]}";
    }

    public override string ToHost(string physicalPath)
    {
        var prefix = $@"\\{_uncHost}\";
        if (!physicalPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || physicalPath.Length < prefix.Length + 2
            || !char.IsAsciiLetter(physicalPath[prefix.Length]) || physicalPath[prefix.Length + 1] != '$')
        {
            throw new HostPathException("Path outside the host's administrative shares.");
        }
        var rest = physicalPath[(prefix.Length + 2)..].TrimStart('\\');
        return $@"{char.ToUpperInvariant(physicalPath[prefix.Length])}:\{rest}";
    }

    public override IReadOnlyList<DriveEntry> FixedDrives()
    {
        var list = new List<DriveEntry>();
        var scope = _target.ConnectWmi(@"root\cimv2");
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(
            "SELECT DeviceID, VolumeName, Size, FreeSpace FROM Win32_LogicalDisk WHERE DriveType = 3"));
        using var results = searcher.Get();
        foreach (ManagementBaseObject o in results)
        {
            using (o)
            {
                if (o["DeviceID"] is string id && id.Length == 2 && char.IsAsciiLetter(id[0]) && id[1] == ':'
                    && o["Size"] is ulong size && o["FreeSpace"] is ulong free)
                {
                    var label = o["VolumeName"] as string;
                    list.Add(new DriveEntry(char.ToUpperInvariant(id[0]) + @":\", string.IsNullOrWhiteSpace(label) ? null : label, (long)size, (long)free));
                }
            }
        }
        _fixedRoots = (DateTime.UtcNow, list.Select(d => d.Path).ToHashSet(StringComparer.OrdinalIgnoreCase));
        return list.OrderBy(d => d.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public override bool IsFixedDrive(string root)
    {
        if (_fixedRoots is not { } cached || DateTime.UtcNow - cached.At > DriveCacheTtl)
        {
            FixedDrives();
        }
        return _fixedRoots!.Value.Roots.Contains(root);
    }

    public override long? FreeSpace(string root)
    {
        if (root.Length < 2 || !char.IsAsciiLetter(root[0]))
        {
            return null;
        }
        try
        {
            var scope = _target.ConnectWmi(@"root\cimv2");
            // The drive letter is a single validated ASCII letter: nothing else is interpolated.
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(
                $"SELECT FreeSpace FROM Win32_LogicalDisk WHERE DeviceID = '{char.ToUpperInvariant(root[0])}:'"));
            using var results = searcher.Get();
            foreach (ManagementBaseObject o in results)
            {
                using (o)
                {
                    return o["FreeSpace"] is ulong free ? (long)free : null;
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException)
        {
        }
        return null;
    }

    public override T Run<T>(Func<T> action) => WindowsIdentity.RunImpersonated(Token(), action);

    public override Task<T> RunAsync<T>(Func<Task<T>> action) => WindowsIdentity.RunImpersonatedAsync(Token(), action);

    private SafeAccessTokenHandle Token()
    {
        lock (_tokenGate)
        {
            if (_token is { IsInvalid: false, IsClosed: false })
            {
                return _token;
            }
            var (domain, user) = RemoteHosts.SplitUserName(_target.UserName!);
            if (!LogonUser(user, domain, _target.RevealPassword(), Logon32LogonNewCredentials, Logon32ProviderWinnt50, out var token))
            {
                throw new UnauthorizedAccessException($"Cannot prepare the credentials for {_target.Display} (LogonUser error {Marshal.GetLastWin32Error()}).");
            }
            _token = token;
            return token;
        }
    }

    public void Dispose()
    {
        lock (_tokenGate)
        {
            _token?.Dispose();
            _token = null;
        }
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogonUser(string lpszUsername, string? lpszDomain, string lpszPassword, int dwLogonType, int dwLogonProvider,
        out SafeAccessTokenHandle phToken);
}
