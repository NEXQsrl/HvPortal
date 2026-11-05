namespace HvPortal.Gateway.Configuration;

public sealed class GatewayOptions
{
    public const string Section = "Gateway";

    /// <summary>Data directory (database, keys, PKI, bootstrap). Must be local storage. Default: %ProgramData%\HvPortal\Gateway.</summary>
    public string? DataDirectory { get; set; }

    public int UiPort { get; set; } = 8443;
    public int AgentPort { get; set; } = 8444;
    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>When true, /agent/* is served only on AgentPort and everything else only on UiPort.</summary>
    public bool EnforcePortSeparation { get; set; } = true;

    /// <summary>DNS names / IPs placed in the internal server certificate (agents connect using one of these).</summary>
    public List<string> PublicNames { get; set; } = [];

    /// <summary>Directory with downloadable packages (agent zip + manifest). Default: &lt;install dir&gt;\packages.</summary>
    public string? PackagesDirectory { get; set; }

    /// <summary>Optional: thumbprint of a certificate in LocalMachine\My to use for the browser UI instead of the internal one.</summary>
    public string? UiCertificateThumbprint { get; set; }

    public DatabaseOptions Database { get; set; } = new();
    public SecurityOptions Security { get; set; } = new();
    public JobOptions Jobs { get; set; } = new();
    public VmGroupOptions VmGroups { get; set; } = new();
}

public sealed class DatabaseOptions
{
    /// <summary>Sqlite (default). PostgreSql and SqlServer are Milestone 2.</summary>
    public string Provider { get; set; } = "Sqlite";
    public string? ConnectionString { get; set; }
}

public sealed class SecurityOptions
{
    public int Pbkdf2Iterations { get; set; } = 600_000;
    public int EnrollmentTokenMinutes { get; set; } = 15;
    public int AgentCertificateDays { get; set; } = 90;
    public int ReauthenticationWindowMinutes { get; set; } = 5;
    /// <summary>Optional local password blocklist (one entry per line: plain text or SHA-1 hex), e.g. an offline breach corpus.</summary>
    public string? BlocklistPath { get; set; }
}

public sealed class JobOptions
{
    /// <summary>Disable only in tests that drive the runner explicitly.</summary>
    public bool RunnerEnabled { get; set; } = true;
    public int MaxConcurrentJobs { get; set; } = 8;
    public int PollIntervalMilliseconds { get; set; } = 1000;
    public int HostUnreachableRetries { get; set; } = 3;
    public int ReconciliationGiveUpMinutes { get; set; } = 15;
}

public sealed class VmGroupOptions
{
    /// <summary>Mirror portal groups to native Hyper-V VM groups (agent 0.3.2+). Disabled only in tests that drive it explicitly.</summary>
    public bool SyncEnabled { get; set; } = true;
    /// <summary>Full re-apply (corrects changes made directly in Hyper-V).</summary>
    public int PeriodicMinutes { get; set; } = 30;
}

public sealed class GatewayPaths
{
    public GatewayPaths(string root)
    {
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }
    public string Keys => Path.Combine(Root, "keys");
    public string DataProtectionKeys => Path.Combine(Root, "keys", "dataprotection");
    public string Pki => Path.Combine(Root, "pki");
    public string Bootstrap => Path.Combine(Root, "bootstrap");
    public string Database => Path.Combine(Root, "db");
    public string SetupTokenFile => Path.Combine(Bootstrap, "setup-token.txt");
    public string RecoveryTokenFile => Path.Combine(Bootstrap, "recovery-token.txt");

    public static string DefaultRoot() =>
        Environment.GetEnvironmentVariable("HVP_DATA_DIR") is { Length: > 0 } d
            ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HvPortal", "Gateway");
}
