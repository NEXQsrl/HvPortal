using HvPortal.Gateway.Audit;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Bootstrap;
using HvPortal.Gateway.Crypto;
using HvPortal.Gateway.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HvPortal.Gateway.Tests.Unit;

/// <summary>Real SQLite (in-memory, migrations applied) for evaluator and audit tests.</summary>
public sealed class SqliteFixture : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    public ServiceProvider Services { get; }

    public SqliteFixture()
    {
        _connection.Open();
        var services = new ServiceCollection();
        services.AddDbContext<PortalDbContext>(o => o.UseSqlite(_connection));
        Services = services.BuildServiceProvider();
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
        db.Database.Migrate();
        BootstrapService.SyncBuiltInRolesAsync(db, CancellationToken.None).GetAwaiter().GetResult();
    }

    public PortalDbContext NewDb() => Services.CreateScope().ServiceProvider.GetRequiredService<PortalDbContext>();

    public void Dispose()
    {
        Services.Dispose();
        _connection.Dispose();
    }
}

public sealed class AccessEvaluatorTests : IDisposable
{
    private readonly SqliteFixture _fx = new();
    private readonly Guid _hostA = Guid.NewGuid();
    private readonly Guid _hostB = Guid.NewGuid();
    private readonly Guid _vmA1 = Guid.NewGuid();
    private readonly Guid _vmA2 = Guid.NewGuid();
    private readonly Guid _vmB1 = Guid.NewGuid();
    private readonly Guid _group = Guid.NewGuid();

    public AccessEvaluatorTests()
    {
        using var db = _fx.NewDb();
        db.Hosts.AddRange(new ManagedHost { Id = _hostA, DisplayName = "A" }, new ManagedHost { Id = _hostB, DisplayName = "B" });
        db.VirtualMachines.AddRange(
            new VirtualMachine { Id = _vmA1, HostId = _hostA, HyperVId = Guid.NewGuid(), Name = "a1" },
            new VirtualMachine { Id = _vmA2, HostId = _hostA, HyperVId = Guid.NewGuid(), Name = "a2" },
            new VirtualMachine { Id = _vmB1, HostId = _hostB, HyperVId = Guid.NewGuid(), Name = "b1" });
        db.LogicalGroups.Add(new LogicalGroup { Id = _group, Name = "g" });
        db.LogicalGroupMembers.Add(new LogicalGroupMember { GroupId = _group, VmId = _vmB1 });
        db.SaveChanges();
    }

    private Guid User(bool enabled = true)
    {
        using var db = _fx.NewDb();
        var u = new User { UserName = Guid.NewGuid().ToString("N")[..10], NormalizedUserName = Guid.NewGuid().ToString("N"), IsEnabled = enabled };
        db.Users.Add(u);
        db.SaveChanges();
        return u.Id;
    }

    private void Assign(Guid user, string roleKey, ResourceRef scope, AssignmentEffect effect = AssignmentEffect.Allow)
    {
        using var db = _fx.NewDb();
        var role = db.Roles.First(r => r.BuiltInKey == roleKey);
        db.RoleAssignments.Add(new RoleAssignment { PrincipalId = user.ToString("D"), RoleId = role.Id, ScopeType = scope.Type, ScopeId = scope.Id, Effect = effect });
        db.SaveChanges();
    }

    private Task<bool> Has(Guid user, string perm, ResourceRef r) => new AccessEvaluator(_fx.NewDb()).HasAsync(user, perm, r);

    [Fact]
    public async Task Default_is_deny()
    {
        var u = User();
        Assert.False(await Has(u, Permissions.InventoryRead, ResourceRef.Vm(_vmA1)));
        Assert.False(await Has(u, Permissions.InventoryRead, ResourceRef.Global));
    }

    [Fact]
    public async Task Global_assignment_inherits_to_hosts_and_vms()
    {
        var u = User();
        Assign(u, BuiltInRoles.ReadOnly, ResourceRef.Global);
        Assert.True(await Has(u, Permissions.InventoryRead, ResourceRef.Vm(_vmB1)));
        Assert.True(await Has(u, Permissions.InventoryRead, ResourceRef.Host(_hostA)));
        Assert.False(await Has(u, Permissions.VmPower, ResourceRef.Vm(_vmB1)));
    }

    [Fact]
    public async Task Host_assignment_covers_only_that_hosts_vms()
    {
        var u = User();
        Assign(u, BuiltInRoles.VmOperator, ResourceRef.Host(_hostA));
        Assert.True(await Has(u, Permissions.VmPower, ResourceRef.Vm(_vmA1)));
        Assert.True(await Has(u, Permissions.VmPower, ResourceRef.Vm(_vmA2)));
        Assert.False(await Has(u, Permissions.VmPower, ResourceRef.Vm(_vmB1)));
    }

