using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using HvPortal.Gateway;
using HvPortal.Gateway.Agents;
using HvPortal.Gateway.Api;
using HvPortal.Gateway.Audit;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Bootstrap;
using HvPortal.Gateway.Configuration;
using HvPortal.Gateway.Crypto;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Hosting;
using HvPortal.Gateway.Jobs;
using HvPortal.Gateway.Pki;
using HvPortal.Gateway.Realtime;
using HvPortal.Gateway.Security;
using HvPortal.Gateway.VmConsole;
using HvPortal.Shared;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.EntityFrameworkCore;

SecureFileSystem.ServiceName = "HvPortalGateway";
if (args.Length > 0 && args[0].Equals("admin", StringComparison.OrdinalIgnoreCase))
{
    return await AdminCli.RunAsync(args[1..]);
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Services.AddWindowsService(o => o.ServiceName = "HvPortalGateway");
GatewayHost.ConfigureServices(builder);
var app = builder.Build();
GatewayHost.ConfigurePipeline(app);
await app.Services.GetRequiredService<BootstrapService>().InitializeAsync(CancellationToken.None);
using (var scope = app.Services.CreateScope())
{
    await HvPortal.Gateway.Backup.ConfigBackup.AfterRestoreAsync(app.Services.GetRequiredService<GatewayPaths>(), app.Services.GetRequiredService<HvPortal.Gateway.Audit.AuditLog>(),
        scope.ServiceProvider.GetRequiredService<HvPortal.Gateway.Data.PortalDbContext>(), TimeProvider.System);
}
await app.RunAsync();
return 0;

namespace HvPortal.Gateway
{
    public static class GatewayHost
    {
        public static void ConfigureServices(WebApplicationBuilder builder)
        {
            var section = builder.Configuration.GetSection(GatewayOptions.Section);
            var options = section.Get<GatewayOptions>() ?? new GatewayOptions();
            builder.Services.Configure<GatewayOptions>(section);
            // Containers: HVP_PUBLIC_NAMES="gw01.contoso.local,10.0.10.5" (one variable instead of Gateway__PublicNames__0..n).
            if (Environment.GetEnvironmentVariable("HVP_PUBLIC_NAMES") is { Length: > 0 } namesVar)
            {
                var names = namesVar.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                options.PublicNames = [.. options.PublicNames.Concat(names).Distinct(StringComparer.OrdinalIgnoreCase)];
                builder.Services.PostConfigure<GatewayOptions>(o => o.PublicNames = [.. o.PublicNames.Concat(names).Distinct(StringComparer.OrdinalIgnoreCase)]);
            }

            var paths = new GatewayPaths(options.DataDirectory ?? GatewayPaths.DefaultRoot());
            if (SecureFileSystem.ContainsReparsePoint(paths.Root))
            {
                throw new InvalidOperationException($"Refusing to use data directory '{paths.Root}': it contains a reparse point.");
            }
            foreach (var dir in new[] { paths.Root, paths.Keys, paths.DataProtectionKeys, paths.Pki, paths.Bootstrap, paths.Database })
            {
                SecureFileSystem.EnsurePrivateDirectory(dir);
            }

            using var startupLogs = LoggerFactory.Create(b => b.AddConsole());
            var startupLog = startupLogs.CreateLogger("HvPortal.Startup");
            // A restore staged from Settings is applied before anything opens the key ring or the database.
            HvPortal.Gateway.Backup.ConfigBackup.ApplyPendingRestore(paths, startupLog);
            var keyRing = KeyRing.LoadOrCreate(paths.Keys);
            if (KeyWrap.KekGeneratedLocally)
            {
                startupLog.LogWarning("No key-encryption key provided (HVP_KEY_ENCRYPTION_KEY_FILE): generated {Path}. Keys are protected only by the file permissions of the data volume; "
                    + "provide the KEK as a Docker secret for production.", Path.Combine(paths.Keys, "kek.bin"));
            }
            var ca = InternalCertificateAuthority.LoadOrCreate(paths.Pki, keyRing, options.PublicNames, startupLog);

            builder.Services.AddSingleton(paths);
            builder.Services.AddSingleton(keyRing);
            builder.Services.AddSingleton(ca);
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton(new PasswordHasher(options.Security.Pbkdf2Iterations));
            builder.Services.AddSingleton<RecoveryCodeService>();
            builder.Services.AddSingleton<AuditLog>();
            builder.Services.AddSingleton<JobSignal>();
            builder.Services.AddSingleton<ConnectedUsers>();
            builder.Services.AddSingleton<ConsoleTicketStore>();
            builder.Services.AddSingleton<ConsoleSessionRegistry>();
            builder.Services.AddSingleton<IRealtimeNotifier, RealtimeNotifier>();
            builder.Services.AddScoped<HvPortal.Gateway.Security.Entra.EntraConfigStore>();
            builder.Services.AddSingleton<HvPortal.Gateway.Security.Entra.IEntraClient, HvPortal.Gateway.Security.Entra.EntraClient>();
            builder.Services.AddSingleton<HvPortal.Gateway.VmGroups.VmGroupSyncSignal>();
            builder.Services.AddSingleton<HvPortal.Gateway.VmGroups.VmGroupSyncService>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<HvPortal.Gateway.VmGroups.VmGroupSyncService>());
            builder.Services.AddSingleton<AgentConnectionManager>();
            builder.Services.AddSingleton<IAgentDispatcher>(sp => sp.GetRequiredService<AgentConnectionManager>());
            builder.Services.AddSingleton<BootstrapService>();
            builder.Services.AddSingleton<JobRunner>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<JobRunner>());
            builder.Services.AddHostedService<AgentSessionWatchdog>();
            builder.Services.AddSingleton<HvPortal.Gateway.Backup.DatabaseBackupService>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<HvPortal.Gateway.Backup.DatabaseBackupService>());

            builder.Services.AddScoped<AccessEvaluator>();
            builder.Services.AddScoped<HvPortal.Gateway.Quotas.GroupPolicyService>();
            builder.Services.AddSingleton<HvPortal.Gateway.Clusters.StorageJobTracker>();
            builder.Services.AddScoped<HvPortal.Gateway.Clusters.ClusterService>();
            builder.Services.AddHttpClient(HvPortal.Gateway.Updates.GitHubReleaseSource.ClientName, c =>
            {
                c.Timeout = TimeSpan.FromMinutes(30);
                c.DefaultRequestHeaders.UserAgent.ParseAdd($"HvPortal-gateway/{ProductInfo.Version}");
                c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            });
            builder.Services.AddSingleton<HvPortal.Gateway.Updates.IReleaseSource, HvPortal.Gateway.Updates.GitHubReleaseSource>();
            builder.Services.AddSingleton<HvPortal.Gateway.Updates.IUpdaterLauncher, HvPortal.Gateway.Updates.ScheduledTaskUpdaterLauncher>();
            builder.Services.AddScoped<HvPortal.Gateway.Updates.PortalUpdateService>();
            builder.Services.AddHostedService<HvPortal.Gateway.Updates.PortalUpdateChecker>();
            builder.Services.AddSingleton<HvPortal.Gateway.Clusters.ClusterMonitor>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<HvPortal.Gateway.Clusters.ClusterMonitor>());
            builder.Services.AddScoped<LoginThrottleService>();
            builder.Services.AddScoped<SettingsStore>();
            builder.Services.AddScoped<SessionService>();
            builder.Services.AddSingleton<ReauthCodeService>();
            builder.Services.AddScoped<EnrollmentService>();
            builder.Services.AddScoped<InventoryIngestor>();
            builder.Services.AddScoped<JobService>();
            // Backups (ADR-031), notifications (ADR-033).
            builder.Services.AddHttpClient();
            // Webhooks: no redirects, never to loopback / link-local / unspecified addresses (checked after DNS resolution).
            builder.Services.AddHttpClient("notifications").ConfigurePrimaryHttpMessageHandler(HvPortal.Gateway.VmBackup.NotificationService.CreateHandler);
            builder.Services.AddSingleton<HvPortal.Gateway.VmBackup.NotificationService>();
            builder.Services.AddSingleton<HvPortal.Gateway.VmBackup.ApiTokenAuditThrottle>();
            builder.Services.AddScoped<HvPortal.Gateway.VmBackup.BackupService>();
            builder.Services.AddSingleton<HvPortal.Gateway.VmBackup.BackupScheduler>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<HvPortal.Gateway.VmBackup.BackupScheduler>());
            // v0.8.0: resource use history of VMs (ADR-039).
            builder.Services.AddSingleton<HvPortal.Gateway.VmMetrics.VmMetricsBuffer>();
            builder.Services.AddSingleton<HvPortal.Gateway.VmMetrics.MetricsCollector>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<HvPortal.Gateway.VmMetrics.MetricsCollector>());

            if (!string.Equals(options.Database.Provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException($"Database provider '{options.Database.Provider}' is planned for Milestone 2. Use 'Sqlite'.");
            }
            var connectionString = options.Database.ConnectionString ?? $"Data Source={Path.Combine(paths.Database, "hvportal.db")};Foreign Keys=True";
            builder.Services.AddDbContext<PortalDbContext>(o => o.UseSqlite(connectionString));

            var dataProtection = builder.Services.AddDataProtection()
                .SetApplicationName("HvPortal")
                .PersistKeysToFileSystem(new DirectoryInfo(paths.DataProtectionKeys));
            if (OperatingSystem.IsWindows())
            {
                dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: true);
            }
            else
            {
                // Linux (container): no DPAPI; keys are sealed with the gateway key ring (KEK-protected).
                builder.Services.Configure<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>(o =>
                    o.XmlEncryptor = new KeyRingXmlEncryptor(keyRing));
            }

            builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                .AddCookie(o =>
                {
                    o.Cookie.Name = "__Host-hvp";
                    o.Cookie.HttpOnly = true;
                    o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                    o.Cookie.SameSite = SameSiteMode.Strict;
                    o.Cookie.Path = "/";
                    o.SlidingExpiration = false;
                    o.ExpireTimeSpan = TimeSpan.FromHours(72);
                    o.Events.OnValidatePrincipal = SessionService.ValidateAsync;
                    o.Events.OnRedirectToLogin = ctx => ApiSupport.Error(401, "unauthenticated").ExecuteAsync(ctx.HttpContext);
                    o.Events.OnRedirectToAccessDenied = ctx => ApiSupport.Forbidden().ExecuteAsync(ctx.HttpContext);
                });
            builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, ApiAuthorizationResultHandler>();
            builder.Services.AddAuthorizationBuilder()
                .AddPolicy(AuthPolicies.AnySession, p => p.RequireAuthenticatedUser().RequireClaim(PortalClaims.SessionId))
                .AddPolicy(AuthPolicies.Full, p => p.RequireAuthenticatedUser().RequireClaim(PortalClaims.SessionId)
                    .RequireAssertion(c => !c.User.HasClaim(x => x.Type == PortalClaims.Restriction)))
                .SetDefaultPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().RequireClaim(PortalClaims.SessionId)
                    .RequireAssertion(c => !c.User.HasClaim(x => x.Type == PortalClaims.Restriction)).Build())
                .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().RequireClaim(PortalClaims.SessionId)
                    .RequireAssertion(c => !c.User.HasClaim(x => x.Type == PortalClaims.Restriction)).Build());

            builder.Services.AddAntiforgery(o =>
            {
                o.HeaderName = "X-CSRF-TOKEN";
                o.Cookie.Name = "__Host-hvp-af";
                o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                o.Cookie.SameSite = SameSiteMode.Strict;
                o.Cookie.HttpOnly = true;
            });

            builder.Services.AddRateLimiter(o =>
            {
                o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "?",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
                o.AddPolicy("enroll", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "?",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
                // Read-only API with tokens (monitoring systems): generous but bounded, per source address.
                o.AddPolicy("api-token", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "?",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            });

            builder.Services.AddSignalR(o =>
            {
                o.MaximumReceiveMessageSize = 32 * 1024;
                o.EnableDetailedErrors = false;
            });
            builder.Services.AddProblemDetails();
            builder.Services.AddOpenApi();
            builder.Services.ConfigureHttpJsonOptions(o =>
            {
                o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
                o.SerializerOptions.MaxDepth = 32;
            });

            builder.WebHost.ConfigureKestrel(k =>
            {
                k.AddServerHeader = false;
                k.Limits.MaxRequestBodySize = 1024 * 1024;
                var bind = System.Net.IPAddress.Parse(options.BindAddress);
                var uiCert = LoadUiCertificate(options) ?? ca.ServerCertificate;
                k.Listen(bind, options.UiPort, l => l.UseHttps(h =>
                {
                    h.ServerCertificate = uiCert;
                    h.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
                }));
                k.Listen(bind, options.AgentPort, l => l.UseHttps(h =>
                {
                    h.ServerCertificate = ca.ServerCertificate;
                    h.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
                    h.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
                    h.CheckCertificateRevocation = false; // revocation is enforced against the database
                    h.ClientCertificateValidation = (cert, _, _) => ca.ValidateClientCertificate(cert);
                }));
            });
        }

        public static void ConfigurePipeline(WebApplication app)
        {
            var options = app.Configuration.GetSection(GatewayOptions.Section).Get<GatewayOptions>() ?? new GatewayOptions();
            var devOrigins = app.Environment.IsDevelopment()
                ? app.Configuration.GetSection("Gateway:DevAllowedOrigins").Get<string[]>() ?? []
                : [];

            app.UseExceptionHandler();
            if (!app.Environment.IsDevelopment())
            {
                app.UseHsts();
            }
            app.UseGatewayBaseline(options);
            // No UseDefaultFiles: "/" and client routes are rendered by SpaShell (CSP nonce substitution).
            app.UseStaticFiles(new StaticFileOptions
            {
                OnPrepareResponse = ctx =>
                {
                    // Hashed asset names (Vite) can be cached forever; other files (favicon) briefly.
                    ctx.Context.Response.Headers.CacheControl = ctx.Context.Request.Path.StartsWithSegments("/assets")
                        ? "public, max-age=31536000, immutable" : "public, max-age=3600";
                },
            });
            app.UseRouting();
            // Before the cross-site checks, which need to recognize WebSocket handshakes (HTTP/1.1 GET and HTTP/2 CONNECT).
            app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
            app.UseRateLimiter();
            app.UseAuthentication();
            app.UseCrossSiteProtection(devOrigins);
            app.UseAuthorization();

            AuthEndpoints.Map(app);
            MfaEndpoints.Map(app);
            AdminEndpoints.Map(app);
            InventoryEndpoints.Map(app);
            JobEndpoints.Map(app);
            AgentEndpoints.Map(app);
            DownloadEndpoints.Map(app);
            InventoryManagementEndpoints.Map(app);
            ManagementAgentEndpoints.Map(app);
            GroupEndpoints.Map(app);
            GroupPolicyEndpoints.Map(app);
            VmOperationsEndpoints.Map(app);
            ConsoleEndpoints.Map(app);
            HvPortal.Gateway.VmGroups.VmGroupEndpoints.Map(app);
            HvPortal.Gateway.Api.TemplateEndpoints.Map(app);
            HostFileEndpoints.Map(app);
            HvPortal.Gateway.Backup.ConfigBackup.Map(app);
            HvPortal.Gateway.Backup.DatabaseBackupService.Map(app);
            HvPortal.Gateway.Security.Entra.EntraEndpoints.Map(app);
            // v0.7.0: networks with VLAN, host health, VM move, agent updates, backups, notifications, API tokens.
            NetworkEndpoints.Map(app);
            HostOperationsEndpoints.Map(app);
            ClusterEndpoints.Map(app);
            HvPortal.Gateway.Updates.PortalUpdateEndpoints.Map(app);
            AgentUpdateEndpoints.Map(app);
            HvPortal.Gateway.VmBackup.BackupEndpoints.Map(app);
            HvPortal.Gateway.VmBackup.BackupFileEndpoints.Map(app);
            HvPortal.Gateway.VmBackup.NotificationEndpoints.Map(app);
            HvPortal.Gateway.VmBackup.ApiTokenEndpoints.Map(app);
            HvPortal.Gateway.VmMetrics.MetricsEndpoints.Map(app);

            app.MapGet("/api/v1/system/info", async (HttpContext http, AccessEvaluator access, AgentConnectionManager agents, GatewayPaths paths) =>
            {
                var admin = await access.HasAsync(http.User.UserId(), Permissions.SettingsManage, ResourceRef.Global, http.RequestAborted);
                return Results.Ok(new
                {
                    version = ProductInfo.Version,
                    runningAsService = ProductInfo.RunningAsService,
                    milestone = "M1+",
                    database = "SQLite",
                    connectedAgents = agents.ConnectedHosts.Count,
                    telemetry = "disabled (no telemetry is implemented)",
                    dataDirectory = admin ? paths.Root : null,
                    internetRequired = "No. Only Microsoft Entra ID sign-in, when enabled, needs HTTPS to Microsoft endpoints; checking for updates contacts GitHub (api.github.com) when an administrator asks.",
                });
            }).WithTags("System");
            app.MapGet("/healthz", () => Results.Text("ok")).AllowAnonymous().ExcludeFromDescription();
            app.MapHub<EventsHub>("/hubs/events");
            app.MapOpenApi("/openapi/{documentName}.json").RequireAuthorization(AuthPolicies.Full);
            // Unknown API/agent/hub routes must be a 404 problem, never the SPA shell.
            foreach (var prefix in new[] { "/api", "/agent", "/hubs", "/openapi" })
            {
                app.Map(prefix + "/{**rest}", () => ApiSupport.NotFound()).AllowAnonymous().ExcludeFromDescription();
            }
            app.MapSpaShell();
        }

        private static X509Certificate2? LoadUiCertificate(GatewayOptions options)
        {
            if (!string.IsNullOrWhiteSpace(options.UiCertificatePath))
            {
                // PFX (password from UiCertificatePassword or the file in UiCertificatePasswordFile) or PEM certificate + key.
                var password = options.UiCertificatePasswordFile is { Length: > 0 } pf ? File.ReadAllText(pf).Trim() : options.UiCertificatePassword;
                return options.UiCertificatePath.EndsWith(".pem", StringComparison.OrdinalIgnoreCase) || options.UiCertificatePath.EndsWith(".crt", StringComparison.OrdinalIgnoreCase)
                    ? X509Certificate2.CreateFromPemFile(options.UiCertificatePath, options.UiCertificateKeyPath)
                    : X509CertificateLoader.LoadPkcs12FromFile(options.UiCertificatePath, password);
            }
            if (string.IsNullOrWhiteSpace(options.UiCertificateThumbprint))
            {
                return null;
            }
            if (!OperatingSystem.IsWindows())
            {
                throw new InvalidOperationException("UiCertificateThumbprint (Windows certificate store) is not available on this platform: use UiCertificatePath.");
            }
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);
            var found = store.Certificates.Find(X509FindType.FindByThumbprint, options.UiCertificateThumbprint.Replace(" ", ""), validOnly: false);
            return found.Count > 0 ? found[0] : throw new InvalidOperationException($"UI certificate {options.UiCertificateThumbprint} not found in LocalMachine\\My.");
        }
    }
}
