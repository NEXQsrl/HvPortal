using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using HvPortal.Contracts.Agent;
using HvPortal.Gateway.Agents;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Crypto;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Security;
using HvPortal.Gateway.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HvPortal.Gateway.Tests.Integration;

/// <summary>Management agents and remote hosts reached with stored credentials (ADR-022).</summary>
public sealed class ManagementAgentTests : IAsyncLifetime
{
    private const string Secret = "Wq9!remote-Hyper-V-pass";
    private GatewayFactory _app = null!;

    public async Task InitializeAsync()
    {
        _app = new GatewayFactory();
        _ = _app.Server;
        await _app.SetMfaPolicyAsync(MfaRequirement.None);
    }

    public Task DisposeAsync()
    {
        _app.Dispose();
        return Task.CompletedTask;
    }

    private async Task<HttpClient> AdminAsync(string name)
    {
        await _app.CreateUserAsync(name, BuiltInRoles.InfrastructureAdmin);
        var c = _app.CreateBrowser();
        await c.LoginAsync(name);
        return c;
    }

    private static async Task<Guid> CreateAgentAsync(HttpClient c, string name)
    {
        var r = await c.PostJsonAsync("/api/v1/management-agents", new { displayName = name });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var json = await r.JsonAsync();
        Assert.Contains("-Management", json.GetProperty("enrollment").GetProperty("command").GetString(), StringComparison.Ordinal);
        return json.GetProperty("managementAgentId").GetGuid();
    }