    [Fact]
    public async Task Vm_assignment_does_not_leak_to_sibling_vm()
    {
        var u = User();
        Assign(u, BuiltInRoles.VmOperator, ResourceRef.Vm(_vmA1));
        Assert.True(await Has(u, Permissions.VmPower, ResourceRef.Vm(_vmA1)));
        Assert.False(await Has(u, Permissions.InventoryRead, ResourceRef.Vm(_vmA2)));
        Assert.False(await Has(u, Permissions.InventoryRead, ResourceRef.Host(_hostA)));
    }

    [Fact]
    public async Task Explicit_deny_wins_over_allow_at_any_level()
    {
        var u = User();
        Assign(u, BuiltInRoles.InfrastructureAdmin, ResourceRef.Global);
        Assign(u, BuiltInRoles.VmOperator, ResourceRef.Vm(_vmA1), AssignmentEffect.Deny);
        Assert.False(await Has(u, Permissions.VmPower, ResourceRef.Vm(_vmA1)));
        Assert.True(await Has(u, Permissions.VmPowerForce, ResourceRef.Vm(_vmA1))); // not in the denied role
        Assert.True(await Has(u, Permissions.VmPower, ResourceRef.Vm(_vmA2)));
    }

    [Fact]
    public async Task Deny_on_host_blocks_vm_allow()
    {
        var u = User();
        Assign(u, BuiltInRoles.VmOperator, ResourceRef.Vm(_vmA1));
        Assign(u, BuiltInRoles.ReadOnly, ResourceRef.Host(_hostA), AssignmentEffect.Deny);
        Assert.False(await Has(u, Permissions.InventoryRead, ResourceRef.Vm(_vmA1)));
        Assert.True(await Has(u, Permissions.VmPower, ResourceRef.Vm(_vmA1)));
    }

    [Fact]
    public async Task Logical_group_assignment_applies_to_members()
    {
        var u = User();
        Assign(u, BuiltInRoles.VmOperator, ResourceRef.Group(_group));
        Assert.True(await Has(u, Permissions.VmPower, ResourceRef.Vm(_vmB1)));
        Assert.False(await Has(u, Permissions.VmPower, ResourceRef.Vm(_vmA1)));
    }

    [Fact]
    public async Task Platform_permissions_are_not_granted_by_scoped_assignments()
    {
        var u = User();
        Assign(u, BuiltInRoles.PlatformAdmin, ResourceRef.Host(_hostA));
        Assert.True(await Has(u, Permissions.VmPowerForce, ResourceRef.Vm(_vmA1)));
        Assert.False(await Has(u, Permissions.UsersManage, ResourceRef.Global));
        Assert.False(await Has(u, Permissions.UsersManage, ResourceRef.Host(_hostA)));
    }

    [Fact]
    public async Task Disabled_user_has_no_permissions()
    {
        var u = User(enabled: false);
        Assign(u, BuiltInRoles.PlatformAdmin, ResourceRef.Global);
        Assert.False(await Has(u, Permissions.InventoryRead, ResourceRef.Global));
    }

    [Fact]
    public async Task Unknown_permission_is_denied()
    {
        var u = User();
        Assign(u, BuiltInRoles.PlatformAdmin, ResourceRef.Global);
        Assert.False(await Has(u, "vm.run_script", ResourceRef.Global));
    }

    public void Dispose() => _fx.Dispose();
}

public sealed class AuditLogTests : IDisposable
{
    private readonly SqliteFixture _fx = new();
    private readonly AuditLog _log;

    public AuditLogTests()
    {
        _log = new AuditLog(_fx.Services.GetRequiredService<IServiceScopeFactory>(), KeyRing.CreateEphemeral(), TimeProvider.System, NullLogger<AuditLog>.Instance);
    }

    private async Task WriteThreeAsync()
    {
        for (var i = 0; i < 3; i++)
        {
            await _log.WriteAsync(new AuditRecord { Action = "test." + i, AuthMethod = "test", Details = new { i } });
        }
    }

    [Fact]
    public async Task Valid_chain_verifies()
    {
        await WriteThreeAsync();
        var r = await _log.VerifyAsync(CancellationToken.None);
        Assert.True(r.Valid);
        Assert.Equal(3, r.EntriesChecked);
    }

    [Fact]
    public async Task Modified_entry_is_detected()
    {
        await WriteThreeAsync();
        using (var db = _fx.NewDb())
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE AuditEntries SET Outcome = 'Failure' WHERE Action = 'test.1'");
        }
        var r = await _log.VerifyAsync(CancellationToken.None);
        Assert.False(r.Valid);
        Assert.Contains("modified", r.Problem);
    }

    [Fact]
    public async Task Deleted_entry_is_detected()
    {
        await WriteThreeAsync();
        using (var db = _fx.NewDb())
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM AuditEntries WHERE Action = 'test.1'");
        }
        var r = await _log.VerifyAsync(CancellationToken.None);
        Assert.False(r.Valid);
        Assert.Contains("Chain link broken", r.Problem);
    }

    public void Dispose() => _fx.Dispose();
}
