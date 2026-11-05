using HvPortal.Contracts.HyperV;

namespace HvPortal.Gateway.Data;

// ---------------------------------------------------------------- identity

public sealed class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string UserName { get; set; }
    public required string NormalizedUserName { get; set; }
    public string? DisplayName { get; set; }
    /// <summary>"local" in M1. "entra" accounts (M2) have no password hash and are identified by tenant id + object id.</summary>
    public string AuthSource { get; set; } = "local";
    public string? ExternalTenantId { get; set; }
    public string? ExternalObjectId { get; set; }
    public string? PasswordHash { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool MustChangePassword { get; set; }
    /// <summary>Local users: MFA must be enrolled at first sign-in regardless of the global policy (set when created).</summary>
    public bool MfaEnrollmentRequired { get; set; }
    /// <summary>Emergency access account: exempt from external IdP policy, every use raises an alert-level audit event.</summary>
    public bool IsBreakGlass { get; set; }
    /// <summary>Changes on password reset, MFA change, disable: invalidates every existing session.</summary>
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PasswordChangedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }

    public byte[]? TotpSecretEncrypted { get; set; }
    public bool TotpEnabled { get; set; }
    public DateTimeOffset? TotpEnabledAt { get; set; }
    /// <summary>Highest accepted TOTP time step; codes at or below it are rejected (replay protection).</summary>
    public long TotpLastUsedStep { get; set; }
    public byte[]? PendingTotpSecretEncrypted { get; set; }
    public DateTimeOffset? PendingTotpCreatedAt { get; set; }
    public long Version { get; set; }
}

public sealed class RecoveryCode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public required string CodeHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}

public sealed class UserSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public required string SecurityStamp { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedReason { get; set; }
    /// <summary>e.g. "pwd", "pwd+totp", "pwd+recovery", "setup".</summary>
    public required string AuthMethods { get; set; }
    public bool MfaSatisfied { get; set; }
    public DateTimeOffset? ReauthenticatedAt { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
}

public sealed class MfaChallenge
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string TokenHash { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public string? IpAddress { get; set; }
}

public sealed class LoginThrottle
{
    public required string Key { get; set; }
    public int Failures { get; set; }
    public DateTimeOffset LastFailureAt { get; set; }
    public DateTimeOffset? BlockedUntil { get; set; }
}

public sealed class OneTimeToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>"setup" or "account-recovery".</summary>
    public required string Purpose { get; set; }
    public required string TokenHash { get; set; }
    public Guid? UserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
}

// ---------------------------------------------------------------- authorization

public enum ScopeType
{
    Global,
    Host,
    Cluster,
    Group,
    Vm,
    Network,
    BackupRepository,
    Template,
}

public enum AssignmentEffect
{
    Allow,
    Deny,
}

public sealed class Role
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public string? Description { get; set; }
    public bool IsBuiltIn { get; set; }
    /// <summary>Stable key for built-in roles (e.g. "platform-admin").</summary>
    public string? BuiltInKey { get; set; }
    public List<RolePermission> Permissions { get; set; } = [];
}

public sealed class RolePermission
{
    public Guid RoleId { get; set; }
    public required string Permission { get; set; }
}

public sealed class RoleAssignment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>"user" in M1; "entra-group" / "entra-app-role" in M2.</summary>
    public string PrincipalType { get; set; } = "user";
    public required string PrincipalId { get; set; }
    public Guid RoleId { get; set; }
    public Role? Role { get; set; }
    public ScopeType ScopeType { get; set; }
    /// <summary>Empty for Global.</summary>
    public string ScopeId { get; set; } = "";
    public AssignmentEffect Effect { get; set; } = AssignmentEffect.Allow;
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedByUserId { get; set; }
}

/// <summary>Portal logical group (cross-host). Not related to native Hyper-V VM groups.</summary>
public sealed class LogicalGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Parent folder (null = root). Role assignments on a group apply to its whole subtree.</summary>
    public Guid? ParentId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class LogicalGroupMember
{
    public Guid GroupId { get; set; }
    public Guid VmId { get; set; }
}

/// <summary>
/// A native Hyper-V VM group (Get-VMGroup) owned by the portal on one host: created by the mirroring of a portal group or
/// imported from the host. Only linked native groups are ever modified or removed by the portal. No foreign key on
/// GroupId: when the portal group is deleted the link survives until the sync has removed the native group.
/// Role "vms" = VMCollectionType with the group's own VMs; "mgmt" = ManagementCollectionType with the VM collections below.
/// </summary>
public sealed class HostVmGroupLink
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HostId { get; set; }
    public Guid GroupId { get; set; }
    public required string Role { get; set; }
    public Guid NativeId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

// ---------------------------------------------------------------- hosts & inventory

public enum HostStatus
{
    Pending,
    Active,
    Revoked,
}

public sealed class ManagedHost
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string DisplayName { get; set; }
    /// <summary>Portal-only free text (not written to Hyper-V).</summary>
    public string? Description { get; set; }
    public string? MachineName { get; set; }
    public string? DnsHostName { get; set; }
    public HostStatus Status { get; set; } = HostStatus.Pending;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? EnrolledAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public DateTimeOffset? LastInventoryAt { get; set; }
    public string? AgentVersion { get; set; }
    public string? HostFactsJson { get; set; }
    public HyperVAccessStatus? Access { get; set; }
    public string? AccessMessage { get; set; }
    public bool InventoryPartial { get; set; }
    public string? InventoryWarningsJson { get; set; }
    /// <summary>JSON array of folders on the host that may be used as ISO library (mount / list).</summary>
    public string? IsoLibraryPathsJson { get; set; }
    /// <summary>Mirroring of portal groups to native Hyper-V VM groups (agent 0.3.2+): last attempt, errors, plan hash.</summary>
    public DateTimeOffset? VmGroupsSyncedAt { get; set; }
    public string? VmGroupsSyncError { get; set; }
    public string? VmGroupsPlanHash { get; set; }
}