    private static async Task<Guid> CreateRemoteHostAsync(HttpClient c, Guid agentId, string name, string computer, string user = "Administrator")
    {
        var r = await c.PostJsonAsync("/api/v1/hosts/remote",
            new { displayName = name, managementAgentId = agentId, computerName = computer, userName = user, password = Secret, useHttps = false });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.JsonAsync()).GetProperty("hostId").GetGuid();
    }

    [Fact]
    public async Task Management_agents_and_remote_hosts_require_global_hosts_manage()
    {
        var admin = await AdminAsync("infra-ma");
        var agentId = await CreateAgentAsync(admin, "mgmt-01");
        var hostId = await CreateRemoteHostAsync(admin, agentId, "hv-remote-auth", "hv-auth.lab.local");

        // Host-scoped administrator: can see the host, but cannot manage agents or credentials (global permission).
        await _app.CreateUserAsync("scoped-ma", BuiltInRoles.InfrastructureAdmin, ScopeType.Host, hostId.ToString("D"));
        await _app.CreateUserAsync("ro-ma", BuiltInRoles.ReadOnly);
        foreach (var user in new[] { "scoped-ma", "ro-ma" })
        {
            var c = _app.CreateBrowser();
            await c.LoginAsync(user);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/v1/management-agents")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PostJsonAsync("/api/v1/management-agents", new { displayName = "x-" + user })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PostJsonAsync($"/api/v1/management-agents/{agentId}/enrollment-token")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PostJsonAsync($"/api/v1/management-agents/{agentId}/revoke")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PostJsonAsync("/api/v1/hosts/remote",
                new { displayName = "x", managementAgentId = agentId, computerName = "hv9", userName = "a", password = "b" })).StatusCode);
            await c.CsrfAsync();
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PutAsJsonAsync($"/api/v1/hosts/{hostId}/remote",
                new { managementAgentId = agentId, computerName = "evil.example", userName = "a", password = "b" }, GatewayFactory.Json)).StatusCode);
        }
        Assert.True(await _app.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "hosts.update_remote" && a.Outcome == AuditOutcome.Denied)));
        Assert.Equal("hv-auth.lab.local", await _app.WithDbAsync(db => db.Hosts.Where(h => h.Id == hostId).Select(h => h.RemoteComputerName).FirstAsync()));
    }

    [Fact]
    public async Task Remote_host_password_is_encrypted_bound_to_the_host_and_never_returned_or_audited()
    {
        var admin = await AdminAsync("infra-pw");
        var agentId = await CreateAgentAsync(admin, "mgmt-pw");
        var hostId = await CreateRemoteHostAsync(admin, agentId, "hv-remote-pw", "hv-pw.lab.local", @"HV-PW\hvadmin");
        Assert.Contains(agentId, _app.Agent.Reconfigured);

        var host = await _app.WithDbAsync(db => db.Hosts.AsNoTracking().FirstAsync(h => h.Id == hostId));
        Assert.Equal(HostConnectionMode.Remote, host.ConnectionMode);
        Assert.Equal(HostStatus.Active, host.Status);
        Assert.NotNull(host.RemotePasswordEncrypted);
        Assert.DoesNotContain(Secret, Encoding.UTF8.GetString(host.RemotePasswordEncrypted!), StringComparison.Ordinal);
        var keys = _app.Services.GetRequiredService<KeyRing>();
        Assert.Equal(Secret, RemoteHostCredentials.Unprotect(keys, hostId, host.RemotePasswordEncrypted!));
        // Bound to the host id: the same ciphertext does not decrypt for another host.
        Assert.ThrowsAny<CryptographicException>(() => RemoteHostCredentials.Unprotect(keys, Guid.NewGuid(), host.RemotePasswordEncrypted!));

        var single = await (await admin.GetAsync($"/api/v1/hosts/{hostId}")).Content.ReadAsStringAsync();
        var list = await (await admin.GetAsync("/api/v1/hosts")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(Secret, single, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, list, StringComparison.Ordinal);
        Assert.Contains("hvadmin", single, StringComparison.Ordinal);
        Assert.Contains("\"connectionMode\":\"Remote\"", single, StringComparison.Ordinal);

        // A user who only sees the host does not get the connection details.
        await _app.CreateUserAsync("viewer-pw", BuiltInRoles.ReadOnly, ScopeType.Host, hostId.ToString("D"));
        var viewer = _app.CreateBrowser();
        await viewer.LoginAsync("viewer-pw");
        var seen = await (await viewer.GetAsync($"/api/v1/hosts/{hostId}")).JsonAsync();
        Assert.Equal(System.Text.Json.JsonValueKind.Null, seen.GetProperty("host").GetProperty("remote").ValueKind);

        // Update without password keeps it; with a new one replaces it. Neither value reaches the audit log.
        await admin.CsrfAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/v1/hosts/{hostId}/remote",
            new { managementAgentId = agentId, computerName = "hv-pw.lab.local", userName = @"HV-PW\other", useHttps = true }, GatewayFactory.Json)).StatusCode);
        host = await _app.WithDbAsync(db => db.Hosts.AsNoTracking().FirstAsync(h => h.Id == hostId));
        Assert.Equal(Secret, RemoteHostCredentials.Unprotect(keys, hostId, host.RemotePasswordEncrypted!));
        Assert.True(host.RemoteUseHttps);
        const string newSecret = "An0ther-remote-Secret";
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/v1/hosts/{hostId}/remote",
            new { managementAgentId = agentId, computerName = "hv-pw.lab.local", userName = @"HV-PW\other", password = newSecret }, GatewayFactory.Json)).StatusCode);
        host = await _app.WithDbAsync(db => db.Hosts.AsNoTracking().FirstAsync(h => h.Id == hostId));
        Assert.Equal(newSecret, RemoteHostCredentials.Unprotect(keys, hostId, host.RemotePasswordEncrypted!));

        var audit = await _app.WithDbAsync(db => db.AuditEntries.AsNoTracking().ToListAsync());
        Assert.Contains(audit, a => a.Action == "hosts.create_remote");
        Assert.Contains(audit, a => a.Action == "hosts.update_remote");
        Assert.DoesNotContain(audit, a => (a.DetailsJson ?? "").Contains(Secret, StringComparison.Ordinal) || (a.DetailsJson ?? "").Contains(newSecret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Remote_host_input_is_validated()
    {
        var admin = await AdminAsync("infra-val");
        var agentId = await CreateAgentAsync(admin, "mgmt-val");
        await CreateRemoteHostAsync(admin, agentId, "hv-val-1", "hv-val.lab.local");

        async Task<string?> Create(object body) => await (await admin.PostJsonAsync("/api/v1/hosts/remote", body)).CodeAsync();
        Assert.Equal("user_name_needs_host", await Create(new { displayName = "v2", managementAgentId = agentId, computerName = "10.0.0.25", userName = "Administrator", password = "x" }));
        Assert.Equal("invalid_computer_name", await Create(new { displayName = "v3", managementAgentId = agentId, computerName = "localhost", userName = "a", password = "x" }));
        Assert.Equal("invalid_computer_name", await Create(new { displayName = "v4", managementAgentId = agentId, computerName = @"\\hv\c$", userName = "a", password = "x" }));
        Assert.Equal("invalid_computer_name", await Create(new { displayName = "v5", managementAgentId = agentId, computerName = "fe80::1", userName = @"H\a", password = "x" }));
        Assert.Equal("invalid_user_name", await Create(new { displayName = "v6", managementAgentId = agentId, computerName = "hv6", userName = "a\"b", password = "x" }));
        Assert.Equal("invalid_password", await Create(new { displayName = "v7", managementAgentId = agentId, computerName = "hv7", userName = "a", password = "" }));
        Assert.Equal("invalid_management_agent", await Create(new { displayName = "v8", managementAgentId = Guid.NewGuid(), computerName = "hv8", userName = "a", password = "x" }));
        Assert.Equal("host_exists", await Create(new { displayName = "v9", managementAgentId = agentId, computerName = "hv-val.lab.local", userName = "a", password = "x" }));
        Assert.Equal(HttpStatusCode.OK, (await admin.PostJsonAsync("/api/v1/hosts/remote",
            new { displayName = "v10", managementAgentId = agentId, computerName = "10.0.0.26", userName = @"HV10\Administrator", password = "x" })).StatusCode);
    }

    [Fact]
    public async Task Remote_hosts_have_no_enrollment_and_follow_their_management_agent_lifecycle()
    {
        var admin = await AdminAsync("infra-life");
        var a1 = await CreateAgentAsync(admin, "mgmt-a1");
        var a2 = await CreateAgentAsync(admin, "mgmt-a2");
        var hostId = await CreateRemoteHostAsync(admin, a1, "hv-life", "hv-life.lab.local");
        Assert.Equal("remote_host_not_enrolled", await (await admin.PostJsonAsync($"/api/v1/hosts/{hostId}/enrollment-token")).CodeAsync());

        // Moving the host reconfigures both agents.
        while (_app.Agent.Reconfigured.TryDequeue(out _))
        {
        }
        await admin.CsrfAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/v1/hosts/{hostId}/remote",
            new { managementAgentId = a2, computerName = "hv-life.lab.local", userName = "Administrator" }, GatewayFactory.Json)).StatusCode);
        Assert.Contains(a1, _app.Agent.Reconfigured);
        Assert.Contains(a2, _app.Agent.Reconfigured);

        // A revoked remote host is re-enabled by saving its connection again (it has no agent to re-enroll).
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostJsonAsync($"/api/v1/hosts/{hostId}/revoke")).StatusCode);
        Assert.Equal(HostStatus.Revoked, await _app.WithDbAsync(db => db.Hosts.Where(h => h.Id == hostId).Select(h => h.Status).FirstAsync()));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/v1/hosts/{hostId}/remote",
            new { managementAgentId = a2, computerName = "hv-life.lab.local", userName = "Administrator" }, GatewayFactory.Json)).StatusCode);
        Assert.Equal(HostStatus.Active, await _app.WithDbAsync(db => db.Hosts.Where(h => h.Id == hostId).Select(h => h.Status).FirstAsync()));

        // An agent in use cannot be deleted; a revoked one cannot receive new hosts.
        Assert.Equal("management_agent_in_use", await (await admin.PostJsonAsync($"/api/v1/management-agents/{a2}/delete", new { confirmName = "mgmt-a2" })).CodeAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostJsonAsync($"/api/v1/management-agents/{a1}/revoke")).StatusCode);
        Assert.Contains(a1, _app.Agent.ManagementDisconnected);
        Assert.Equal("invalid_management_agent", await (await admin.PostJsonAsync("/api/v1/hosts/remote",
            new { displayName = "hv-life2", managementAgentId = a1, computerName = "hv-life2", userName = "a", password = "x" })).CodeAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostJsonAsync($"/api/v1/management-agents/{a1}/delete", new { confirmName = "mgmt-a1" })).StatusCode);

        // Deleting the remote host removes the stored credentials and tells its agent.
        while (_app.Agent.Reconfigured.TryDequeue(out _))
        {
        }
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostJsonAsync($"/api/v1/hosts/{hostId}/delete", new { confirmName = "hv-life" })).StatusCode);
        Assert.Contains(a2, _app.Agent.Reconfigured);
        Assert.False(await _app.WithDbAsync(db => db.Hosts.AnyAsync(h => h.Id == hostId)));

        var listed = await (await admin.GetAsync("/api/v1/management-agents")).JsonAsync();
        Assert.Single(listed.EnumerateArray());
        Assert.Equal(0, listed[0].GetProperty("hostCount").GetInt32());
    }

    [Fact]
    public async Task Management_agent_enrolls_with_its_token_and_gets_the_management_role()
    {
        var admin = await AdminAsync("infra-enr");
        var r = await (await admin.PostJsonAsync("/api/v1/management-agents", new { displayName = "mgmt-enr" })).JsonAsync();
        var agentId = r.GetProperty("managementAgentId").GetGuid();
        var token = r.GetProperty("enrollment").GetProperty("token").GetString()!;
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var csr = new CertificateRequest("CN=mgmt-server", key, HashAlgorithmName.SHA256).CreateSigningRequestPem();

        using var scope = _app.Services.CreateScope();
        var enrollment = scope.ServiceProvider.GetRequiredService<EnrollmentService>();
        var response = await enrollment.EnrollAsync(new EnrollmentRequest { Token = token, CsrPem = csr, MachineName = "MGMT01", AgentVersion = "0.4.0" }, "10.0.0.9", CancellationToken.None);
        Assert.Equal(AgentRole.Management, response.Role);
        Assert.Equal(agentId, response.HostId);
        var agent = await _app.WithDbAsync(db => db.ManagementAgents.AsNoTracking().FirstAsync(a => a.Id == agentId));
        Assert.Equal(HostStatus.Active, agent.Status);
        Assert.Equal("MGMT01", agent.MachineName);
        // A management agent is not a host: no host row appears in the inventory.
        Assert.False(await _app.WithDbAsync(db => db.Hosts.AnyAsync(h => h.Id == agentId)));
        Assert.True(await _app.WithDbAsync(db => db.AgentCertificates.AnyAsync(c => c.HostId == agentId && c.RevokedAt == null)));
    }
}
