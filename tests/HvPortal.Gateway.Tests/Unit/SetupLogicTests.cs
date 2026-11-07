using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HvPortal.Setup;

namespace HvPortal.Gateway.Tests.Unit;

/// <summary>
/// Graphical setup (HvPortal.Gateway.Setup.exe / HvPortal.Agent.Setup.exe): validation of the wizard fields, the
/// parameters handed to the installer scripts and the wrapper script that receives them (run with Windows PowerShell).
/// </summary>
public sealed class SetupLogicTests
{
    [Theory]
    [InlineData("gw01.contoso.local", true)]
    [InlineData("HVMGMT01", true)]
    [InlineData("10.0.10.5", true)]
    [InlineData("999.0.0.1", false)]
    [InlineData("gw01;calc", false)]
    [InlineData("gw 01", false)]
    [InlineData("-gw", false)]
    [InlineData("", false)]
    public void Host_names_are_dns_names_or_ipv4_addresses(string value, bool valid) => Assert.Equal(valid, SetupRules.IsHostName(value));

    [Fact]
    public void Field_formats()
    {
        Assert.Equal("https://gw01.contoso.local:8444", SetupRules.NormalizeGatewayUrl(" https://GW01.contoso.local:8444/ "));
        Assert.Equal("https://10.0.10.5:8444", SetupRules.NormalizeGatewayUrl("https://10.0.10.5"));
        foreach (var bad in new[] { "http://gw01:8444", "https://gw01:8444/path", "https://user@gw01:8444", "https://gw01:0", "gw01:8444", "https://gw01:8444 -x" })
        {
            Assert.Null(SetupRules.NormalizeGatewayUrl(bad));
        }
        var fingerprint = new string('a', 64);
        Assert.Equal(fingerprint.ToUpperInvariant(), SetupRules.NormalizeFingerprint(string.Join(":", Enumerable.Range(0, 32).Select(_ => "aa"))));
        Assert.Null(SetupRules.NormalizeFingerprint(fingerprint + "0"));
        Assert.Null(SetupRules.NormalizeFingerprint("zz" + fingerprint[2..]));
        Assert.Equal("989A5C82203AB19B6DB29FC447E8CB5EBEEB60D8", SetupRules.NormalizeThumbprint("98 9a 5c 82 20 3a b1 9b 6d b2 9f c4 47 e8 cb 5e be eb 60 d8"));
        Assert.Null(SetupRules.NormalizeThumbprint("989A5C82"));
        foreach (var ok in new[] { "LocalSubnet", "any", "10.0.10.5", "10.0.10.0/24", "10.0.10.1-10.0.10.50" })
        {
            Assert.True(SetupRules.IsFirewallAddress(ok), ok);
        }
        foreach (var bad in new[] { "", "10.0.10.0/33", "10.0.10.0/-1", "everyone", "10.0.10.1;calc", "10.0.10.1-x" })
        {
            Assert.False(SetupRules.IsFirewallAddress(bad), bad);
        }
        Assert.True(SetupRules.IsServiceAccount("svc-hvportal"));
        Assert.False(SetupRules.IsServiceAccount("svc hv"));
        Assert.False(SetupRules.IsServiceAccount("Administrator;x"));
        Assert.False(SetupRules.IsServiceAccount(new string('a', 21)));
        Assert.Equal(new[] { "gw01", "10.0.10.5", "gw01.contoso.local" }, SetupRules.SplitList("gw01, 10.0.10.5;\r\n gw01.contoso.local  GW01"));
    }

