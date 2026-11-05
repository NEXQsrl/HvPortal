using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HvPortal.Agent.Commands;
using HvPortal.Agent.HyperV;
using HvPortal.Agent.Identity;
using HvPortal.Contracts.Agent;
using HvPortal.Contracts.HyperV;
using Microsoft.Extensions.Logging.Abstractions;

namespace HvPortal.Agent.Tests;

public class PowerStateMappingTests
{
    [Theory]
    [InlineData(2, VmPowerState.Running, false)]
    [InlineData(3, VmPowerState.Off, false)]
    [InlineData(6, VmPowerState.Saved, false)]
    [InlineData(9, VmPowerState.Paused, false)]
    [InlineData(32768, VmPowerState.Paused, false)]
    [InlineData(32769, VmPowerState.Saved, false)]
    [InlineData(32770, VmPowerState.Starting, false)]
    [InlineData(32774, VmPowerState.Stopping, false)]
    [InlineData(32781, VmPowerState.Running, true)]
    [InlineData(32782, VmPowerState.Off, true)]
    [InlineData(12345, VmPowerState.Unknown, false)]
    public void Maps_documented_enabled_states(int raw, VmPowerState expected, bool critical)
    {
        Assert.Equal(expected, VmPowerStateMapper.Map((ushort)raw, out var c));
        Assert.Equal(critical, c);
    }

    [Theory]
    [InlineData(VmPowerOperation.Start, VmPowerState.Off, true)]
    [InlineData(VmPowerOperation.Start, VmPowerState.Saved, true)]
    [InlineData(VmPowerOperation.Start, VmPowerState.Running, false)]
    [InlineData(VmPowerOperation.Shutdown, VmPowerState.Off, false)]
    [InlineData(VmPowerOperation.TurnOff, VmPowerState.Saved, false)]
    [InlineData(VmPowerOperation.Resume, VmPowerState.Paused, true)]
    [InlineData(VmPowerOperation.Save, VmPowerState.Paused, true)]
    [InlineData(VmPowerOperation.Reset, VmPowerState.Off, false)]
    public void Operation_availability_depends_on_state(VmPowerOperation op, VmPowerState state, bool allowed) =>
        Assert.Equal(allowed, VmPowerRules.IsAllowed(op, state));

    [Fact]
    public void Only_turnoff_and_reset_are_destructive() =>
        Assert.Equal([VmPowerOperation.TurnOff, VmPowerOperation.Reset], Enum.GetValues<VmPowerOperation>().Where(VmPowerRules.IsDestructive));

    [Fact]
    public void Requested_state_codes_match_wmi_documentation()
    {
        Assert.Equal((ushort)2, VmPowerRules.RequestedStateFor(VmPowerOperation.Start));
        Assert.Equal((ushort)3, VmPowerRules.RequestedStateFor(VmPowerOperation.TurnOff));
        Assert.Equal((ushort)6, VmPowerRules.RequestedStateFor(VmPowerOperation.Save));
        Assert.Equal((ushort)9, VmPowerRules.RequestedStateFor(VmPowerOperation.Pause));
        Assert.Equal((ushort)11, VmPowerRules.RequestedStateFor(VmPowerOperation.Reset));
        Assert.Null(VmPowerRules.RequestedStateFor(VmPowerOperation.Shutdown));
    }
}

public class WmiHelperTests
{
    [Fact]
    public void Parses_rasd_instance_owner()
    {
        var id = Guid.NewGuid();
        Assert.True(WmiHyperVHost.TryParseOwner($"Microsoft:{id:D}\\b637f346-6a0e-4dec-af52-bd70cb80a21d\\0", out var parsed));
        Assert.Equal(id, parsed);
        Assert.False(WmiHyperVHost.TryParseOwner("Microsoft:Definition\\x\\Default", out _));
        Assert.False(WmiHyperVHost.TryParseOwner(null, out _));
    }

    [Theory]
    [InlineData("Microsoft:Hyper-V:SubType:1", 1)]
    [InlineData("Microsoft:Hyper-V:SubType:2", 2)]
    [InlineData("something", null)]
    public void Parses_generation(string subType, int? expected) => Assert.Equal(expected, WmiHyperVHost.ParseGeneration(subType));

    [Theory]
    [InlineData(32769u, AgentCommandStatus.AccessDenied)]
    [InlineData(32775u, AgentCommandStatus.InvalidState)]
    [InlineData(32770u, AgentCommandStatus.NotSupported)]
    [InlineData(32768u, AgentCommandStatus.Failed)]
    public void Maps_wmi_return_codes(uint code, AgentCommandStatus expected) => Assert.Equal(expected, WmiHyperVHost.MapReturnCode(code));
}

