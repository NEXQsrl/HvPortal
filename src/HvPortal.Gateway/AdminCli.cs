using System.Security.Principal;
using HvPortal.Gateway.Audit;
using HvPortal.Gateway.Configuration;
using HvPortal.Gateway.Crypto;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Security;
using HvPortal.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace HvPortal.Gateway;

/// <summary>
/// Local administration commands, run on the gateway server from an elevated prompt.
/// Access is controlled by the operating system: the data directory is readable only by
/// Administrators/SYSTEM/the service account.
/// </summary>
public static class AdminCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
        string? Opt(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        var paths = new GatewayPaths(Opt("--data-dir") ?? Environment.GetEnvironmentVariable("HVP_DATA_DIR") ?? GatewayPaths.DefaultRoot());
        if (cmd is not ("help" or "--help" or "-h") && !IsAdministrator() && Environment.GetEnvironmentVariable("HVP_DATA_DIR") is null)
        {
            Console.Error.WriteLine("Run this command from an elevated (Administrator) prompt.");
            return 5;
        }

        switch (cmd)
        {
            case "recover-account":
                return await RecoverAccountAsync(paths, Opt("--user"), args.Contains("--reset-mfa"));
            case "export-keys":
                return ExportKeys(paths, Opt("--out"));
            case "import-keys":
                return ImportKeys(paths, Opt("--in"), args.Contains("--force"));
            case "rotate-keys":
                var id = KeyRing.LoadOrCreate(paths.Keys).Rotate();
                Console.WriteLine($"New active key: {id}. Existing secrets remain readable with the previous keys; export the keys again.");
                return 0;
            case "verify-audit":
                return await VerifyAuditAsync(paths);
            default:
                Console.WriteLine("""
                    HvPortal gateway administration (run elevated on the gateway server)

                      admin recover-account --user <name> [--reset-mfa]   Issue a one-time account recovery token (30 min)
                      admin export-keys --out <file>                      Export key ring + internal CA (passphrase from HVP_EXPORT_PASSPHRASE or prompt)
                      admin import-keys --in <file> [--force]             Import an export on a new/restored server (service stopped)
                      admin rotate-keys                                   Add a new active encryption key
                      admin verify-audit                                  Verify the audit hash chain
                    Options: --data-dir <path> (default %ProgramData%\HvPortal\Gateway or HVP_DATA_DIR)
                    """);
                return cmd is "help" or "--help" or "-h" ? 0 : 64;
        }
    }

    private static PortalDbContext OpenDb(GatewayPaths paths)
    {
        var options = new DbContextOptionsBuilder<PortalDbContext>()
            .UseSqlite($"Data Source={Path.Combine(paths.Database, "hvportal.db")};Foreign Keys=True").Options;
        return new PortalDbContext(options);
    }

    private static async Task<int> RecoverAccountAsync(GatewayPaths paths, string? userName, bool resetMfa)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            Console.Error.WriteLine("--user is required.");
            return 64;
        }
        await using var db = OpenDb(paths);
        var normalized = userName.Trim().ToUpperInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedUserName == normalized && u.AuthSource == "local");
        if (user is null)
        {
            Console.Error.WriteLine("No local user with that name.");
            return 1;
        }
        var token = Tokens.New();
        var now = DateTimeOffset.UtcNow;
        await db.OneTimeTokens.Where(t => t.UserId == user.Id && t.Purpose.StartsWith("account-recovery") && t.ConsumedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(t => t.ExpiresAt, now));
        db.OneTimeTokens.Add(new OneTimeToken
        {
            Purpose = resetMfa ? "account-recovery+mfa" : "account-recovery",
            TokenHash = Tokens.Hash(token),
            UserId = user.Id,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(30),
        });
        await db.SaveChangesAsync();

        var keys = KeyRing.LoadOrCreate(paths.Keys);
        var audit = new AuditLog(new SingleDbScopeFactory(paths), keys, TimeProvider.System, NullLogger<AuditLog>.Instance);
        await audit.WriteAsync(new AuditRecord
        {
            Action = "admin_cli.recover_account",
            AuthMethod = "local-os-admin:" + WindowsIdentity.GetCurrent().Name,
            ResourceType = "user",
            ResourceId = user.Id.ToString("D"),
            Details = new { resetMfa },
        });
        Console.WriteLine($"One-time recovery token for '{user.UserName}' (valid 30 minutes{(resetMfa ? ", MFA will be reset" : "")}):");
        Console.WriteLine(token);
        Console.WriteLine("Use it on the sign-in page: 'Recover account'. All sessions of the user are revoked on use.");
        return 0;
    }

    private static int ExportKeys(GatewayPaths paths, string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            Console.Error.WriteLine("--out is required.");
            return 64;
        }
        var pass = Environment.GetEnvironmentVariable("HVP_EXPORT_PASSPHRASE") ?? ReadSecret("Export passphrase (min 20 chars): ");
        var confirm = Environment.GetEnvironmentVariable("HVP_EXPORT_PASSPHRASE") ?? ReadSecret("Repeat passphrase: ");
        if (pass != confirm)
        {
            Console.Error.WriteLine("Passphrases do not match.");
            return 1;
        }
        var ring = KeyRing.LoadOrCreate(paths.Keys);
        var files = new Dictionary<string, byte[]>();
        foreach (var f in new[] { "ca.bin", "server.bin" })
        {
            var p = Path.Combine(paths.Pki, f);
            if (File.Exists(p))
            {
                files["pki/" + f] = File.ReadAllBytes(p);
            }
        }
        File.WriteAllBytes(output, ring.Export(pass, files));
        Console.WriteLine($"Key export written to {output}. Store it offline, separately from database backups, together with the passphrase procedure.");
        return 0;
    }

    private static int ImportKeys(GatewayPaths paths, string? input, bool force)
    {
        if (string.IsNullOrWhiteSpace(input) || !File.Exists(input))
        {
            Console.Error.WriteLine("--in <existing file> is required.");
            return 64;
        }
        if (File.Exists(Path.Combine(paths.Keys, "master.json")) && !force)
        {
            Console.Error.WriteLine("A key ring already exists. Use --force to replace it (stop the service first).");
            return 1;
        }
        var pass = Environment.GetEnvironmentVariable("HVP_EXPORT_PASSPHRASE") ?? ReadSecret("Export passphrase: ");
        var (keys, active, files) = KeyRing.ReadExport(File.ReadAllBytes(input), pass);
        KeyRing.WriteImported(paths.Keys, keys, active);
        SecureFileSystem.EnsurePrivateDirectory(paths.Pki);
        foreach (var (name, bytes) in files)
        {
            var safe = Path.GetFileName(name);
            if (safe is "ca.bin" or "server.bin")
            {
                SecureFileSystem.WriteAllBytesAtomic(Path.Combine(paths.Pki, safe), bytes);
            }
        }
        Console.WriteLine($"Imported {keys.Count} key(s) and {files.Count} PKI file(s). Restore the database, then start the service.");
        return 0;
    }

    private static async Task<int> VerifyAuditAsync(GatewayPaths paths)
    {
        var keys = KeyRing.LoadOrCreate(paths.Keys);
        var audit = new AuditLog(new SingleDbScopeFactory(paths), keys, TimeProvider.System, NullLogger<AuditLog>.Instance);
        var r = await audit.VerifyAsync(CancellationToken.None);
        Console.WriteLine(r.Valid ? $"Audit chain valid ({r.EntriesChecked} entries)." : $"AUDIT CHAIN INVALID at entry {r.FirstInvalidId}: {r.Problem}");
        return r.Valid ? 0 : 2;
    }

    private static bool IsAdministrator()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string ReadSecret(string prompt)
    {
        Console.Write(prompt);
        var chars = new List<char>();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter)
            {
                break;
            }
            if (k.Key == ConsoleKey.Backspace)
            {
                if (chars.Count > 0)
                {
                    chars.RemoveAt(chars.Count - 1);
                }
                continue;
            }
            chars.Add(k.KeyChar);
        }
        Console.WriteLine();
        return new string(chars.ToArray());
    }

    /// <summary>Minimal scope factory so AuditLog can be used outside the web host.</summary>
    private sealed class SingleDbScopeFactory(GatewayPaths paths) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(OpenDb(paths));

        private sealed class Scope(PortalDbContext db) : IServiceScope, IServiceProvider
        {
            public IServiceProvider ServiceProvider => this;
            public object? GetService(Type serviceType) => serviceType == typeof(PortalDbContext) ? db : null;
            public void Dispose() => db.Dispose();
        }
    }
}
