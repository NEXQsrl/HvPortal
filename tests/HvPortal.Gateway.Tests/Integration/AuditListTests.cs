using System.Net;
using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Security;
using HvPortal.Gateway.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HvPortal.Gateway.Tests.Integration;

/// <summary>Audit list for people: names of the resources instead of identifiers, free-text search, period.</summary>
public sealed class AuditListTests : IAsyncLifetime
{
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

    [Fact]
    public async Task Entries_carry_the_name_of_their_resource_and_can_be_searched_by_it()
    {
        await _app.CreateUserAsync("audit-admin", BuiltInRoles.PlatformAdmin);
        var c = _app.CreateBrowser();
        await c.LoginAsync("audit-admin");
        var (hostId, _) = await _app.CreateHostAsync("audit-host", ("billing-db", VmPowerState.Off));
        var created = await c.PostJsonAsync("/api/v1/groups", new { name = "Finance" });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var groupId = (await created.JsonAsync()).GetProperty("id").GetGuid();
        // An entry about the host that stores only its identifier.
        Assert.Equal(HttpStatusCode.OK, (await c.PostJsonAsync($"/api/v1/hosts/{hostId}/enrollment-token")).StatusCode);

        // Found by the name of the resource, which the entry does not contain.
        var byHost = await (await c.GetAsync("/api/v1/audit?q=audit-host")).JsonAsync();
        var hostEntry = byHost.GetProperty("items").EnumerateArray().First(e => e.GetProperty("action").GetString() == "hosts.enrollment_token");
        Assert.Equal("audit-host", hostEntry.GetProperty("resourceName").GetString());
        Assert.True(hostEntry.GetProperty("resourceExists").GetBoolean());
        Assert.Equal(hostId.ToString("D"), hostEntry.GetProperty("resourceId").GetString());

        var byGroup = await (await c.GetAsync("/api/v1/audit?q=Finance&action=groups.")).JsonAsync();
        var groupEntry = Assert.Single(byGroup.GetProperty("items").EnumerateArray());
        Assert.Equal("groups.create", groupEntry.GetProperty("action").GetString());
        Assert.Equal("Finance", groupEntry.GetProperty("resourceName").GetString());

        // Removed resource: the name recorded in the entry is still shown, marked as no longer existing.
        await c.CsrfAsync();
        Assert.True((await c.DeleteAsync($"/api/v1/groups/{groupId}")).IsSuccessStatusCode);
        var afterDelete = await (await c.GetAsync("/api/v1/audit?action=groups.create")).JsonAsync();
        var gone = Assert.Single(afterDelete.GetProperty("items").EnumerateArray());
        Assert.Equal("Finance", gone.GetProperty("resourceName").GetString());
        Assert.False(gone.GetProperty("resourceExists").GetBoolean());

        // Period: nothing in the future, nothing before the gateway existed; everything in between.
        var total = (await (await c.GetAsync("/api/v1/audit")).JsonAsync()).GetProperty("total").GetInt32();
        Assert.True(total > 0);
        var future = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(1).ToString("O"));
        var past = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(-1).ToString("O"));
        Assert.Equal(0, (await (await c.GetAsync($"/api/v1/audit?from={future}")).JsonAsync()).GetProperty("total").GetInt32());
        Assert.Equal(0, (await (await c.GetAsync($"/api/v1/audit?to={past}")).JsonAsync()).GetProperty("total").GetInt32());
        Assert.Equal(total + 2, (await (await c.GetAsync($"/api/v1/audit?from={past}&to={future}")).JsonAsync()).GetProperty("total").GetInt32() + 2);
        Assert.Equal(0, (await (await c.GetAsync("/api/v1/audit?q=no-such-thing-anywhere")).JsonAsync()).GetProperty("total").GetInt32());

        // An area is several action prefixes.
        var area = await (await c.GetAsync("/api/v1/audit?action=" + Uri.EscapeDataString("groups.,hosts."))).JsonAsync();
        var actions = area.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("action").GetString()!).ToList();
        Assert.Contains("groups.create", actions);
        Assert.Contains("hosts.enrollment_token", actions);
        Assert.All(actions, a => Assert.True(a.StartsWith("groups.", StringComparison.Ordinal) || a.StartsWith("hosts.", StringComparison.Ordinal), a));
    }

    [Fact]
    public async Task Audit_list_needs_its_permission_and_the_refusal_is_recorded()
    {
        await _app.CreateUserAsync("audit-operator", BuiltInRoles.VmOperator);
        var c = _app.CreateBrowser();
        await c.LoginAsync("audit-operator");
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/v1/audit?q=anything")).StatusCode);
        Assert.True(await _app.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "audit.list" && a.Outcome == AuditOutcome.Denied && a.ActorName == "audit-operator")));
    }
}
