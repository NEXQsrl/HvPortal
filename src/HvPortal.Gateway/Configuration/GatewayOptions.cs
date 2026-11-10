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
    /// <summary>Linux/container alternative to the thumbprint: PFX file, or PEM certificate (+ UiCertificateKeyPath).</summary>
    public string? UiCertificatePath { get; set; }
    public string? UiCertificateKeyPath { get; set; }
    public string? UiCertificatePassword { get; set; }
    /// <summary>File containing the PFX password (Docker secret).</summary>
    public string? UiCertificatePasswordFile { get; set; }

    public DatabaseOptions Database { get; set; } = new();
    public SecurityOptions Security { get; set; } = new();
    public JobOptions Jobs { get; set; } = new();
    public VmGroupOptions VmGroups { get; set; } = new();
    public BackupOptions Backup { get; set; } = new();
    public MetricsOptions Metrics { get; set; } = new();
    public ClusterOptions Clusters { get; set; } = new();
    public UpdateOptions Updates { get; set; } = new();
}

/// <summary>Resource use history of VMs (ADR-039).</summary>
public sealed class UpdateOptions
{
    /// <summary>GitHub repository whose releases are offered as updates of the portal (owner/name).</summary>
    public string Repository { get; set; } = "NEXQsrl/HvPortal";
    /// <summary>Runs the automatic checks when an administrator turned them on (Settings > System). Disabled only in tests.</summary>
    public bool CheckerEnabled { get; set; } = true;
}

public sealed class ClusterOptions
{
    /// <summary>Reads every failover cluster periodically (ADR-041). Disabled only in tests that drive it explicitly.</summary>
    public bool MonitorEnabled { get; set; } = true;
    /// <summary>Seconds between two reads of each cluster (30-3600).</summary>
    public int MonitorIntervalSeconds { get; set; } = 120;
}

public sealed class MetricsOptions
{
    /// <summary>Collects samples of the VMs with resource metering on. Disabled only in tests that drive it explicitly.</summary>
    public bool CollectorEnabled { get; set; } = true;
    /// <summary>One stored sample per VM every this many seconds (Measure-VM on the host; CPU and memory from the inventory in between).</summary>
    public int SampleIntervalSeconds { get; set; } = 300;
    /// <summary>Samples older than this are deleted.</summary>
    public int RetentionDays { get; set; } = 10;
    /// <summary>Reset-VMResourceMetering after this many days, so that the totals of Hyper-V cover at most this period.</summary>
    public int ResetDays { get; set; } = 10;
}

public sealed class BackupOptions
{
    /// <summary>Starts scheduled backup runs, closes runs, applies retention, sends notifications. Disabled only in tests that drive it explicitly.</summary>
    public bool SchedulerEnabled { get; set; } = true;
    public int IntervalSeconds { get; set; } = 20;
}

public sealed class DatabaseOptions
{
    /// <summary>Sqlite (default). PostgreSql and SqlServer are Milestone 2.</summary>
    public string Provider { get; set; } = "Sqlite";
    public string? ConnectionString { get; set; }
    /// <summary>Verified local copies of the SQLite database in dbackups (protection against corruption, ADR-023).</summary>
    public bool AutoBackupEnabled { get; set; } = true;
    public int AutoBackupRetentionDays { get; set; } = 7;
    public int AutoBackupIntervalHours { get; set; } = 24;
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
    /// <summary>Jobs that may run for hours (backups, restores, VM moves, agent updates): separate budget.</summary>
    public int MaxConcurrentLongJobs { get; set; } = 4;
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
    /// <summary>
    /// Import the VM groups that already exist on a host the first time it is mirrored (only those that can be taken
    /// over without changing Hyper-V). Off = existing groups are imported only on request from the host page.
    /// </summary>
    public bool AutoImport { get; set; } = true;
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
