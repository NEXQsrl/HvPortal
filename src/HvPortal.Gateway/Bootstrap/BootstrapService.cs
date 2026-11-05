using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Configuration;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Security;
using HvPortal.Shared;
using Microsoft.EntityFrameworkCore;

namespace HvPortal.Gateway.Bootstrap;

/// <summary>
/// First-run initialization:
///  - applies database migrations and synchronizes built-in roles with the code definition;
///  - while no user exists, issues a one-time setup token (24 h) written ONLY to
///    &lt;data&gt;\bootstrap\setup-token.txt, whose ACL allows SYSTEM, Administrators and the service account.
///    The token is never logged, never shown in the UI and never sent anywhere. Each restart replaces it.
/// There are no default credentials.
/// </summary>
public sealed class BootstrapService
{
    public const string SetupPurpose = "setup";
    private readonly IServiceScopeFactory _scopes;
    private readonly GatewayPaths _paths;
    private readonly TimeProvider _time;
    private readonly ILogger<BootstrapService> _logger;

    public BootstrapService(IServiceScopeFactory scopes, GatewayPaths paths, TimeProvider time, ILogger<BootstrapService> logger)
    {
        _scopes = scopes;
        _paths = paths;
        _time = time;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
        await db.Database.MigrateAsync(ct);
        if (db.Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite")
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
        }
        await SyncBuiltInRolesAsync(db, ct);

        if (!await db.Users.AnyAsync(ct))
        {
            await IssueSetupTokenAsync(db, ct);
        }
        else if (File.Exists(_paths.SetupTokenFile))
        {
            File.Delete(_paths.SetupTokenFile);
        }
    }

    public static async Task SyncBuiltInRolesAsync(PortalDbContext db, CancellationToken ct)
    {
        foreach (var def in BuiltInRoles.All)
        {
            var role = await db.Roles.Include(r => r.Permissions).FirstOrDefaultAsync(r => r.BuiltInKey == def.Key, ct);
            if (role is null)
            {
                role = new Role { Name = def.Name, Description = def.Description, IsBuiltIn = true, BuiltInKey = def.Key };
                db.Roles.Add(role);
            }
            role.Name = def.Name;
            role.Description = def.Description;
            var wanted = def.Permissions.ToHashSet(StringComparer.Ordinal);
            role.Permissions.RemoveAll(p => !wanted.Contains(p.Permission));
            foreach (var p in wanted.Where(w => role.Permissions.All(rp => rp.Permission != w)))
            {
                role.Permissions.Add(new RolePermission { RoleId = role.Id, Permission = p });
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task IssueSetupTokenAsync(PortalDbContext db, CancellationToken ct)
    {
        await db.OneTimeTokens.Where(t => t.Purpose == SetupPurpose).ExecuteDeleteAsync(ct);
        var token = Tokens.New();
        var now = _time.GetUtcNow();
        db.OneTimeTokens.Add(new OneTimeToken
        {
            Purpose = SetupPurpose,
            TokenHash = Tokens.Hash(token),
            CreatedAt = now,
            ExpiresAt = now.AddHours(24),
        });
        await db.SaveChangesAsync(ct);

        SecureFileSystem.EnsurePrivateDirectory(_paths.Bootstrap);
        SecureFileSystem.WriteAllTextAtomic(_paths.SetupTokenFile,
            "HvPortal one-time setup token (valid 24 hours, single use, replaced at every service restart until setup completes):\r\n" +
            token + "\r\n");
        _logger.LogWarning("Initial setup required. Open the portal and use the one-time setup token stored in {Path} " +
                           "(readable by local administrators only).", _paths.SetupTokenFile);
        // Containers (Portainer): HVP_PRINT_SETUP_TOKEN=true also writes it to the container log, which only host
        // administrators can read. Never enabled by default; the token is single-use and becomes useless after setup.
        if (string.Equals(Environment.GetEnvironmentVariable("HVP_PRINT_SETUP_TOKEN"), "true", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("HvPortal setup token (single use, 24 h): " + token);
        }
    }
}
