using System.Management;
using System.Runtime.InteropServices;
using HvPortal.Contracts.HyperV;

namespace HvPortal.Agent.HyperV;

/// <summary>
/// Tells apart the reasons a WMI call fails (0.9.4): host not reachable, WMI / Hyper-V provider not available, timeout,
/// credentials refused, access denied, an error of the Hyper-V API. Pure classification from the exception (its type,
/// HRESULT and WMI status): the message says what to check, and the original error is kept in it and in the agent log.
/// </summary>
public static class WmiDiagnostics
{
    /// <param name="Code">One of <see cref="WmiProblemCodes"/>.</param>
    /// <param name="Summary">What happened and what to check, in plain words.</param>
    /// <param name="Technical">Exception type, HRESULT / WMI status and the original message.</param>
    public sealed record Problem(string Code, string Summary, string Technical)
    {
        /// <summary>"code: summary [technical]": the form sent to the gateway (HostInventory.AccessMessage, command messages).</summary>
        public string Message => $"{Code}: {Summary} [{Technical}]";

        /// <summary>Refused credentials and missing rights: the caller is not allowed, as opposed to "could not be done".</summary>
        public bool IsDenied => Code is WmiProblemCodes.Authentication or WmiProblemCodes.AccessDenied;
    }

    // HRESULTs of the Win32 / RPC / COM errors that reach .NET as COMException or UnauthorizedAccessException.
    private const int RpcServerUnavailable = unchecked((int)0x800706BA);
    private const int RpcCallFailed = unchecked((int)0x800706BE);
    private const int RpcCallFailedDne = unchecked((int)0x800706BF);
    private const int EndpointNotRegistered = unchecked((int)0x800706D9);
    private const int RpcServerTooBusy = unchecked((int)0x800706BB);
    private const int NetworkPathNotFound = unchecked((int)0x80070035);
    private const int NetworkUnreachable = unchecked((int)0x800704CF);
    private const int AccessDeniedWin32 = unchecked((int)0x80070005);
    private const int LogonFailure = unchecked((int)0x8007052E);
    private const int AccountRestriction = unchecked((int)0x8007052F);
    private const int InvalidLogonHours = unchecked((int)0x80070530);
    private const int PasswordExpired = unchecked((int)0x80070532);
    private const int AccountDisabled = unchecked((int)0x80070533);
    private const int LogonTypeNotGranted = unchecked((int)0x80070569);
    private const int PasswordMustChange = unchecked((int)0x80070773);
    private const int AccountLockedOut = unchecked((int)0x80070775);
    private const int Win32Timeout = unchecked((int)0x800705B4);
    private const int ServiceDisabled = unchecked((int)0x80070422);
    private const int ServiceNotActive = unchecked((int)0x80070426);
    private const int WbemTimedOut = unchecked((int)0x80041069);
    private const int WbemAccessDenied = unchecked((int)0x80041003);
    private const int WbemInvalidNamespace = unchecked((int)0x8004100E);
    private const int WbemInvalidClass = unchecked((int)0x80041010);
    private const int WbemProviderNotFound = unchecked((int)0x80041011);
    private const int WbemProviderLoadFailure = unchecked((int)0x80041013);
    private const int WbemInitializationFailure = unchecked((int)0x80041014);
    private const int WbemTransportFailure = unchecked((int)0x80041015);
    private const int WbemShuttingDown = unchecked((int)0x80041033);

    /// <param name="ex">What the WMI call threw.</param>
    /// <param name="host">Name of the host for the message ("this host" for the local one).</param>
    /// <param name="remote">The host is reached over the network by a management agent (with stored credentials).</param>
    /// <param name="account">Remote hosts: the account used.</param>
    /// <param name="ns">WMI namespace the call was for (root\virtualization\v2, root\cimv2, ...).</param>
    public static Problem Classify(Exception ex, string host, bool remote, string? account = null, string? ns = null)
    {
        var code = CodeFor(ex);
        var where = remote ? host : "this host";
        var summary = code switch
        {
            WmiProblemCodes.Unreachable => remote
                ? $"{where} does not answer: check that it is on, that its name resolves, and that the firewall lets WMI through (TCP 135 and the dynamic RPC ports, rule group 'Windows Management Instrumentation')."
                : "The WMI service of this host does not answer: check that the service 'Windows Management Instrumentation' (Winmgmt) is running.",
            WmiProblemCodes.ProviderUnavailable => IsNamespaceOrClass(ex) && IsVirtualization(ns)
                ? $"The Hyper-V WMI provider is not available on {where} ({ns}): the Hyper-V role is not installed, or its management service (vmms) is not running."
                : $"WMI is not available on {where}{(ns is null ? "" : $" ({ns})")}: the WMI service or its provider is stopped, disabled or failed to load. Restart the service 'Windows Management Instrumentation'; if the error stays, check the WMI repository (winmgmt /verifyrepository).",
            WmiProblemCodes.Timeout => $"{where} did not answer in time: it is overloaded, or the network between the agent and the host drops packets. The request was not confirmed.",
            WmiProblemCodes.Authentication => remote
                ? $"{where} refused the credentials of {account ?? "the stored account"}: wrong user name or password, or the account is locked, disabled or expired. Update the credentials of the host in the portal."
                : "Windows refused the identity of the agent service: check the account of the service HvPortalAgent.",
            WmiProblemCodes.AccessDenied => remote
                ? $"{where} accepted {account ?? "the account"} but denies access: it must be an administrator of the host; a local account other than the built-in Administrator also needs LocalAccountTokenFilterPolicy (docs/install.md)."
                : "Access denied: the agent service account must be a member of 'Hyper-V Administrators' on this host (Install-Agent.ps1 adds it); a restart of the service applies a new membership.",
            WmiProblemCodes.HyperV => $"Hyper-V on {where} returned an error.",
            _ => $"WMI error on {where}.",
        };
        return new Problem(code, summary, Technical(ex));
    }

