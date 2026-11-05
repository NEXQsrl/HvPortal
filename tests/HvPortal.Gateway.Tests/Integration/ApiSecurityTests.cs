using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Crypto;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Security;
using HvPortal.Gateway.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HvPortal.Gateway.Tests.Integration;

/// <summary>Negative authorization, CSRF, session revocation, MFA replay, injection and escalation tests on the real pipeline.</summary>
public sealed class ApiSecurityTests : IAsyncLifetime
{
    private GatewayFactory _app = null!;

    public async Task InitializeAsync()
    {
        _app = new GatewayFactory();
        _ = _app.Server; // start host: migrations + bootstrap
        await _app.SetMfaPolicyAsync(MfaRequirement.None);
    }

    public Task DisposeAsync()
    {
        _app.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Unauthenticated_requests_are_rejected()
    {
        var c = _app.CreateBrowser();
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v1/vms")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v1/hosts")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/openapi/v1.json")).StatusCode);
    }

    [Fact]
    public async Task State_changing_request_without_csrf_token_is_rejected()
    {
        await _app.CreateUserAsync("op1", BuiltInRoles.ReadOnly);
        var c = _app.CreateBrowser();
        await c.LoginAsync("op1");
        c.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        var r = await c.PostAsJsonAsync("/api/v1/auth/logout", new { });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("csrf_invalid", await r.CodeAsync());
    }

    [Fact]
    public async Task Operator_sees_and_controls_only_assigned_vm()
    {
        var (_, vms) = await _app.CreateHostAsync("h1", ("allowed", VmPowerState.Off), ("forbidden", VmPowerState.Off));
        await _app.CreateUserAsync("op2", BuiltInRoles.VmOperator, ScopeType.Vm, vms["allowed"].ToString("D"));
        var c = _app.CreateBrowser();
        await c.LoginAsync("op2");

        var list = await (await c.GetAsync("/api/v1/vms")).JsonAsync();
        Assert.Single(list.EnumerateArray());
        Assert.Equal("allowed", list[0].GetProperty("name").GetString());

        // The forbidden VM is indistinguishable from a nonexistent one.
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/v1/vms/{vms["forbidden"]}")).StatusCode);
        var power = await c.PostJsonAsync($"/api/v1/vms/{vms["forbidden"]}/power", new { operation = "Start" });
        Assert.Equal(HttpStatusCode.NotFound, power.StatusCode);

        var ok = await c.PostJsonAsync($"/api/v1/vms/{vms["allowed"]}/power", new { operation = "Start" });
        Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
        await _app.RunJobsAsync();
        var jobId = (await ok.JsonAsync()).GetProperty("jobId").GetGuid();
        var job = await (await c.GetAsync($"/api/v1/jobs/{jobId}")).JsonAsync();
        Assert.Equal("Succeeded", job.GetProperty("job").GetProperty("status").GetString());

        // Hosts listing reveals only the host that contains a visible VM, never others.
        var hosts = await (await c.GetAsync("/api/v1/hosts")).JsonAsync();
        Assert.Single(hosts.EnumerateArray());
    }

