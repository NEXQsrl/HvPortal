using System.Net;
using System.Net.Http.Json;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Security;
using HvPortal.Gateway.Tests.Infrastructure;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace HvPortal.Gateway.Tests.Integration;

/// <summary>User creation (type, role, MFA at first sign-in) and Microsoft Entra ID sign-in / re-authentication.</summary>
public sealed class UserOnboardingTests : IAsyncLifetime
{
    private const string Tenant = "11111111-2222-3333-4444-555555555555";
    private const string ClientApp = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
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
        await _app.CreateUserAsync(name, BuiltInRoles.PlatformAdmin);
        var c = _app.CreateBrowser();
        await c.LoginAsync(name);
        await c.ReauthAsync();
        return c;
    }

    private Task<Guid> RoleIdAsync(string key) => _app.WithDbAsync(db => db.Roles.Where(r => r.BuiltInKey == key).Select(r => r.Id).FirstAsync());

    [Fact]
    public async Task New_local_user_gets_role_and_must_set_up_mfa_after_the_password_change()
    {
        var admin = await AdminAsync("admin-onb");
        var r = await admin.PostJsonAsync("/api/v1/users", new { userName = "mario.rossi", displayName = "Mario Rossi", roleId = await RoleIdAsync(BuiltInRoles.VmOperator), scopeType = "Global" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.JsonAsync();
        var temp = body.GetProperty("temporaryPassword").GetString()!;
        var id = body.GetProperty("id").GetGuid();
        Assert.True(await _app.WithDbAsync(db => db.RoleAssignments.AnyAsync(a => a.PrincipalId == id.ToString("D"))));

        // First sign-in: password change, then (even though the global policy does not require it) MFA enrollment.
        var user = _app.CreateBrowser();
        var login = await user.PostJsonAsync("/api/v1/auth/login", new { userName = "mario.rossi", password = temp });
        Assert.Equal("password_change", (await login.JsonAsync()).GetProperty("restriction").GetString());
        Assert.True((await user.PostJsonAsync("/api/v1/auth/password", new { currentPassword = temp, newPassword = "copper meadow quiet river 77" })).IsSuccessStatusCode);
        var me = await (await user.GetAsync("/api/v1/auth/me")).JsonAsync();
        Assert.Equal("mfa_enrollment", me.GetProperty("restriction").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/v1/hosts")).StatusCode);

        // Opt-out is possible at creation.
        var r2 = await admin.PostJsonAsync("/api/v1/users", new { userName = "service.viewer", requireMfa = false });
        Assert.False(await _app.WithDbAsync(db => db.Users.Where(u => u.UserName == "service.viewer").Select(u => u.MfaEnrollmentRequired).FirstAsync()));
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
    }

    [Fact]
    public async Task Creating_a_user_with_a_role_follows_the_anti_escalation_rules()
    {
        await _app.CreateUserAsync("admin-x", BuiltInRoles.PlatformAdmin);
        var roleId = await _app.WithDbAsync(async db =>
        {
            var role = new Role { Name = "User admin" };
            foreach (var p in new[] { Permissions.UsersManage, Permissions.RolesManage, Permissions.InventoryRead })
            {
                role.Permissions.Add(new RolePermission { RoleId = role.Id, Permission = p });
            }
            db.Roles.Add(role);
            await db.SaveChangesAsync();
            return role.Id;
        });
        var helpdesk = await _app.CreateUserAsync("helpdesk");
        await _app.WithDbAsync(async db =>
        {
            db.RoleAssignments.Add(new RoleAssignment { PrincipalId = helpdesk.ToString("D"), RoleId = roleId, ScopeType = ScopeType.Global, ScopeId = "", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        });
        var c = _app.CreateBrowser();
        await c.LoginAsync("helpdesk");
        await c.ReauthAsync();
        var r = await c.PostJsonAsync("/api/v1/users", new { userName = "new.admin", roleId = await RoleIdAsync(BuiltInRoles.PlatformAdmin) });
        Assert.Equal("permission_amplification", await r.CodeAsync());
        Assert.False(await _app.WithDbAsync(db => db.Users.AnyAsync(u => u.UserName == "new.admin")));
        Assert.Equal(HttpStatusCode.OK, (await c.PostJsonAsync("/api/v1/users", new { userName = "new.reader", roleId = await RoleIdAsync(BuiltInRoles.ReadOnly) })).StatusCode);
    }

    [Fact]
    public async Task Entra_users_confirm_sensitive_actions_by_typing_a_one_time_code_again()
    {
        var admin = await AdminAsync("admin-code");
        await ConfigureEntraAsync(admin);
        await admin.PostJsonAsync("/api/v1/users", new { userName = "luca@contoso.com", authSource = "entra", roleId = await RoleIdAsync(BuiltInRoles.ReadOnly) });
        var browser = _app.CreateBrowser();
        Assert.Equal("/vms", (await SignInWithEntraAsync(browser, Guid.NewGuid().ToString(), "luca@contoso.com")).Headers.Location!.ToString());
        await _app.WithDbAsync(db => db.Sessions.ExecuteUpdateAsync(u => u.SetProperty(s => s.ReauthenticatedAt, (DateTimeOffset?)null)));
        Task<DateTimeOffset?> ReauthAt() => _app.WithDbAsync(db => db.Sessions.Where(s => s.UserId == db.Users.First(u => u.UserName == "luca@contoso.com").Id).Select(s => s.ReauthenticatedAt).FirstAsync());

        // Without a session: refused. Local accounts confirm with their password, not with a code.
        Assert.Equal(HttpStatusCode.Unauthorized, (await _app.CreateBrowser().PostJsonAsync("/api/v1/auth/reauth/code", new { })).StatusCode);
        Assert.Equal("password_required", await (await admin.PostJsonAsync("/api/v1/auth/reauth/code", new { })).CodeAsync());

        // No code asked yet, or a password instead of the code: no re-authentication.
        Assert.Equal("confirmation_code_expired", await (await browser.PostJsonAsync("/api/v1/auth/reauth", new { confirmationCode = "ABCDEF" })).CodeAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostJsonAsync("/api/v1/auth/reauth", new { password = "anything at all here 12" })).StatusCode);
        Assert.Null(await ReauthAt());

        var issued = await (await browser.PostJsonAsync("/api/v1/auth/reauth/code", new { })).JsonAsync();
        var code = issued.GetProperty("code").GetString()!;
        Assert.Matches("^[A-HJ-NP-Z2-9]{6}$", code);
        var wrong = code[0] == 'A' ? "B" + code[1..] : "A" + code[1..];
        Assert.Equal("invalid_confirmation_code", await (await browser.PostJsonAsync("/api/v1/auth/reauth", new { confirmationCode = wrong })).CodeAsync());
        Assert.Null(await ReauthAt());

        // Typed again (case and spaces do not matter): the session is re-authenticated; the code cannot be used twice.
        var typed = code[..3].ToLowerInvariant() + " " + code[3..];
        Assert.Equal(HttpStatusCode.OK, (await browser.PostJsonAsync("/api/v1/auth/reauth", new { confirmationCode = typed })).StatusCode);
        Assert.NotNull(await ReauthAt());
        Assert.Equal("confirmation_code_expired", await (await browser.PostJsonAsync("/api/v1/auth/reauth", new { confirmationCode = code })).CodeAsync());
        Assert.True(await _app.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "auth.reauthenticate" && a.Outcome == AuditOutcome.Success && a.DetailsJson!.Contains("confirmation-code"))));

        // A code is bound to its session: another session of the same user cannot use it.
        var other = _app.CreateBrowser();
        await SignInWithEntraAsync(other, await _app.WithDbAsync(db => db.Users.Where(u => u.UserName == "luca@contoso.com").Select(u => u.ExternalObjectId!).FirstAsync()), "luca@contoso.com");
        var code2 = (await (await browser.PostJsonAsync("/api/v1/auth/reauth/code", new { })).JsonAsync()).GetProperty("code").GetString()!;
        Assert.Equal("confirmation_code_expired", await (await other.PostJsonAsync("/api/v1/auth/reauth", new { confirmationCode = code2 })).CodeAsync());

        // Five wrong attempts discard the code.
        var wrong2 = code2[0] == 'A' ? "B" + code2[1..] : "A" + code2[1..];
        for (var i = 0; i < ReauthCodeService.MaxAttempts - 1; i++)
        {
            Assert.Equal("invalid_confirmation_code", await (await browser.PostJsonAsync("/api/v1/auth/reauth", new { confirmationCode = wrong2 })).CodeAsync());
        }
        Assert.Equal("confirmation_code_locked", await (await browser.PostJsonAsync("/api/v1/auth/reauth", new { confirmationCode = wrong2 })).CodeAsync());
        Assert.Equal("confirmation_code_expired", await (await browser.PostJsonAsync("/api/v1/auth/reauth", new { confirmationCode = code2 })).CodeAsync());
    }

    [Fact]
    public async Task A_sign_out_after_the_inactivity_warning_is_audited_as_such()
    {
        var c = await AdminAsync("admin-idle");
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostJsonAsync("/api/v1/auth/logout?reason=idle", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.True(await _app.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "auth.logout" && a.DetailsJson!.Contains("idle"))));
        Assert.True(await _app.WithDbAsync(db => db.Sessions.AnyAsync(s => s.RevokedReason == "idle timeout (browser)")));
    }

    private async Task ConfigureEntraAsync(HttpClient admin)
    {
        await admin.CsrfAsync();
        var r = await admin.PutAsJsonAsync("/api/v1/settings/entra", new { enabled = true, tenantId = Tenant, clientId = ClientApp, clientSecret = "s3cr3t-value" }, GatewayFactory.Json);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var json = await r.JsonAsync();
        Assert.True(json.GetProperty("hasClientSecret").GetBoolean());
        Assert.False(json.TryGetProperty("clientSecret", out _));
    }

    /// <summary>Start -> (Entra) -> callback, as a browser would do. Returns the callback response.</summary>
    private async Task<HttpResponseMessage> SignInWithEntraAsync(HttpClient browser, string oid, string upn, string? tenant = null, bool badNonce = false, string mode = "login")
    {
        var start = await browser.GetAsync($"/api/v1/auth/entra/start?returnUrl=%2Fvms{(mode == "reauth" ? "&mode=reauth" : "")}");
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var location = start.Headers.Location!;
        var q = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("S256", q["code_challenge_method"].ToString());
        Assert.Equal(ClientApp, q["client_id"].ToString());
        Assert.StartsWith("https://login.microsoftonline.com/" + Tenant, location.ToString());
        _app.Entra.NextClaims.Clear();
        _app.Entra.NextClaims["tid"] = tenant ?? Tenant;
        _app.Entra.NextClaims["oid"] = oid;
        _app.Entra.NextClaims["preferred_username"] = upn;
        _app.Entra.NextClaims["name"] = "Test User";
        _app.Entra.NextClaims["nonce"] = badNonce ? "other" : q["nonce"].ToString();
        return await browser.GetAsync($"/api/v1/auth/entra/callback?code=abc&state={Uri.EscapeDataString(q["state"].ToString())}");
    }

    [Fact]
    public async Task Entra_users_are_pre_provisioned_bound_at_first_sign_in_and_validated()
    {
        var admin = await AdminAsync("admin-entra");
        Assert.Equal("entra_not_configured", await (await admin.PostJsonAsync("/api/v1/users", new { userName = "anna@contoso.com", authSource = "entra" })).CodeAsync());
        await ConfigureEntraAsync(admin);
        var state = await (await _app.CreateBrowser().GetAsync("/api/v1/auth/state")).JsonAsync();
        Assert.Contains(state.GetProperty("providers").EnumerateArray(), p => p.GetString() == "entra");

        var created = await (await admin.PostJsonAsync("/api/v1/users", new { userName = "anna@contoso.com", authSource = "entra", roleId = await RoleIdAsync(BuiltInRoles.ReadOnly) })).JsonAsync();
        Assert.Equal(System.Text.Json.JsonValueKind.Null, created.GetProperty("temporaryPassword").ValueKind);
        Assert.Null(await _app.WithDbAsync(db => db.Users.Where(u => u.UserName == "anna@contoso.com").Select(u => u.PasswordHash).FirstAsync()));

        // Not provisioned, wrong tenant, bad nonce, tampered state: refused.
        Assert.Equal("/login?error=entra_not_provisioned", (await SignInWithEntraAsync(_app.CreateBrowser(), Guid.NewGuid().ToString(), "eve@contoso.com")).Headers.Location!.ToString());
        // Issuer validation already refuses tokens of other tenants (the tid check is a second barrier).
        Assert.Equal("/login?error=entra_invalid_token", (await SignInWithEntraAsync(_app.CreateBrowser(), Guid.NewGuid().ToString(), "anna@contoso.com", tenant: Guid.NewGuid().ToString())).Headers.Location!.ToString());
        Assert.Equal("/login?error=entra_invalid_token", (await SignInWithEntraAsync(_app.CreateBrowser(), Guid.NewGuid().ToString(), "anna@contoso.com", badNonce: true)).Headers.Location!.ToString());
        _app.Entra.SignWithForeignKey = true;
        Assert.Equal("/login?error=entra_invalid_token", (await SignInWithEntraAsync(_app.CreateBrowser(), Guid.NewGuid().ToString(), "anna@contoso.com")).Headers.Location!.ToString());
        _app.Entra.SignWithForeignKey = false;
        var noState = await _app.CreateBrowser().GetAsync("/api/v1/auth/entra/callback?code=abc&state=forged");
        Assert.Equal("/login?error=entra_state", noState.Headers.Location!.ToString());

        // Valid: bound to the object id, session created, no local MFA restriction.
        var oid = Guid.NewGuid().ToString();
        var browser = _app.CreateBrowser();
        var ok = await SignInWithEntraAsync(browser, oid, "ANNA@contoso.com");
        Assert.Equal("/vms", ok.Headers.Location!.ToString());
        var me = await (await browser.GetAsync("/api/v1/auth/me")).JsonAsync();
        Assert.Equal("anna@contoso.com", me.GetProperty("userName").GetString());
        Assert.Equal("entra", me.GetProperty("authSource").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, me.GetProperty("restriction").ValueKind);
        Assert.Equal(oid, await _app.WithDbAsync(db => db.Users.Where(u => u.UserName == "anna@contoso.com").Select(u => u.ExternalObjectId).FirstAsync()));

        // Once bound, another Entra account with the same UPN (recreated user, different oid) is refused.
        Assert.Equal("/login?error=entra_not_provisioned", (await SignInWithEntraAsync(_app.CreateBrowser(), Guid.NewGuid().ToString(), "anna@contoso.com")).Headers.Location!.ToString());
        // Entra users cannot sign in with a password.
        Assert.Equal(HttpStatusCode.Unauthorized, (await _app.CreateBrowser().PostJsonAsync("/api/v1/auth/login", new { userName = "anna@contoso.com", password = "anything at all here 12" })).StatusCode);

        // Re-authentication through Entra marks the current session.
        // A fresh sign-in counts as re-authentication: clear it to observe the Entra re-authentication.
        await _app.WithDbAsync(db => db.Sessions.ExecuteUpdateAsync(u => u.SetProperty(s => s.ReauthenticatedAt, (DateTimeOffset?)null)));
        var reauth = await SignInWithEntraAsync(browser, oid, "anna@contoso.com", mode: "reauth");
        Assert.Equal("/auth/reauth-done?status=ok", reauth.Headers.Location!.ToString());
        Assert.NotNull(await _app.WithDbAsync(db => db.Sessions.Where(s => s.UserId == db.Users.First(u => u.UserName == "anna@contoso.com").Id).Select(s => s.ReauthenticatedAt).FirstAsync()));
        Assert.True(await _app.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "users.entra.bound")));
    }

    [Fact]
    public async Task Entra_settings_need_settings_manage_and_complete_values()
    {
        var admin = await AdminAsync("admin-set");
        await admin.CsrfAsync();
        Assert.Equal("invalid_entra_settings", await (await admin.PutAsJsonAsync("/api/v1/settings/entra", new { enabled = true, tenantId = "contoso.com", clientId = ClientApp }, GatewayFactory.Json)).CodeAsync());
        Assert.Equal("invalid_entra_settings", await (await admin.PutAsJsonAsync("/api/v1/settings/entra", new { enabled = true, tenantId = Tenant, clientId = ClientApp }, GatewayFactory.Json)).CodeAsync());
        Assert.Equal("invalid_entra_settings", await (await admin.PutAsJsonAsync("/api/v1/settings/entra", new { enabled = false, tenantId = Tenant, clientId = ClientApp, authority = "https://evil.example" }, GatewayFactory.Json)).CodeAsync());
        await _app.CreateUserAsync("op-set", BuiltInRoles.VmOperator);
        var op = _app.CreateBrowser();
        await op.LoginAsync("op-set");
        Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync("/api/v1/settings/entra")).StatusCode);
        await ConfigureEntraAsync(admin);
        // The secret is encrypted at rest.
        var raw = await _app.WithDbAsync(db => db.Settings.Where(s => s.Key == "entra").Select(s => s.Value).FirstAsync());
        Assert.DoesNotContain("s3cr3t-value", raw);
        Assert.False(await _app.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.DetailsJson!.Contains("s3cr3t-value"))));
    }
}