    /// <summary>The kind of problem, from the exception alone.</summary>
    public static string CodeFor(Exception ex)
    {
        switch (ex)
        {
            case TimeoutException or OperationCanceledException:
                return WmiProblemCodes.Timeout;
            case ManagementException me:
                return me.ErrorCode switch
                {
                    ManagementStatus.AccessDenied => WmiProblemCodes.AccessDenied,
                    ManagementStatus.Timedout or ManagementStatus.CallCanceled => WmiProblemCodes.Timeout,
                    ManagementStatus.InvalidNamespace or ManagementStatus.InvalidClass or ManagementStatus.ProviderNotFound
                        or ManagementStatus.ProviderLoadFailure or ManagementStatus.InitializationFailure or ManagementStatus.ProviderNotCapable
                        or ManagementStatus.ShuttingDown or ManagementStatus.TransportFailure => WmiProblemCodes.ProviderUnavailable,
                    ManagementStatus.Failed or ManagementStatus.ProviderFailure or ManagementStatus.NotFound or ManagementStatus.InvalidParameter
                        or ManagementStatus.InvalidMethod or ManagementStatus.InvalidMethodParameters or ManagementStatus.MethodNotImplemented
                        or ManagementStatus.NotSupported or ManagementStatus.InvalidOperation => WmiProblemCodes.HyperV,
                    _ => FromHResult(me.HResult) ?? WmiProblemCodes.Other,
                };
            case UnauthorizedAccessException:
                // Wrong credentials also surface as "unauthorized": the HRESULT tells them apart from a missing right.
                return FromHResult(ex.HResult) ?? WmiProblemCodes.AccessDenied;
            default:
                return FromHResult(ex.HResult) ?? WmiProblemCodes.Other;
        }
    }

    private static string? FromHResult(int hresult) => hresult switch
    {
        RpcServerUnavailable or RpcCallFailed or RpcCallFailedDne or RpcServerTooBusy or NetworkPathNotFound or NetworkUnreachable => WmiProblemCodes.Unreachable,
        EndpointNotRegistered or ServiceDisabled or ServiceNotActive or WbemInvalidNamespace or WbemInvalidClass or WbemProviderNotFound
            or WbemProviderLoadFailure or WbemInitializationFailure or WbemTransportFailure or WbemShuttingDown => WmiProblemCodes.ProviderUnavailable,
        Win32Timeout or WbemTimedOut => WmiProblemCodes.Timeout,
        LogonFailure or AccountRestriction or InvalidLogonHours or PasswordExpired or AccountDisabled or LogonTypeNotGranted
            or PasswordMustChange or AccountLockedOut => WmiProblemCodes.Authentication,
        AccessDeniedWin32 or WbemAccessDenied => WmiProblemCodes.AccessDenied,
        _ => null,
    };

    private static bool IsNamespaceOrClass(Exception ex) =>
        ex is ManagementException { ErrorCode: ManagementStatus.InvalidNamespace or ManagementStatus.InvalidClass }
        || ex.HResult is WbemInvalidNamespace or WbemInvalidClass;

    private static bool IsVirtualization(string? ns) => ns is not null && ns.Contains("virtualization", StringComparison.OrdinalIgnoreCase);

    /// <summary>The original error, complete enough to search for: type, HRESULT, WMI status, message.</summary>
    public static string Technical(Exception ex)
    {
        var status = ex is ManagementException me ? $" {me.ErrorCode}" : "";
        var hresult = ex is COMException or ManagementException or UnauthorizedAccessException ? $" 0x{ex.HResult:X8}" : "";
        var message = ex.Message.ReplaceLineEndings(" ").Trim();
        return $"{ex.GetType().Name}{hresult}{status}: {(message.Length > 400 ? message[..400] : message)}";
    }
}