    [Fact]
    public void Gateway_options_are_validated_and_become_script_parameters()
    {
        var o = new GatewaySetupOptions { PublicNames = ["hvportal.contoso.com", "10.0.10.5"], UiPort = 443, UiCertificateThumbprint = "98 9a 5c 82 20 3a b1 9b 6d b2 9f c4 47 e8 cb 5e be eb 60 d8" };
        Assert.Empty(o.Validate());
        var p = o.ToParameters(@"C:\Program Files\HvPortal\Setup\x\gateway");
        Assert.Equal(443, p["UiPort"]);
        Assert.Equal(8444, p["AgentPort"]);
        Assert.Equal(new[] { "hvportal.contoso.com", "10.0.10.5" }, (string[])p["PublicNames"]);
        Assert.Equal("989A5C82203AB19B6DB29FC447E8CB5EBEEB60D8", p["UiCertificateThumbprint"]);
        // No firewall rule unless asked: the script creates rules only with -OpenFirewall.
        Assert.False(p.ContainsKey("OpenFirewall"));
        Assert.False(p.ContainsKey("UiRemoteAddress"));

        o.OpenFirewall = true;
        o.AgentRemoteAddress = ["172.40.1.169"];
        p = o.ToParameters("src");
        Assert.Equal(true, p["OpenFirewall"]);
        Assert.Equal(new[] { "172.40.1.169" }, (string[])p["AgentRemoteAddress"]);

        Assert.Contains("err.names", new GatewaySetupOptions().Validate());
        Assert.Contains("err.names", new GatewaySetupOptions { PublicNames = ["localhost"] }.Validate());
        Assert.Contains("err.ports", new GatewaySetupOptions { PublicNames = ["gw"], UiPort = 8444 }.Validate());
        Assert.Contains("err.ports", new GatewaySetupOptions { PublicNames = ["gw"], UiPort = 70000 }.Validate());
        Assert.Contains("err.bind", new GatewaySetupOptions { PublicNames = ["gw"], BindAddress = "::" }.Validate());
        Assert.Contains("err.thumbprint", new GatewaySetupOptions { PublicNames = ["gw"], UiCertificateThumbprint = "abc" }.Validate());
        Assert.Contains("err.firewall", new GatewaySetupOptions { PublicNames = ["gw"], OpenFirewall = true, UiRemoteAddress = ["everyone"] }.Validate());
    }

    [Fact]
    public void Upgrade_reads_the_current_settings_from_the_service_environment()
    {
        var o = GatewaySetupOptions.FromServiceEnvironment(
        [
            "ASPNETCORE_ENVIRONMENT=Production", "Gateway__AgentPort=9444", "Gateway__BindAddress=10.0.10.5", "Gateway__PublicNames__1=10.0.10.5",
            "Gateway__PublicNames__0=gw01.contoso.local", "Gateway__UiCertificateThumbprint=989A5C82203AB19B6DB29FC447E8CB5EBEEB60D8", "Gateway__UiPort=443",
            @"HVP_DATA_DIR=C:\ProgramData\HvPortal\Gateway", "Gateway__PublicNames__2=bad name", "garbage",
        ]);
        Assert.Equal((443, 9444, "10.0.10.5"), (o.UiPort, o.AgentPort, o.BindAddress));
        Assert.Equal(new[] { "gw01.contoso.local", "10.0.10.5" }, o.PublicNames);
        Assert.Equal("989A5C82203AB19B6DB29FC447E8CB5EBEEB60D8", o.UiCertificateThumbprint);
        // The firewall is left alone on upgrade unless the user asks.
        Assert.False(o.OpenFirewall);
        Assert.Empty(GatewaySetupOptions.FromServiceEnvironment(null).PublicNames);
    }