    [Fact]
    public async Task Force_operations_need_their_own_permission_and_are_audited_when_denied()
    {
        var (hostId, vms) = await _app.CreateHostAsync("h2", ("vm", VmPowerState.Running));
        await _app.CreateUserAsync("op3", BuiltInRoles.VmOperator, ScopeType.Host, hostId.ToString("D"));
        var c = _app.CreateBrowser();
        await c.LoginAsync("op3");
        var r = await c.PostJsonAsync($"/api/v1/vms/{vms["vm"]}/power", new { operation = "TurnOff", confirmName = "vm" });
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        var denied = await _app.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "vm.power.turnoff.request" && a.Outcome == AuditOutcome.Denied));
        Assert.True(denied);
    }

    [Fact]
    public async Task Destructive_operations_require_typed_confirmation()
    {
        var (_, vms) = await _app.CreateHostAsync("h3", ("db01", VmPowerState.Running));
        await _app.CreateUserAsync("infra1", BuiltInRoles.InfrastructureAdmin);
        var c = _app.CreateBrowser();
        await c.LoginAsync("infra1");
        var missing = await c.PostJsonAsync($"/api/v1/vms/{vms["db01"]}/power", new { operation = "TurnOff" });
        Assert.Equal("confirmation_required", await missing.CodeAsync());
        var wrong = await c.PostJsonAsync($"/api/v1/vms/{vms["db01"]}/power", new { operation = "TurnOff", confirmName = "DB01" });
        Assert.Equal("confirmation_required", await wrong.CodeAsync());
        var ok = await c.PostJsonAsync($"/api/v1/vms/{vms["db01"]}/power", new { operation = "TurnOff", confirmName = "db01" });
        Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
    }

    [Fact]
    public async Task Revoked_permission_is_enforced_when_the_queued_job_executes()
    {
        var (hostId, vms) = await _app.CreateHostAsync("h4", ("vm", VmPowerState.Off));
        var uid = await _app.CreateUserAsync("op4", BuiltInRoles.VmOperator, ScopeType.Host, hostId.ToString("D"));
        var c = _app.CreateBrowser();
        await c.LoginAsync("op4");
        var r = await c.PostJsonAsync($"/api/v1/vms/{vms["vm"]}/power", new { operation = "Start" });
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        var jobId = (await r.JsonAsync()).GetProperty("jobId").GetGuid();

        await _app.WithDbAsync(db => db.RoleAssignments.Where(a => a.PrincipalId == uid.ToString("D")).ExecuteDeleteAsync());
        await _app.RunJobsAsync();

        var job = await _app.WithDbAsync(db => db.Jobs.AsNoTracking().FirstAsync(j => j.Id == jobId));
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal("authorization_revoked", job.ErrorCode);
        var hyperVId = await _app.WithDbAsync(db => db.VirtualMachines.Where(v => v.Id == vms["vm"]).Select(v => v.HyperVId).FirstAsync());
        Assert.Equal(0, _app.Agent.PowerCommandsFor(hyperVId)); // never reached the host
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/v1/vms/{vms["vm"]}")).StatusCode);
    }

    [Fact]
    public async Task Password_reset_by_admin_revokes_existing_sessions()
    {
        var target = await _app.CreateUserAsync("victim", BuiltInRoles.ReadOnly);
        await _app.CreateUserAsync("admin1", BuiltInRoles.PlatformAdmin);
        var victim = _app.CreateBrowser();
        await victim.LoginAsync("victim");
        Assert.Equal(HttpStatusCode.OK, (await victim.GetAsync("/api/v1/auth/me")).StatusCode);

        var admin = _app.CreateBrowser();
        await admin.LoginAsync("admin1");
        var r = await admin.PostJsonAsync($"/api/v1/users/{target}/reset-password");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await victim.GetAsync("/api/v1/auth/me")).StatusCode);

        // The temporary password only grants a restricted session that must change the password first.
        var temp = (await r.JsonAsync()).GetProperty("temporaryPassword").GetString()!;
        var again = _app.CreateBrowser();
        await again.LoginAsync("victim", temp);
        var vms = await again.GetAsync("/api/v1/vms");
        Assert.Equal("password_change_required", await vms.CodeAsync());
    }

    [Fact]
    public async Task Critical_actions_require_recent_reauthentication()
    {
        await _app.CreateUserAsync("admin2", BuiltInRoles.PlatformAdmin);
        var c = _app.CreateBrowser();
        await c.LoginAsync("admin2");
        var sid = await _app.WithDbAsync(db => db.Sessions.Where(s => s.UserId == db.Users.First(u => u.UserName == "admin2").Id).Select(s => s.Id).FirstAsync());
        await _app.WithDbAsync(db => db.Sessions.Where(s => s.Id == sid).ExecuteUpdateAsync(u => u.SetProperty(s => s.ReauthenticatedAt, DateTimeOffset.UtcNow.AddMinutes(-30))));
        var r = await c.PostJsonAsync("/api/v1/users", new { userName = "newuser" });
        Assert.Equal("reauthentication_required", await r.CodeAsync());
        await c.ReauthAsync();
        Assert.Equal(HttpStatusCode.OK, (await c.PostJsonAsync("/api/v1/users", new { userName = "newuser" })).StatusCode);
    }

    [Fact]
    public async Task Role_management_cannot_be_used_for_privilege_escalation()
    {
        var helperRole = await _app.WithDbAsync(async db =>
        {
            var role = new Role { Name = "Role manager only" };
            role.Permissions.Add(new RolePermission { RoleId = role.Id, Permission = Permissions.RolesManage });
            role.Permissions.Add(new RolePermission { RoleId = role.Id, Permission = Permissions.InventoryRead });
            db.Roles.Add(role);
            await db.SaveChangesAsync();
            return role.Id;
        });
        var managerId = await _app.CreateUserAsync("rolemgr");
        await _app.WithDbAsync(async db =>
        {
            db.RoleAssignments.Add(new RoleAssignment { PrincipalId = managerId.ToString("D"), RoleId = helperRole, ScopeType = ScopeType.Global, ScopeId = "" });
            await db.SaveChangesAsync();
        });
        var friend = await _app.CreateUserAsync("friend");
        var platformAdmin = await _app.WithDbAsync(db => db.Roles.Where(r => r.BuiltInKey == BuiltInRoles.PlatformAdmin).Select(r => r.Id).FirstAsync());

        var c = _app.CreateBrowser();
        await c.LoginAsync("rolemgr");
        var toFriend = await c.PostJsonAsync("/api/v1/role-assignments", new { userId = friend, roleId = platformAdmin, scopeType = "Global", effect = "Allow" });
        Assert.Equal("permission_amplification", await toFriend.CodeAsync());
        var toSelf = await c.PostJsonAsync("/api/v1/role-assignments", new { userId = managerId, roleId = platformAdmin, scopeType = "Global", effect = "Allow" });
        Assert.Equal("self_assignment_forbidden", await toSelf.CodeAsync());
        var customRole = await c.PostJsonAsync("/api/v1/roles", new { name = "sneaky", permissions = new[] { Permissions.UsersManage } });
        Assert.Equal("permission_amplification", await customRole.CodeAsync());
        // Allowed: grant what the manager holds.
        var readOnly = await _app.WithDbAsync(db => db.Roles.Where(r => r.BuiltInKey == BuiltInRoles.ReadOnly).Select(r => r.Id).FirstAsync());
        var fine = await c.PostJsonAsync("/api/v1/role-assignments", new { userId = friend, roleId = readOnly, scopeType = "Global", effect = "Allow" });
        Assert.Equal(HttpStatusCode.OK, fine.StatusCode);
    }

    [Fact]
    public async Task Last_platform_admin_cannot_be_removed()
    {
        var a = await _app.CreateUserAsync("solo-admin", BuiltInRoles.PlatformAdmin);
        var b = await _app.CreateUserAsync("helper-admin", BuiltInRoles.PlatformAdmin);
        var c = _app.CreateBrowser();
        await c.LoginAsync("helper-admin");
        var assignmentOfA = await _app.WithDbAsync(db => db.RoleAssignments.Where(x => x.PrincipalId == a.ToString("D")).Select(x => x.Id).FirstAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteWithCsrfAsync($"/api/v1/role-assignments/{assignmentOfA}")).StatusCode);
        // Now helper-admin is the last one: disabling them is impossible from another account, self-management is refused.
        var self = await _app.WithDbAsync(db => db.RoleAssignments.Where(x => x.PrincipalId == b.ToString("D")).Select(x => x.Id).FirstAsync());
        Assert.Equal("self_assignment_forbidden", await (await c.DeleteWithCsrfAsync($"/api/v1/role-assignments/{self}")).CodeAsync());
    }

    [Theory]
    [InlineData("admin' OR '1'='1")]
    [InlineData("../../etc/passwd")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("a")]
    public async Task Invalid_user_names_are_rejected(string name)
    {
        await _app.CreateUserAsync("admin3", BuiltInRoles.PlatformAdmin);
        var c = _app.CreateBrowser();
        await c.LoginAsync("admin3");
        var r = await c.PostJsonAsync("/api/v1/users", new { userName = name });
        Assert.Equal("invalid_user_name", await r.CodeAsync());
    }

    [Fact]
    public async Task Hostile_input_in_queries_and_routes_is_handled_safely()
    {
        await _app.CreateHostAsync("h5", ("web", VmPowerState.Off));
        await _app.CreateUserAsync("reader", BuiltInRoles.ReadOnly);
        var c = _app.CreateBrowser();
        await c.LoginAsync("reader");
        var r = await c.GetAsync("/api/v1/vms?search=" + Uri.EscapeDataString("x' OR 1=1; DROP TABLE VirtualMachines;--"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Empty((await r.JsonAsync()).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/v1/vms/not-a-guid")).StatusCode);
        Assert.True(await _app.WithDbAsync(db => db.VirtualMachines.AnyAsync()));
        var bad = await c.PostJsonAsync($"/api/v1/vms/{Guid.NewGuid()}/power", new { operation = "RunScript" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Idempotency_key_returns_the_same_job_and_rejects_reuse_for_other_operations()
    {
        var (_, vms) = await _app.CreateHostAsync("h6", ("vm", VmPowerState.Off));
        await _app.CreateUserAsync("op6", BuiltInRoles.VmOperator);
        var c = _app.CreateBrowser();
        await c.LoginAsync("op6");
        var key = "key-" + Guid.NewGuid().ToString("N");
        var a = await (await c.PostJsonAsync($"/api/v1/vms/{vms["vm"]}/power", new { operation = "Start", idempotencyKey = key })).JsonAsync();
        var b = await (await c.PostJsonAsync($"/api/v1/vms/{vms["vm"]}/power", new { operation = "Start", idempotencyKey = key })).JsonAsync();
        Assert.Equal(a.GetProperty("jobId").GetGuid(), b.GetProperty("jobId").GetGuid());
        Assert.False(b.GetProperty("created").GetBoolean());
        var conflict = await c.PostJsonAsync($"/api/v1/vms/{vms["vm"]}/power", new { operation = "Save", idempotencyKey = key });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task Totp_login_rejects_code_replay()
    {
        var uid = await _app.CreateUserAsync("mfauser", BuiltInRoles.ReadOnly);
        var secret = Totp.NewSecret();
        var keys = _app.Services.GetRequiredService<KeyRing>();
        await _app.WithDbAsync(db => db.Users.Where(u => u.Id == uid).ExecuteUpdateAsync(u => u
            .SetProperty(x => x.TotpEnabled, true).SetProperty(x => x.TotpSecretEncrypted, keys.Encrypt(secret, "totp"))));
        var code = Totp.Compute(secret, Totp.TimeStep(DateTimeOffset.UtcNow));

        var c1 = _app.CreateBrowser();
        var step1 = await (await c1.PostJsonAsync("/api/v1/auth/login", new { userName = "mfauser", password = GatewayFactory.DefaultPassword })).JsonAsync();
        Assert.Equal("mfa_required", step1.GetProperty("status").GetString());
        var ok = await c1.PostJsonAsync("/api/v1/auth/login/mfa", new { challenge = step1.GetProperty("challenge").GetString(), code });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var c2 = _app.CreateBrowser();
        var step2 = await (await c2.PostJsonAsync("/api/v1/auth/login", new { userName = "mfauser", password = GatewayFactory.DefaultPassword })).JsonAsync();
        var replay = await c2.PostJsonAsync("/api/v1/auth/login/mfa", new { challenge = step2.GetProperty("challenge").GetString(), code });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal("invalid_mfa_code", await replay.CodeAsync());

        // Brute force on one challenge is bounded.
        for (var i = 0; i < 5; i++)
        {
            await c2.PostJsonAsync("/api/v1/auth/login/mfa", new { challenge = step2.GetProperty("challenge").GetString(), code = "000000" });
        }
        var exhausted = await c2.PostJsonAsync("/api/v1/auth/login/mfa", new { challenge = step2.GetProperty("challenge").GetString(), code = "000000" });
        Assert.True(await exhausted.CodeAsync() is "mfa_challenge_invalid" or "throttled");
    }

    [Fact]
    public async Task Repeated_failures_trigger_progressive_delay_without_locking_other_sources()
    {
        await _app.CreateUserAsync("target", BuiltInRoles.ReadOnly);
        var attacker = _app.CreateBrowser();
        HttpResponseMessage last = null!;
        for (var i = 0; i < 4; i++)
        {
            last = await attacker.PostJsonAsync("/api/v1/auth/login", new { userName = "target", password = "wrong password attempt " + i });
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, last.StatusCode);
        // Same account from another source address is not locked out (TestServer uses one address, so we check the key design directly).
        var rows = await _app.WithDbAsync(db => db.LoginThrottles.AsNoTracking().ToListAsync());
        Assert.Contains(rows, r => r.Key.StartsWith("ai:TARGET|", StringComparison.Ordinal) && r.BlockedUntil != null);
        // The account-wide counter only feeds alerting: it never blocks, so the account cannot be locked out remotely.
        var accountWide = Assert.Single(rows, r => r.Key == "a:TARGET");
        Assert.Null(accountWide.BlockedUntil);
    }
}
