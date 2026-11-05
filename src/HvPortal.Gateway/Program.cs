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
            var keyRing = KeyRing.LoadOrCreate(paths.Keys);
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
            builder.Services.AddSingleton<IRealtimeNotifier, RealtimeNotifier>();
            builder.Services.AddSingleton<AgentConnectionManager>();
            builder.Services.AddSingleton<IAgentDispatcher>(sp => sp.GetRequiredService<AgentConnectionManager>());
            builder.Services.AddSingleton<BootstrapService>();
            builder.Services.AddSingleton<JobRunner>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<JobRunner>());
            builder.Services.AddHostedService<AgentSessionWatchdog>();

            builder.Services.AddScoped<AccessEvaluator>();
            builder.Services.AddScoped<LoginThrottleService>();
            builder.Services.AddScoped<SettingsStore>();
            builder.Services.AddScoped<SessionService>();
            builder.Services.AddScoped<EnrollmentService>();
            builder.Services.AddScoped<InventoryIngestor>();
            builder.Services.AddScoped<JobService>();

            if (!string.Equals(options.Database.Provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException($"Database provider '{options.Database.Provider}' is planned for Milestone 2. Use 'Sqlite'.");
            }
            var connectionString = options.Database.ConnectionString ?? $"Data Source={Path.Combine(paths.Database, "hvportal.db")};Foreign Keys=True";
            builder.Services.AddDbContext<PortalDbContext>(o => o.UseSqlite(connectionString));

            builder.Services.AddDataProtection()
                .SetApplicationName("HvPortal")
                .PersistKeysToFileSystem(new DirectoryInfo(paths.DataProtectionKeys))
                .ProtectKeysWithDpapi(protectToLocalMachine: true);

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
            app.UseDefaultFiles();
            app.UseStaticFiles(new StaticFileOptions
            {
                OnPrepareResponse = ctx =>
                {
                    var name = ctx.File.Name;
                    ctx.Context.Response.Headers.CacheControl = name.Equals("index.html", StringComparison.OrdinalIgnoreCase)
                        ? "no-cache" : "public, max-age=31536000, immutable";
                },
            });
            app.UseRouting();
            app.UseRateLimiter();
            app.UseAuthentication();
            app.UseCrossSiteProtection(devOrigins);
            app.UseAuthorization();
            app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

            AuthEndpoints.Map(app);
            MfaEndpoints.Map(app);
            AdminEndpoints.Map(app);
            InventoryEndpoints.Map(app);
            JobEndpoints.Map(app);
            AgentEndpoints.Map(app);
            DownloadEndpoints.Map(app);
            InventoryManagementEndpoints.Map(app);

            app.MapGet("/api/v1/system/info", async (HttpContext http, AccessEvaluator access, AgentConnectionManager agents, GatewayPaths paths) =>
            {
                var admin = await access.HasAsync(http.User.UserId(), Permissions.SettingsManage, ResourceRef.Global, http.RequestAborted);
                return Results.Ok(new
                {
                    version = typeof(GatewayHost).Assembly.GetName().Version?.ToString(),
                    milestone = "M1",
                    database = "SQLite",
                    connectedAgents = agents.ConnectedHosts.Count,
                    telemetry = "disabled (no telemetry is implemented)",
                    dataDirectory = admin ? paths.Root : null,
                    internetRequired = "No. Only Entra ID sign-in (Milestone 2) needs connectivity to Microsoft endpoints.",
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
            app.MapFallbackToFile("index.html").AllowAnonymous();
        }

        private static X509Certificate2? LoadUiCertificate(GatewayOptions options)
        {
            if (string.IsNullOrWhiteSpace(options.UiCertificateThumbprint))
            {
                return null;
            }
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);
            var found = store.Certificates.Find(X509FindType.FindByThumbprint, options.UiCertificateThumbprint.Replace(" ", ""), validOnly: false);
            return found.Count > 0 ? found[0] : throw new InvalidOperationException($"UI certificate {options.UiCertificateThumbprint} not found in LocalMachine\\My.");
        }
    }
}
