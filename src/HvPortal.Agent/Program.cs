using System.Text.Json;
using HvPortal.Agent.Channel;
using HvPortal.Agent.Commands;
using HvPortal.Agent.HyperV;
using HvPortal.Agent.Identity;
using HvPortal.Contracts.Agent;
using Microsoft.Extensions.Logging.Abstractions;

HvPortal.Shared.SecureFileSystem.ServiceName = "HvPortalAgent";
var verb = args.Length > 0 ? args[0].ToLowerInvariant() : "run";
var store = new AgentIdentityStore(AgentIdentityStore.DefaultDataDirectory());

switch (verb)
{
    case "enroll":
        return await EnrollAsync(store, args[1..]);

    case "status":
        var st = store.LoadState();
        if (st is null)
        {
            Console.WriteLine("Not enrolled.");
            return 1;
        }
        using (var c = store.LoadCertificate())
        {
            Console.WriteLine($"Gateway:      {st.GatewayUrl}");
            Console.WriteLine($"Role:         {st.Role}");
            Console.WriteLine(st.Role == AgentRole.Management ? $"Agent id:     {st.HostId}" : $"Host id:      {st.HostId}");
            Console.WriteLine($"Enrolled:     {st.EnrolledAtUtc:u}");
            Console.WriteLine($"Certificate:  {c?.Thumbprint} valid until {c?.NotAfter.ToUniversalTime():u}");
        }
        return 0;

    case "inventory":
        // Local, read-only diagnostic: prints what the agent would report. Useful to verify Hyper-V permissions.
        var host = new WmiHyperVHost(NullLogger<WmiHyperVHost>.Instance);
        var inv = await host.CollectInventoryAsync(CancellationToken.None);
        Console.WriteLine(JsonSerializer.Serialize(inv, new JsonSerializerOptions(AgentProtocol.Json) { WriteIndented = true }));
        return inv.Access == HvPortal.Contracts.HyperV.HyperVAccessStatus.Ok ? 0 : 2;

    case "unenroll":
        store.Delete();
        Console.WriteLine("Local identity removed. Revoke the host in the portal as well.");
        return 0;

    case "run":
        break;

    default:
        PrintUsage();
        return 64;
}

var builder = Host.CreateApplicationBuilder(args.Length > 0 && args[0] == "run" ? args[1..] : args);
builder.Services.AddWindowsService(o => o.ServiceName = "HvPortalAgent");
builder.Services.Configure<AgentRuntimeOptions>(builder.Configuration.GetSection("Agent"));
builder.Services.AddSingleton(store);
builder.Services.AddSingleton<IHyperVHost, WmiHyperVHost>();
builder.Services.AddSingleton<PowerShellOperationRunner>();
builder.Services.AddSingleton<ConsoleService>();
builder.Services.AddSingleton<CommandExecutor>();
builder.Services.AddSingleton<RemoteTargetRegistry>();
builder.Services.AddHostedService<GatewayChannelService>();
var app = builder.Build();
await app.RunAsync();
return 0;

static async Task<int> EnrollAsync(AgentIdentityStore store, string[] a)
{
    string? Get(string name)
    {
        var i = Array.IndexOf(a, name);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }

    var gateway = Get("--gateway");
    var token = Get("--token") ?? Environment.GetEnvironmentVariable("HVP_ENROLLMENT_TOKEN");
    var fp = Get("--ca-sha256");
    if (gateway is null || token is null || fp is null || !Uri.TryCreate(gateway, UriKind.Absolute, out var gw))
    {
        PrintUsage();
        return 64;
    }
    if (store.IsEnrolled && !a.Contains("--force"))
    {
        Console.Error.WriteLine("Agent already enrolled. Use --force to replace the existing identity.");
        return 1;
    }
    try
    {
        var state = await new EnrollmentClient(store).EnrollAsync(gw, token, fp, CancellationToken.None);
        Console.WriteLine(state.Role == AgentRole.Management
            ? $"Enrolled as management agent {state.HostId}. Assign remote hosts to it in the portal. Data directory: {store.DataDirectory}"
            : $"Enrolled as host {state.HostId}. Data directory: {store.DataDirectory}");
        Console.WriteLine("Start the service: Start-Service HvPortalAgent");
        return 0;
    }
    catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or ArgumentException or TaskCanceledException)
    {
        Console.Error.WriteLine("Enrollment failed: " + ex.Message);
        return 1;
    }
}

static void PrintUsage()
{
    Console.WriteLine("""
        HvPortal agent

          HvPortal.Agent.exe run                      Run the agent (default; used by the Windows service)
          HvPortal.Agent.exe enroll --gateway https://<gateway>:8444 --token <one-time token> --ca-sha256 <fingerprint> [--force]
          HvPortal.Agent.exe status                   Show enrollment state
          HvPortal.Agent.exe inventory                Print local Hyper-V inventory (read-only diagnostic)
          HvPortal.Agent.exe unenroll                 Remove the local identity

        The token can also be passed in HVP_ENROLLMENT_TOKEN to keep it out of the process command line.
        """);
}
