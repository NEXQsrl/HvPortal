using System.Net;
using System.Text.RegularExpressions;

namespace HvPortal.Contracts.HyperV;

/// <summary>
/// Rules for remote Hyper-V hosts managed through a management agent (protocol 6). Shared by the gateway (input
/// validation) and the agent (defense in depth before any connection is attempted).
/// </summary>
public static partial class RemoteHosts
{
    public const int MaxPasswordLength = 256;

    /// <summary>DNS host name (labels of letters, digits and hyphens) or a literal IPv4 address. No ports, no paths.</summary>
    [GeneratedRegex(@"^(?=.{1,253}$)[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$", RegexOptions.CultureInvariant)]
    private static partial Regex HostNameRegex();

    /// <summary>"user", "HOST\user", "DOMAIN\user" or "user@domain.tld". No control characters or wildcard/quote characters.</summary>
    [GeneratedRegex(@"^(?:[A-Za-z0-9][A-Za-z0-9.-]{0,62}\\)?[^\x00-\x1f\\/""\[\]:;|=+*?<>@]{1,104}(?:@[A-Za-z0-9.-]{1,253})?$", RegexOptions.CultureInvariant)]
    private static partial Regex UserNameRegex();

    public static bool IsIpAddress(string name) => IPAddress.TryParse(name, out _);

    /// <summary>
    /// Accepts DNS names and dotted IPv4 addresses, never loopback (a local host uses its own agent). IPv6 literals are
    /// refused (WinRM and UNC paths need special forms for them): use a DNS name.
    /// </summary>
    public static bool IsValidComputerName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 253 || !HostNameRegex().IsMatch(name)
            || name.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (name.All(c => char.IsAsciiDigit(c) || c == '.'))
        {
            // Numeric: must be a complete dotted IPv4 address (IPAddress.TryParse also accepts "10" or "10.1").
            return name.Split('.').Length == 4 && IPAddress.TryParse(name, out var ip) && !IPAddress.IsLoopback(ip) && !ip.Equals(IPAddress.Any)
                   && !ip.Equals(IPAddress.Broadcast);
        }
        return true;
    }

    public static bool IsValidUserName(string? user) => user is { Length: >= 1 and <= 256 } && UserNameRegex().IsMatch(user) && user.Trim() == user;

    public static bool IsValidPassword(string? password) => password is { Length: >= 1 and <= MaxPasswordLength } && !password.Contains('\0', StringComparison.Ordinal);

    /// <summary>
    /// The account name used for WMI, WinRM and SMB. A bare "user" is a local account of the target: it is qualified
    /// with the target's NetBIOS-style name (first DNS label). With an IP address the target name is unknown, so the
    /// user must be given as "HOST\user" (see <see cref="Validate"/>).
    /// </summary>
    public static string QualifiedUserName(string computerName, string user)
    {
        if (user.Contains('\\', StringComparison.Ordinal) || user.Contains('@', StringComparison.Ordinal))
        {
            return user;
        }
        var host = computerName.Split('.')[0];
        return host.ToUpperInvariant() + "\\" + user;
    }

    /// <summary>(domain, user) as LogonUser expects them.</summary>
    public static (string? Domain, string User) SplitUserName(string qualified)
    {
        var i = qualified.IndexOf('\\', StringComparison.Ordinal);
        return i < 0 ? (null, qualified) : (qualified[..i], qualified[(i + 1)..]);
    }

    /// <summary>Null when acceptable, otherwise an error code. Password is optional when updating (null = keep).</summary>
    public static string? Validate(string? computerName, string? userName, string? password, bool passwordRequired)
    {
        if (!IsValidComputerName(computerName))
        {
            return "invalid_computer_name";
        }
        if (!IsValidUserName(userName))
        {
            return "invalid_user_name";
        }
        if (IsIpAddress(computerName!) && !userName!.Contains('\\', StringComparison.Ordinal) && !userName.Contains('@', StringComparison.Ordinal))
        {
            return "user_name_needs_host";
        }
        if (password is null ? passwordRequired : !IsValidPassword(password))
        {
            return "invalid_password";
        }
        return null;
    }
}
