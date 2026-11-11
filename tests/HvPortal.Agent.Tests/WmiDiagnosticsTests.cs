using System.Runtime.InteropServices;
using HvPortal.Agent.HyperV;
using HvPortal.Contracts.HyperV;

namespace HvPortal.Agent.Tests;

/// <summary>
/// 0.9.4: WMI failures are told apart (unreachable, provider not available, timeout, credentials, access denied, Hyper-V
/// error) from the exception alone; the message keeps the original error. Classification only: what a real host
/// actually throws in each situation is in the real-host test plan.
/// </summary>
public sealed class WmiDiagnosticsTests
{
    private static COMException Com(uint hresult, string message = "boom") => new(message, unchecked((int)hresult));

    [Theory]
    [InlineData(0x800706BA, WmiProblemCodes.Unreachable)]          // RPC server unavailable
    [InlineData(0x800706BE, WmiProblemCodes.Unreachable)]          // remote procedure call failed
    [InlineData(0x80070035, WmiProblemCodes.Unreachable)]          // network path not found
    [InlineData(0x800706D9, WmiProblemCodes.ProviderUnavailable)]  // no endpoint: the WMI service is not listening
    [InlineData(0x80070422, WmiProblemCodes.ProviderUnavailable)]  // service disabled
    [InlineData(0x8004100E, WmiProblemCodes.ProviderUnavailable)]  // invalid namespace
    [InlineData(0x80041013, WmiProblemCodes.ProviderUnavailable)]  // provider load failure
    [InlineData(0x80041069, WmiProblemCodes.Timeout)]
    [InlineData(0x800705B4, WmiProblemCodes.Timeout)]
    [InlineData(0x8007052E, WmiProblemCodes.Authentication)]       // logon failure
    [InlineData(0x80070775, WmiProblemCodes.Authentication)]       // account locked out
    [InlineData(0x80070533, WmiProblemCodes.Authentication)]       // account disabled
    [InlineData(0x80070005, WmiProblemCodes.AccessDenied)]
    [InlineData(0x80041003, WmiProblemCodes.AccessDenied)]
    [InlineData(0x80004005, WmiProblemCodes.Other)]                // unspecified: not guessed
    public void Com_errors_are_classified_by_their_hresult(uint hresult, string expected) =>
        Assert.Equal(expected, WmiDiagnostics.CodeFor(Com(hresult)));

    [Fact]
    public void Timeouts_and_refused_identities_are_recognised_whatever_throws_them()
    {
        Assert.Equal(WmiProblemCodes.Timeout, WmiDiagnostics.CodeFor(new TimeoutException()));
        Assert.Equal(WmiProblemCodes.Timeout, WmiDiagnostics.CodeFor(new OperationCanceledException()));
        // "Unauthorized" without a known HRESULT is a missing right; with a logon-failure HRESULT it is the credentials.
        Assert.Equal(WmiProblemCodes.AccessDenied, WmiDiagnostics.CodeFor(new UnauthorizedAccessException("denied")));
        Assert.Equal(WmiProblemCodes.Authentication, WmiDiagnostics.CodeFor(new UnauthorizedAccessException("logon") { HResult = unchecked((int)0x8007052E) }));
        Assert.Equal(WmiProblemCodes.Other, WmiDiagnostics.CodeFor(new InvalidOperationException("x")));
    }

    [Fact]
    public void The_message_says_what_to_check_and_keeps_the_original_error()
    {
        var remote = WmiDiagnostics.Classify(Com(0x800706BA, "The RPC server is unavailable."), "hv07.lab.local", remote: true, @"hv07\portal", @"root\cimv2");
        Assert.Equal(WmiProblemCodes.Unreachable, remote.Code);
        Assert.StartsWith("wmi_unreachable: hv07.lab.local does not answer", remote.Message, StringComparison.Ordinal);
        Assert.Contains("TCP 135", remote.Message, StringComparison.Ordinal);
        Assert.Contains("[COMException 0x800706BA: The RPC server is unavailable.]", remote.Message, StringComparison.Ordinal);
        Assert.False(remote.IsDenied);
        // The gateway reads the code back from the message, and only known codes.
        Assert.Equal(WmiProblemCodes.Unreachable, WmiProblemCodes.CodeOf(remote.Message));
        Assert.Null(WmiProblemCodes.CodeOf("Access denied to root\\virtualization\\v2."));
        Assert.Null(WmiProblemCodes.CodeOf("something_else: text"));
        Assert.Null(WmiProblemCodes.CodeOf(null));

        var credentials = WmiDiagnostics.Classify(Com(0x8007052E), "hv07", remote: true, @"hv07\portal");
        Assert.True(credentials.IsDenied);
        Assert.Contains(@"hv07\portal", credentials.Summary, StringComparison.Ordinal);
        Assert.Contains("Update the credentials", credentials.Summary, StringComparison.Ordinal);

        var local = WmiDiagnostics.Classify(new UnauthorizedAccessException("Access is denied."), "local", remote: false, ns: @"root\virtualization\v2");
        Assert.Equal(WmiProblemCodes.AccessDenied, local.Code);
        Assert.Contains("Hyper-V Administrators", local.Summary, StringComparison.Ordinal);

        // A missing Hyper-V namespace is the role or vmms, not "WMI is broken".
        var noRole = WmiDiagnostics.Classify(Com(0x8004100E), "local", remote: false, ns: @"root\virtualization\v2");
        Assert.Contains("Hyper-V role is not installed", noRole.Summary, StringComparison.Ordinal);
        var noCim = WmiDiagnostics.Classify(Com(0x80041013), "local", remote: false, ns: @"root\cimv2");
        Assert.Contains("winmgmt /verifyrepository", noCim.Summary, StringComparison.Ordinal);

        // Long or multi-line provider messages do not flood the portal: one line, cut; never a password (none is ever in a WMI error).
        var long_ = WmiDiagnostics.Technical(Com(0x80004005, new string('x', 2000) + "\r\nsecond line"));
        Assert.True(long_.Length < 460);
        Assert.DoesNotContain('\n', long_);
    }
}
