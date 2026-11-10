using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Agents;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Jobs;
using HvPortal.Gateway.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace HvPortal.Gateway.Tests.Infrastructure;

/// <summary>
/// Real gateway pipeline (auth, CSRF, RBAC, jobs, audit, SQLite) on TestServer with an isolated data directory.
/// Only the agent transport is replaced by <see cref="FakeAgentDispatcher"/>; the job runner loop is disabled
/// so tests drive it deterministically.
/// </summary>
public sealed class GatewayFactory : WebApplicationFactory<Program>
{
    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "hvp-tests", Guid.NewGuid().ToString("N"));
    public FakeAgentDispatcher Agent { get; } = new();

    /// <summary>TEST DOUBLE of GitHub and of the updater task (portal updates, ADR-042).</summary>
    public FakeReleases Releases { get; } = new();
    public FakeEntraClient Entra { get; } = new();
    public string PackagesDirectory => Path.Combine(DataDirectory, "packages-test");
    /// <summary>Configuration values of a single test (e.g. an option switched off).</summary>
    public Dictionary<string, string> ExtraSettings { get; } = [];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Gateway:DataDirectory", DataDirectory);
        builder.UseSetting("Gateway:EnforcePortSeparation", "false");
        builder.UseSetting("Gateway:Security:Pbkdf2Iterations", "10000");
        builder.UseSetting("Gateway:Jobs:RunnerEnabled", "false");
        builder.UseSetting("Gateway:VmGroups:SyncEnabled", "false");
        builder.UseSetting("Gateway:Backup:SchedulerEnabled", "false");
        builder.UseSetting("Gateway:Metrics:CollectorEnabled", "false");
        builder.UseSetting("Gateway:Clusters:MonitorEnabled", "false");
        builder.UseSetting("Gateway:Updates:CheckerEnabled", "false");
        builder.UseSetting("Gateway:PackagesDirectory", PackagesDirectory);
        foreach (var (key, value) in ExtraSettings)
        {
            builder.UseSetting(key, value);
        }
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAgentDispatcher>();
            services.AddSingleton<IAgentDispatcher>(Agent);
            services.RemoveAll<HvPortal.Gateway.Updates.IReleaseSource>();
            services.AddSingleton<HvPortal.Gateway.Updates.IReleaseSource>(Releases);
            services.RemoveAll<HvPortal.Gateway.Updates.IUpdaterLauncher>();
            services.AddSingleton<HvPortal.Gateway.Updates.IUpdaterLauncher>(Releases);
            services.RemoveAll<HvPortal.Gateway.Security.Entra.IEntraClient>();
            services.AddSingleton<HvPortal.Gateway.Security.Entra.IEntraClient>(Entra);
        });
    }

    public HttpClient CreateBrowser() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        HandleCookies = true,
        AllowAutoRedirect = false,
    });

    /// <summary>Browser whose cookies are accessible (needed to authenticate WebSocket handshakes in tests).</summary>
    public (HttpClient Client, System.Net.CookieContainer Cookies) CreateBrowserWithCookies()
    {
        var cookies = new System.Net.CookieContainer();
        var client = CreateDefaultClient(new Uri("https://localhost"), new Microsoft.AspNetCore.Mvc.Testing.Handlers.CookieContainerHandler(cookies));
        return (client, cookies);
    }

    public JobRunner Runner => Services.GetRequiredService<JobRunner>();

    public HvPortal.Gateway.VmBackup.BackupScheduler Scheduler => Services.GetRequiredService<HvPortal.Gateway.VmBackup.BackupScheduler>();

    public async Task<T> WithDbAsync<T>(Func<PortalDbContext, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<PortalDbContext>());
    }

    public Task WithDbAsync(Func<PortalDbContext, Task> action) => WithDbAsync(async db => { await action(db); return 0; });

    public async Task RunJobsAsync()
    {
        await Runner.ProcessQueueAsync(CancellationToken.None);
        await Runner.DrainAsync();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try
        {
            if (Directory.Exists(DataDirectory))
            {
                Directory.Delete(DataDirectory, true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ------------------------------------------------------------------ seeding

    public const string DefaultPassword = "orbit lantern violet harbor 42";

    public async Task<Guid> CreateUserAsync(string userName, string? roleKey = null, ScopeType scope = ScopeType.Global, string scopeId = "",
        AssignmentEffect effect = AssignmentEffect.Allow, string password = DefaultPassword)
    {
        var hasher = Services.GetRequiredService<PasswordHasher>();
        return await WithDbAsync(async db =>
        {
            var user = new User
            {
                UserName = userName,
                NormalizedUserName = userName.ToUpperInvariant(),
                PasswordHash = hasher.Hash(password),
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            if (roleKey is not null)
            {
                await AssignAsync(db, user.Id, roleKey, scope, scopeId, effect);
            }
            return user.Id;
        });
    }

    public Task AssignAsync(Guid userId, string roleKey, ScopeType scope, string scopeId, AssignmentEffect effect = AssignmentEffect.Allow) =>
        WithDbAsync(db => AssignAsync(db, userId, roleKey, scope, scopeId, effect));

    private static async Task AssignAsync(PortalDbContext db, Guid userId, string roleKey, ScopeType scope, string scopeId, AssignmentEffect effect)
    {
        var role = await db.Roles.FirstAsync(r => r.BuiltInKey == roleKey);
        db.RoleAssignments.Add(new RoleAssignment
        {
            PrincipalId = userId.ToString("D"),
            RoleId = role.Id,
            ScopeType = scope,
            ScopeId = scopeId,
            Effect = effect,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    public Task SetMfaPolicyAsync(MfaRequirement requirement) => WithDbAsync(async db =>
    {
        var store = new SettingsStore(db, TimeProvider.System);
        await store.SaveSecurityAsync(new SecuritySettings { MfaRequiredFor = requirement });
    });

    /// <summary>Creates an active host with VMs, and registers the VMs in the fake agent.</summary>
    public async Task<(Guid HostId, Dictionary<string, Guid> Vms)> CreateHostAsync(string name, params (string Name, VmPowerState State)[] vms)
    {
        var map = new Dictionary<string, Guid>();
        var hostId = await WithDbAsync(async db =>
        {
            var host = new ManagedHost
            {
                DisplayName = name,
                Status = HostStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
                LastInventoryAt = DateTimeOffset.UtcNow,
                LastSeenAt = DateTimeOffset.UtcNow,
                Access = HyperVAccessStatus.Ok,
                HostFactsJson = JsonSerializer.Serialize(new HostFacts { ComputerName = name.ToUpperInvariant(), AgentVersion = "test", VirtualSwitches = ["vSwitch"] }),
            };
            db.Hosts.Add(host);
            foreach (var (vmName, state) in vms)
            {
                var vm = new VirtualMachine
                {
                    HostId = host.Id,
                    HyperVId = Guid.NewGuid(),
                    Name = vmName,
                    PowerState = state,
                    ShutdownServiceAvailable = true,
                    FirstSeenAt = DateTimeOffset.UtcNow,
                    LastSeenAt = DateTimeOffset.UtcNow,
                };
                db.VirtualMachines.Add(vm);
                map[vmName] = vm.Id;
                Agent.AddVm(host.Id, vm.HyperVId, vmName, state);
            }
            await db.SaveChangesAsync();
            return host.Id;
        });
        Agent.Connect(hostId);
        return (hostId, map);
    }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
}

public static class BrowserExtensions
{
    public static async Task<string> CsrfAsync(this HttpClient client)
    {
        var doc = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/csrf");
        var token = doc.GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);
        return token;
    }

    public static async Task<HttpResponseMessage> PostJsonAsync(this HttpClient client, string url, object? body = null)
    {
        await client.CsrfAsync();
        return await client.PostAsJsonAsync(url, body ?? new { }, GatewayFactory.Json);
    }

    public static async Task<HttpResponseMessage> DeleteWithCsrfAsync(this HttpClient client, string url)
    {
        await client.CsrfAsync();
        return await client.DeleteAsync(url);
    }

    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    public static async Task<string?> CodeAsync(this HttpResponseMessage response)
    {
        var json = await response.JsonAsync();
        return json.TryGetProperty("code", out var c) ? c.GetString() : null;
    }

    public static async Task LoginAsync(this HttpClient client, string userName, string password = GatewayFactory.DefaultPassword)
    {
        var r = await client.PostJsonAsync("/api/v1/auth/login", new { userName, password });
        r.EnsureSuccessStatusCode();
        var status = (await r.JsonAsync()).GetProperty("status").GetString();
        Assert.Equal("ok", status);
    }

    /// <summary>Marks the current session as recently re-authenticated (password re-entry).</summary>
    public static async Task ReauthAsync(this HttpClient client, string password = GatewayFactory.DefaultPassword)
    {
        var r = await client.PostJsonAsync("/api/v1/auth/reauth", new { password });
        r.EnsureSuccessStatusCode();
    }
}
