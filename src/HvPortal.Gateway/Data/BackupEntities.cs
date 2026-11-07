namespace HvPortal.Gateway.Data;

/// <summary>
/// A network of the portal (ADR-030): a virtual switch of a host plus an access VLAN. A VM adapter connected to the
/// network is connected to the switch and tagged with the VLAN; an adapter is "on" the network when its switch and
/// access VLAN are those of the network (derived from Hyper-V, nothing else is recorded).
/// </summary>
public sealed class PortalNetwork
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HostId { get; set; }
    public required string Name { get; set; }
    public required string SwitchName { get; set; }
    /// <summary>1-4094; null = untagged.</summary>
    public int? VlanId { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid CreatedByUserId { get; set; }
}

public enum BackupPointStatus
{
    /// <summary>The backup job is running.</summary>
    Creating,
    Ok,
    Failed,
    /// <summary>The outcome of the backup job is not known (result lost): verify the point before relying on it.</summary>
    Unverified,
    Deleting,
    Deleted,
}

public enum BackupRunStatus
{
    Running,
    Success,
    Warning,
    Failed,
}

/// <summary>
/// Where restore points are kept (ADR-031): a local folder of one host, or an SMB share usable by every host, with
/// optional credentials for shares outside the domain (AES-256-GCM with the key ring, bound to the repository id;
/// never returned by the API, sent only inside the commands of the jobs that use them).
/// </summary>
public sealed class BackupRepository
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public string? Description { get; set; }
    public required string Path { get; set; }
    /// <summary>Local folder repositories: the host that has the folder (only its VMs can be backed up there).</summary>
    public Guid? HostId { get; set; }
    public string? UserName { get; set; }
    public byte[]? PasswordEncrypted { get; set; }
    public DateTimeOffset? CredentialUpdatedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid CreatedByUserId { get; set; }
    /// <summary>Last readiness check (GetBackupReadiness) and the host that ran it.</summary>
    public DateTimeOffset? LastCheckedAt { get; set; }
    public Guid? LastCheckHostId { get; set; }
    public string? LastCheckJson { get; set; }
}

/// <summary>A backup job: what (VMs and groups), where (repository), when (schedule), how long (retention).</summary>
public sealed class BackupJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public string? Description { get; set; }
    public bool Enabled { get; set; } = true;
    public Guid RepositoryId { get; set; }
    /// <summary>daily | hourly | manual.</summary>
    public string ScheduleKind { get; set; } = "daily";
    /// <summary>Days of the week (bit 0 = Sunday ... bit 6 = Saturday), daily schedules.</summary>
    public int DaysMask { get; set; } = 0x7F;
    /// <summary>JSON array of "HH:mm" in <see cref="TimeZoneId"/>, daily schedules.</summary>
    public string TimesJson { get; set; } = "[\"22:00\"]";
    public int IntervalHours { get; set; } = 6;
    /// <summary>IANA or Windows time zone of the schedule (the browser's when the job was saved).</summary>
    public string TimeZoneId { get; set; } = "UTC";
    /// <summary>Restore points kept per VM (the newest ones), 1-366.</summary>
    public int RetentionPoints { get; set; } = 7;
    /// <summary>Restore points older than this are removed too (null = only the count applies).</summary>
    public int? RetentionDays { get; set; }
    public string CheckpointType { get; set; } = "Production";
    public bool VerifyAfterCopy { get; set; } = true;
    /// <summary>Every N days the newest restore point of each VM is restore-tested (0 = never).</summary>
    public int RestoreTestDays { get; set; }
    /// <summary>never | failure | warning | always.</summary>
    public string NotifyOn { get; set; } = "warning";
    public DateTimeOffset CreatedAt { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public DateTimeOffset? LastRestoreTestAt { get; set; }
}

/// <summary>A VM or a group (with its sub-groups, resolved at each run) protected by a backup job.</summary>
public sealed class BackupJobTarget
{
    public Guid BackupJobId { get; set; }
    /// <summary>vm | group.</summary>
    public required string Kind { get; set; }
    public Guid TargetId { get; set; }
}

/// <summary>One execution of a backup job, or an ad-hoc backup ("Back up now").</summary>
public sealed class BackupRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? BackupJobId { get; set; }
    /// <summary>schedule | manual | adhoc.</summary>
    public required string Trigger { get; set; }
    public Guid? RequestedByUserId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public BackupRunStatus Status { get; set; } = BackupRunStatus.Running;
    public int Total { get; set; }
    public int Succeeded { get; set; }
    public int Warnings { get; set; }
    public int Failed { get; set; }
    public string? Message { get; set; }
    public bool Notified { get; set; }
}

/// <summary>
/// A restore point: the copy of one VM taken at one moment, in &lt;repository&gt;\&lt;Folder&gt; (Folder = "&lt;vm key&gt;\&lt;id&gt;").
/// Kept by the portal also after the VM is deleted, so it can still be restored.
/// </summary>
public sealed class BackupPoint
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? RunId { get; set; }
    public Guid? BackupJobId { get; set; }
    public Guid RepositoryId { get; set; }
    /// <summary>Portal id of the VM (no foreign key: the point outlives the VM).</summary>
    public Guid VmId { get; set; }
    public Guid VmHyperVId { get; set; }
    public required string VmName { get; set; }
    public Guid HostId { get; set; }
    public required string Folder { get; set; }
    public BackupPointStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long SizeBytes { get; set; }
    public string? CheckpointType { get; set; }
    public string? CheckpointTypeUsed { get; set; }
    /// <summary>The manifest written by the agent (files, disks, configuration of the VM), as JSON.</summary>
    public string? ManifestJson { get; set; }
    public string? Error { get; set; }
    /// <summary>The job that creates (or deletes) the point.</summary>
    public Guid? JobId { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public bool? VerifyOk { get; set; }
    public string? VerifyDetail { get; set; }
    public DateTimeOffset? TestedAt { get; set; }
    public bool? TestPassed { get; set; }
    public string? TestDetail { get; set; }
    /// <summary>Kept whatever the retention of its job.</summary>
    public bool Keep { get; set; }
}

/// <summary>
/// Read-only API token for monitoring systems (ADR-032): "hvp_" + 40 random characters, shown once; only its SHA-256
/// is stored. Scopes are closed (backup.read). Used with "Authorization: Bearer" on /api/v1/public/* only.
/// </summary>
public sealed class ApiToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string TokenHash { get; set; }
    /// <summary>First characters of the token, to recognize it in the list.</summary>
    public required string Prefix { get; set; }
    public required string Scopes { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public string? LastUsedIp { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}