    [Fact]
    public void Agent_options_never_put_the_token_among_the_parameters()
    {
        var token = "AbCdEfGhIjKlMnOpQrStUvWxYz012345";
        var o = new AgentSetupOptions { Gateway = "https://gw01.contoso.local:8444", CaSha256 = new string('b', 64), Token = token, Management = true, ServiceAccount = "svc-hvportal" };
        Assert.Empty(o.Validate());
        var p = o.ToParameters("src");
        Assert.Equal("https://gw01.contoso.local:8444", p["Gateway"]);
        Assert.Equal(new string('B', 64), p["CaSha256"]);
        Assert.Equal(true, p["Management"]);
        Assert.Equal("svc-hvportal", p["ServiceAccount"]);
        Assert.DoesNotContain(token, SetupJson.Serialize(p));
        Assert.False(p.ContainsKey("EnrollmentToken"));

        // Upgrade that keeps the registration: no gateway, no fingerprint, nothing to validate there.
        var keep = new AgentSetupOptions { Enroll = false };
        Assert.Empty(keep.Validate());
        Assert.Equal(new[] { "Source" }, keep.ToParameters("src").Keys);

        var bad = new AgentSetupOptions { Gateway = "http://gw01", CaSha256 = "12", Token = "x y", ServiceAccount = "bad name" };
        Assert.Equal(new[] { "err.gateway", "err.fingerprint", "err.token", "err.account" }, bad.Validate());
    }