public sealed class AgentCertificate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HostId { get; set; }
    public required string ThumbprintSha256 { get; set; }
    public required string SerialNumber { get; set; }
    public DateTimeOffset NotBefore { get; set; }
    public DateTimeOffset NotAfter { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedReason { get; set; }
}

public sealed class EnrollmentToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HostId { get; set; }
    public required string TokenHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public string? ConsumedFromIp { get; set; }
    public Guid CreatedByUserId { get; set; }
}

public sealed class VirtualMachine
{
    /// <summary>Portal id. Hyper-V ids are not globally unique (Import-VM -Copy can keep the same id on another host).</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HostId { get; set; }
    public Guid HyperVId { get; set; }
    public required string Name { get; set; }
    /// <summary>Portal-only free text (not written to Hyper-V).</summary>
    public string? Description { get; set; }
    public int? CheckpointCount { get; set; }
    /// <summary>Last on-demand details (disks, adapters, guest IPs) as JSON, with collection time.</summary>
    public string? DetailsJson { get; set; }
    public DateTimeOffset? DetailsCollectedAt { get; set; }
    public VmPowerState PowerState { get; set; }
    public int EnabledStateRaw { get; set; }
    public bool IsCritical { get; set; }
    public int? Generation { get; set; }
    public string? ConfigurationVersion { get; set; }
    public int? ProcessorCount { get; set; }
    public long? MemoryStartupMb { get; set; }
    public long? MemoryAssignedMb { get; set; }
    public bool? DynamicMemoryEnabled { get; set; }
    public long? UptimeMs { get; set; }
    public int? Heartbeat { get; set; }
    public int? HealthState { get; set; }
    public int? ProcessorLoadPercent { get; set; }
    public string? GuestOperatingSystem { get; set; }
    public bool? ShutdownServiceAvailable { get; set; }
    public string OperationalStatusJson { get; set; } = "[]";
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset? StateChangedAt { get; set; }
    public bool IsMissing { get; set; }
    public DateTimeOffset? MissingSince { get; set; }
}

/// <summary>Operation history (not security audit): discoveries, removals, out-of-portal changes.</summary>
public sealed class InventoryEvent
{
    public long Id { get; set; }
    public Guid HostId { get; set; }
    public Guid? VmId { get; set; }
    public required string Kind { get; set; }
    public string? Details { get; set; }
    public DateTimeOffset At { get; set; }
}

// ---------------------------------------------------------------- jobs

public enum JobStatus
{
    Queued,
    Running,
    /// <summary>Process restarted or channel lost after dispatch: outcome being verified from host state.</summary>
    Reconciling,
    Succeeded,
    /// <summary>Host accepted the request but the end state was not observed.</summary>
    Unverified,
    Failed,
    Cancelled,
    /// <summary>Outcome could not be determined. Operator must check the host.</summary>
    Unknown,
}

public sealed class Job
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Type { get; set; }
    public required string Operation { get; set; }
    public ScopeType TargetType { get; set; }
    public Guid TargetId { get; set; }
    public string? TargetName { get; set; }
    public Guid? HostId { get; set; }
    public Guid RequestedByUserId { get; set; }
    public required string RequiredPermission { get; set; }
    public JobStatus Status { get; set; }
    public int Progress { get; set; }
    public string? ProgressMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? NotBefore { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? DispatchedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? HeartbeatAt { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public int TimeoutSeconds { get; set; }
    public int Attempt { get; set; }
    public string? IdempotencyKey { get; set; }
    public Guid CorrelationId { get; set; } = Guid.NewGuid();
    public bool CancelRequested { get; set; }
    public string? ResultMessage { get; set; }
    public string? ErrorCode { get; set; }
    public long? HyperVCode { get; set; }
    public DateTimeOffset? ReconcileStartedAt { get; set; }
    /// <summary>Typed arguments of the operation (JSON), validated when the job is created.</summary>
    public string? ParametersJson { get; set; }
    /// <summary>Structured result returned by the host (JSON), e.g. the id of a created VM.</summary>
    public string? ResultJson { get; set; }
    public long Version { get; set; }
}

public sealed class JobEvent
{
    public long Id { get; set; }
    public Guid JobId { get; set; }
    public DateTimeOffset At { get; set; }
    public required string Kind { get; set; }
    public string? Message { get; set; }
}

public sealed class ResourceLock
{
    public required string ResourceKey { get; set; }
    public Guid JobId { get; set; }
    public DateTimeOffset AcquiredAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

// ---------------------------------------------------------------- audit & settings

public enum AuditOutcome
{
    Success,
    Failure,
    Denied,
}

public sealed class AuditEntry
{
    public long Id { get; set; }
    public DateTimeOffset TimestampUtc { get; set; }
    public Guid? ActorUserId { get; set; }
    public string? ActorName { get; set; }
    /// <summary>How the actor authenticated: "local:pwd+totp", "system", "agent-mtls", "anonymous"...</summary>
    public required string AuthMethod { get; set; }
    public string? SourceIp { get; set; }
    public required string Action { get; set; }
    public string? ResourceType { get; set; }
    public string? ResourceId { get; set; }
    public AuditOutcome Outcome { get; set; }
    public Guid CorrelationId { get; set; }
    public string? DetailsJson { get; set; }
    public required string KeyId { get; set; }
    public required string PreviousHash { get; set; }
    public required string Hash { get; set; }
}

public sealed class Setting
{
    public required string Key { get; set; }
    public required string Value { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
