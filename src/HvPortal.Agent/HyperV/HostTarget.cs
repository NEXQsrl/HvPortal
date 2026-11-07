using System.Management;
using System.Runtime.InteropServices;
using System.Security;
using HvPortal.Contracts.Agent;
using HvPortal.Contracts.HyperV;

namespace HvPortal.Agent.HyperV;

/// <summary>
/// The Hyper-V host an agent component talks to: the local machine (host agent) or a remote host reached by a
/// management agent with explicit credentials. The password is held in a SecureString and handed out only to the
/// Windows APIs that need it (WMI ConnectionOptions, LogonUser, the remote launcher's stdin).
/// </summary>
public sealed class HostTarget : IDisposable
{
    public static readonly HostTarget Local = new(null, null, null, false);

    private readonly SecureString? _password;

    private HostTarget(string? computerName, string? userName, SecureString? password, bool useHttps, IReadOnlyList<string>? storageFolders = null)
    {
        ComputerName = computerName;
        UserName = userName;
        _password = password;
        UseHttps = useHttps;
        StorageFolders = storageFolders ?? [];
    }

    /// <summary>Remote host: the folders the portal may use there (from the gateway). The local host uses LocalStorageFolders.</summary>
    public IReadOnlyList<string> StorageFolders { get; }

    /// <summary>Null for the local machine.</summary>
    public string? ComputerName { get; }

    /// <summary>Qualified account name (HOST\user, DOMAIN\user or UPN); null for the local machine.</summary>
    public string? UserName { get; }

    public bool UseHttps { get; }

    public bool IsLocal => ComputerName is null;

    /// <summary>Text for log and error messages (never contains credentials).</summary>
    public string Display => ComputerName ?? Environment.MachineName;

    /// <exception cref="ArgumentException">The target is not acceptable (validated again here, defense in depth).</exception>
    public static HostTarget FromRemote(RemoteTarget t)
    {
        if (RemoteHosts.Validate(t.ComputerName, t.UserName, t.Password, passwordRequired: true) is { } error)
        {
            throw new ArgumentException("Invalid remote target: " + error);
        }
        var secure = new SecureString();
        foreach (var c in t.Password)
        {
            secure.AppendChar(c);
        }
        secure.MakeReadOnly();
        var folders = (t.StorageFolders ?? []).Select(HostPaths.Normalize).ToList();
        if (StorageFolderRules.Validate(folders) is { } badFolders)
        {
            throw new ArgumentException("Invalid remote target: " + badFolders);
        }
        return new HostTarget(t.ComputerName, RemoteHosts.QualifiedUserName(t.ComputerName, t.UserName), secure, t.UseHttps, folders);
    }

    /// <summary>WMI namespace path on the target, e.g. \\hv01\root\virtualization\v2.</summary>
    public string WmiPath(string relativeNamespace) => $@"\\{ComputerName ?? "."}\{relativeNamespace}";

    /// <summary>Connected scope. Local: the agent's own identity. Remote: explicit credentials, packet privacy (encrypted DCOM).</summary>
    public ManagementScope ConnectWmi(string relativeNamespace, TimeSpan? timeout = null)
    {
        var options = new ConnectionOptions
        {
            Impersonation = ImpersonationLevel.Impersonate,
            Authentication = AuthenticationLevel.PacketPrivacy,
            Timeout = timeout ?? TimeSpan.FromSeconds(30),
        };
        if (!IsLocal)
        {
            options.Username = UserName;
            options.SecurePassword = _password;
        }
        var scope = new ManagementScope(WmiPath(relativeNamespace), options);
        scope.Connect();
        return scope;
    }

    /// <summary>Plain-text password for a short-lived use (LogonUser, launcher stdin). Callers must not keep or log it.</summary>
    internal string RevealPassword()
    {
        if (_password is null)
        {
            throw new InvalidOperationException("The local target has no credentials.");
        }
        var ptr = Marshal.SecureStringToGlobalAllocUnicode(_password);
        try
        {
            return Marshal.PtrToStringUni(ptr) ?? "";
        }
        finally
        {
            Marshal.ZeroFreeGlobalAllocUnicode(ptr);
        }
    }

    /// <summary>True when both describe the same connection (used to keep live sessions when the configuration is re-sent).</summary>
    public bool SameAs(RemoteTarget t)
    {
        if (IsLocal || !string.Equals(ComputerName, t.ComputerName, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(UserName, RemoteHosts.QualifiedUserName(t.ComputerName, t.UserName), StringComparison.OrdinalIgnoreCase)
            || UseHttps != t.UseHttps
            || !StorageFolders.SequenceEqual((t.StorageFolders ?? []).Select(HostPaths.Normalize), StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }
        return CryptographicOperationsEquals(RevealPassword(), t.Password);
    }

    private static bool CryptographicOperationsEquals(string a, string b) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));

    public void Dispose() => _password?.Dispose();
}