public class CommandExecutorTests
{
    private sealed class CountingHost : IHyperVHost
    {
        public int PowerCalls;
        public Task<HostInventory> CollectInventoryAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<VmSummary?> GetVmAsync(Guid vmId, CancellationToken ct) => Task.FromResult<VmSummary?>(null);
        public async Task<PowerOutcome> ExecutePowerAsync(Guid vmId, VmPowerOperation operation, string reason, TimeSpan timeout,
            IProgress<(int Percent, string Message)> progress, CancellationToken ct)
        {
            Interlocked.Increment(ref PowerCalls);
            await Task.Delay(50, ct);
            return new PowerOutcome(AgentCommandStatus.Succeeded, "ok", 0, null);
        }
    }

    private static readonly IProgress<AgentCommandProgress> NoProgress = new Progress<AgentCommandProgress>();

    [Fact]
    public async Task Same_command_id_is_executed_once()
    {
        var host = new CountingHost();
        var exec = new CommandExecutor(host, NullLogger<CommandExecutor>.Instance);
        var cmd = new AgentCommand { CommandId = Guid.NewGuid(), Kind = AgentCommandKind.VmPower, VmId = Guid.NewGuid(), PowerOperation = VmPowerOperation.Start };
        var results = await Task.WhenAll(exec.ExecuteAsync(cmd, NoProgress, default), exec.ExecuteAsync(cmd, NoProgress, default));
        var again = await exec.ExecuteAsync(cmd, NoProgress, default);
        Assert.Equal(1, host.PowerCalls);
        Assert.Single(results.Concat([again]), r => !r.Replayed);
        Assert.True(again.Replayed);
    }

    [Theory]
    [InlineData(AgentCommandKind.VmPower, true, null)]
    [InlineData(AgentCommandKind.VmPower, false, VmPowerOperation.Start)]
    [InlineData(AgentCommandKind.GetVm, false, null)]
    public async Task Malformed_commands_are_rejected(AgentCommandKind kind, bool withVm, VmPowerOperation? op)
    {
        var exec = new CommandExecutor(new CountingHost(), NullLogger<CommandExecutor>.Instance);
        var r = await exec.ExecuteAsync(new AgentCommand { CommandId = Guid.NewGuid(), Kind = kind, VmId = withVm ? Guid.NewGuid() : null, PowerOperation = op },
            NoProgress, default);
        Assert.Equal(AgentCommandStatus.Rejected, r.Status);
    }

    [Fact]
    public async Task Undefined_operation_value_is_rejected()
    {
        var host = new CountingHost();
        var exec = new CommandExecutor(host, NullLogger<CommandExecutor>.Instance);
        var r = await exec.ExecuteAsync(new AgentCommand { CommandId = Guid.NewGuid(), Kind = AgentCommandKind.VmPower, VmId = Guid.NewGuid(), PowerOperation = (VmPowerOperation)999 },
            NoProgress, default);
        Assert.Equal(AgentCommandStatus.Rejected, r.Status);
        Assert.Equal(0, host.PowerCalls);
    }

    [Theory]
    [InlineData("job 1234; rm -rf /", "job 1234 rm -rf ")]
    [InlineData("$(Get-Process)", "Get-Process")]
    [InlineData(null, "HvPortal")]
    public void Shutdown_reason_is_sanitized(string? input, string expected) => Assert.Equal(expected, CommandExecutor.SanitizeReason(input));
}

public class GatewayTrustTests
{
    private static X509Certificate2 Ca(string name, out ECDsa key)
    {
        key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    private static X509Certificate2 Leaf(X509Certificate2 ca)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=gateway", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        return req.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30), [1, 2, 3, 4]);
    }

    [Fact]
    public void Accepts_only_certificates_chained_to_the_pinned_ca()
    {
        using var ca = Ca("pinned", out var k1);
        using var other = Ca("other", out var k2);
        using var good = Leaf(ca);
        using var bad = Leaf(other);
        Assert.True(GatewayTrust.Validate(ca, good, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(GatewayTrust.Validate(ca, bad, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(GatewayTrust.Validate(ca, good, SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.False(GatewayTrust.Validate(ca, null, SslPolicyErrors.None));
        k1.Dispose();
        k2.Dispose();
    }

    [Fact]
    public void Fingerprint_comparison_requires_exact_sha256()
    {
        using var ca = Ca("pinned", out var k);
        var fp = GatewayTrust.Sha256Fingerprint(ca);
        Assert.True(GatewayTrust.FingerprintMatches(ca, fp));
        Assert.True(GatewayTrust.FingerprintMatches(ca, string.Join(':', Enumerable.Range(0, 32).Select(i => fp.Substring(i * 2, 2)))));
        Assert.False(GatewayTrust.FingerprintMatches(ca, fp[..62] + "00"));
        Assert.False(GatewayTrust.FingerprintMatches(ca, "abc"));
        k.Dispose();
    }
}