    [Fact]
    public void Storage_folders_are_local_folders_outside_system_folders_and_reach_the_script_as_an_array()
    {
        Assert.Equal([@"D:\VMs", @"E:\Virtual Machines", @"F:\"], SetupRules.SplitFolders("D:\\VMs\\\r\nE:\\Virtual Machines;F:\\\n\nd:\\vms"));
        foreach (var ok in new[] { @"D:\VMs", @"E:\", @"D:\Hyper-V\ISO" })
        {
            Assert.True(SetupRules.IsStorageFolder(ok), ok);
        }
        foreach (var bad in new[] { @"C:\Windows", @"C:\Program Files\HvPortal", @"D:\$Recycle.Bin", @"\\nas\share", @"D:\a\..\Windows", "D:", "VMs", @"D:\a*b" })
        {
            Assert.False(SetupRules.IsStorageFolder(bad), bad);
        }
        var o = new AgentSetupOptions { Enroll = false, StorageFolders = [@"D:\VMs", @"C:\Users\x"] };
        Assert.Equal(["err.storage"], o.Validate());
        o.StorageFolders = [@"D:\VMs", @"D:\ISO"];
        Assert.Empty(o.Validate());
        Assert.Equal(new[] { @"D:\VMs", @"D:\ISO" }, o.ToParameters("src")["StorageFolders"]);
        // An empty list removes the restriction; null keeps what the host has (nothing sent).
        o.StorageFolders = [];
        Assert.Equal(new[] { "" }, o.ToParameters("src")["StorageFolders"]);
        o.StorageFolders = null;
        Assert.False(o.ToParameters("src").ContainsKey("StorageFolders"));
    }

    [Fact]
    public void Portal_data_is_read_from_the_setup_string_and_from_the_powershell_command()
    {
        var ca = new string('c', 64).ToUpperInvariant();
        var token = "tok_ABCDEFGHIJKLMNOPQRSTUVWXYZ-123";
        Assert.True(PortalData.TryParse(PortalData.Format("https://gw01.contoso.local:8444", ca, token, management: true), out var a));
        Assert.Equal(("https://gw01.contoso.local:8444", ca, token, true), (a.Gateway, a.CaSha256, a.Token, a.Management));

        var command = "cd C:\\HvPortal\\agent-package\\installer\r\n$env:HVP_ENROLLMENT_TOKEN = '" + token + "'\r\n.\\Install-Agent.ps1 -Source ..\\agent -Gateway https://10.0.10.5:8444 -CaSha256 " + ca;
        Assert.True(PortalData.TryParse(command, out var b));
        Assert.Equal(("https://10.0.10.5:8444", ca, token, false), (b.Gateway, b.CaSha256, b.Token, b.Management));
        Assert.True(PortalData.TryParse(command + " -Management", out var c) && c.Management);

        foreach (var bad in new[] { null, "", "hello", "HVPORTAL-SETUP gateway=http://gw ca=" + ca + " token=" + token, "HVPORTAL-SETUP gateway=https://gw:8444 ca=12 token=" + token,
                     "HVPORTAL-SETUP gateway=https://gw:8444 ca=" + ca + " token=a;b", new string('x', 9000) })
        {
            Assert.False(PortalData.TryParse(bad, out _));
        }
    }

    [Fact]
    public void Parameters_file_is_valid_json_whatever_the_values()
    {
        var hostile = "C:\\Temp\\a'b \"q\" $(calc) `whoami`; & exit\r\n\tèç\u0001";
        var json = SetupJson.Serialize(new Dictionary<string, object> { ["Source"] = hostile, ["UiPort"] = 443, ["OpenFirewall"] = true, ["PublicNames"] = new[] { "a", "b\"c" } });
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(hostile, doc.RootElement.GetProperty("Source").GetString());
        Assert.Equal(443, doc.RootElement.GetProperty("UiPort").GetInt32());
        Assert.True(doc.RootElement.GetProperty("OpenFirewall").GetBoolean());
        Assert.Equal("b\"c", doc.RootElement.GetProperty("PublicNames")[1].GetString());
        Assert.True(json.All(ch => ch is >= ' ' and <= '~'));
        Assert.Throws<ArgumentException>(() => SetupJson.Serialize(new Dictionary<string, object> { ["X"] = 1.5 }));
    }

    /// <summary>
    /// The real wrapper (installer\Invoke-Setup.ps1) with a stand-in installer script that prints what it received:
    /// values arrive typed (arrays, switch, int) and literal (nothing typed in the wizard is evaluated).
    /// </summary>
    [Fact]
    public async Task Wrapper_script_passes_typed_literal_values_and_refuses_unknown_parameters()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "setup-wrapper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Setup", "Invoke-Setup.ps1"), Path.Combine(dir, "Invoke-Setup.ps1"));
            await File.WriteAllTextAsync(Path.Combine(dir, "Install-Gateway.ps1"), """
                param([Parameter(Mandatory)][string]$Source, [int]$UiPort = 8443, [int]$AgentPort = 8444, [string]$BindAddress, [string[]]$PublicNames = @(),
                      [string]$UiCertificateThumbprint, [switch]$OpenFirewall, [string[]]$UiRemoteAddress = @('LocalSubnet'), [string[]]$AgentRemoteAddress = @('LocalSubnet'))
                [pscustomobject]@{ source = $Source; uiPort = $UiPort; portType = $UiPort.GetType().Name; names = $PublicNames; open = [bool]$OpenFirewall
                                   ui = $UiRemoteAddress; token = $env:HVP_ENROLLMENT_TOKEN } | ConvertTo-Json -Compress
                """);
            var hostile = "C:\\Temp\\a'b $(Set-Content pwned.txt x) \"q\"; & whoami";
            var options = new GatewaySetupOptions { PublicNames = ["gw01.contoso.local", "10.0.10.5"], UiPort = 443, OpenFirewall = true, UiRemoteAddress = ["10.0.10.0/24", "LocalSubnet"] };
            var parametersFile = Path.Combine(dir, "setup-parameters.json");
            await File.WriteAllTextAsync(parametersFile, SetupJson.Serialize(options.ToParameters(hostile)), new UTF8Encoding(false));

            var (code, output) = await RunAsync(dir, parametersFile, "secret-token");
            Assert.True(code == 0, output);
            using var doc = JsonDocument.Parse(output.Trim().Split('\n').Last());
            var r = doc.RootElement;
            Assert.Equal(hostile, r.GetProperty("source").GetString());
            Assert.Equal(443, r.GetProperty("uiPort").GetInt32());
            Assert.Equal("Int32", r.GetProperty("portType").GetString());
            Assert.Equal(new[] { "gw01.contoso.local", "10.0.10.5" }, r.GetProperty("names").EnumerateArray().Select(x => x.GetString()).ToArray());
            Assert.True(r.GetProperty("open").GetBoolean());
            Assert.Equal(2, r.GetProperty("ui").GetArrayLength());
            Assert.Equal("secret-token", r.GetProperty("token").GetString());
            Assert.False(File.Exists(Path.Combine(dir, "pwned.txt")));

            // A parameter the installer script does not declare is refused before the script runs.
            await File.WriteAllTextAsync(parametersFile, """{"Source":"x","ScriptBlock":"calc"}""");
            var (refused, message) = await RunAsync(dir, parametersFile, null);
            Assert.NotEqual(0, refused);
            Assert.Contains("Unknown parameter", message);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// The wizard and the installer scripts must agree: every parameter the setup can send is declared by the real
    /// script with the expected type. Only the declarations are read (Get-Command): nothing is installed.
    /// </summary>
    [Theory]
    [InlineData("Gateway")]
    [InlineData("Agent")]
    public async Task Every_setup_parameter_is_declared_by_the_installer_script(string component)
    {
        var script = Path.Combine(AppContext.BaseDirectory, "Setup", $"Install-{component}.ps1");
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
                     "(Get-Command -Name $env:HVP_TEST_SCRIPT).Parameters.Values | ForEach-Object { $_.Name + '=' + $_.ParameterType.Name }" })
        {
            psi.ArgumentList.Add(a);
        }
        psi.Environment["HVP_TEST_SCRIPT"] = script;
        using var process = Process.Start(psi)!;
        var text = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        var declared = text.Split((char)10, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(l => l.Split('=')).Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => p[1], StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Source", declared.Keys);

        var sent = component == "Gateway"
            ? new GatewaySetupOptions { PublicNames = ["gw"], UiCertificateThumbprint = new string('A', 40), OpenFirewall = true }.ToParameters("src")
            : new AgentSetupOptions
            {
                Gateway = "https://gw:8444", CaSha256 = new string('A', 64), Token = new string('t', 32), ForceReenroll = true, Management = true, ServiceAccount = "svc",
                StorageFolders = [@"D:\VMs"], DisableRemoteUpdate = true,
            }.ToParameters("src");
        foreach (var (name, value) in sent)
        {
            Assert.True(declared.TryGetValue(name, out var type), $"Install-{component}.ps1 has no parameter '{name}'.");
            var expected = value switch { bool => "SwitchParameter", int => "Int32", string[] => "String[]", _ => "String" };
            Assert.Equal(expected, type);
        }
        // The token is read by the agent script from the environment variable set by the setup.
        if (component == "Agent")
        {
            Assert.Contains("$env:HVP_ENROLLMENT_TOKEN", await File.ReadAllTextAsync(script));
        }
    }

    /// <summary>A refused update (port taken, wrong certificate) must not leave a working gateway stopped.</summary>
    [Fact]
    public async Task Gateway_installer_refuses_before_it_stops_the_running_service()
    {
        var script = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Setup", "Install-Gateway.ps1"));
        var stop = script.IndexOf("Stop-HvPortalService -ServiceName $service", StringComparison.Ordinal);
        Assert.True(stop > 0);
        foreach (var check in new[] { "UiPort and AgentPort must be different", "is already in use by", "not found in LocalMachine\\My", "has no private key" })
        {
            var at = script.IndexOf(check, StringComparison.Ordinal);
            Assert.True(at > 0 && at < stop, $"'{check}' must be checked before the service is stopped.");
        }
    }

    private static async Task<(int Code, string Output)> RunAsync(string dir, string parametersFile, string? token)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = dir,
        };
        foreach (var a in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(dir, "Invoke-Setup.ps1"), "-Component", "Gateway", "-ParametersFile", parametersFile })
        {
            psi.ArgumentList.Add(a);
        }
        if (token is not null)
        {
            psi.Environment["HVP_ENROLLMENT_TOKEN"] = token;
        }
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout + await stderr);
    }
}
