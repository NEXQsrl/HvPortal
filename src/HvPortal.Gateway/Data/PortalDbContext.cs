using HvPortal.Contracts.HyperV;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace HvPortal.Gateway.Data;

public sealed class PortalDbContext(DbContextOptions<PortalDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<RecoveryCode> RecoveryCodes => Set<RecoveryCode>();
    public DbSet<UserSession> Sessions => Set<UserSession>();
    public DbSet<MfaChallenge> MfaChallenges => Set<MfaChallenge>();
    public DbSet<LoginThrottle> LoginThrottles => Set<LoginThrottle>();
    public DbSet<OneTimeToken> OneTimeTokens => Set<OneTimeToken>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<RoleAssignment> RoleAssignments => Set<RoleAssignment>();
    public DbSet<LogicalGroup> LogicalGroups => Set<LogicalGroup>();
    public DbSet<LogicalGroupMember> LogicalGroupMembers => Set<LogicalGroupMember>();
    public DbSet<GroupNetwork> GroupNetworks => Set<GroupNetwork>();
    public DbSet<VmTemplate> VmTemplates => Set<VmTemplate>();
    public DbSet<HostVmGroupLink> HostVmGroupLinks => Set<HostVmGroupLink>();
    public DbSet<ManagedHost> Hosts => Set<ManagedHost>();
    public DbSet<ManagementAgent> ManagementAgents => Set<ManagementAgent>();
    public DbSet<AgentCertificate> AgentCertificates => Set<AgentCertificate>();
    public DbSet<EnrollmentToken> EnrollmentTokens => Set<EnrollmentToken>();
    public DbSet<VirtualMachine> VirtualMachines => Set<VirtualMachine>();
    public DbSet<InventoryEvent> InventoryEvents => Set<InventoryEvent>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<JobEvent> JobEvents => Set<JobEvent>();
    public DbSet<ResourceLock> ResourceLocks => Set<ResourceLock>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<PortalNetwork> PortalNetworks => Set<PortalNetwork>();
    public DbSet<BackupRepository> BackupRepositories => Set<BackupRepository>();
    public DbSet<BackupJob> BackupJobs => Set<BackupJob>();
    public DbSet<BackupJobTarget> BackupJobTargets => Set<BackupJobTarget>();
    public DbSet<BackupRun> BackupRuns => Set<BackupRun>();
    public DbSet<BackupPoint> BackupPoints => Set<BackupPoint>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();

    /// <summary>Optimistic concurrency: entities with a long "Version" property get it incremented on update.</summary>
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State == EntityState.Modified && entry.Metadata.FindProperty("Version") is { ClrType: var t } && t == typeof(long))
            {
                var p = entry.Property("Version");
                p.CurrentValue = (long)p.CurrentValue! + 1;
            }
        }
        return base.SaveChangesAsync(cancellationToken);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        // SQLite has no native DateTimeOffset: store as sortable binary (all values are UTC).
        // PostgreSQL / SQL Server (Milestone 2) keep native types through their own migrations assembly.
        if (Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite")
        {
            b.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
            b.Properties<DateTimeOffset?>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        }
        b.Properties<VmPowerState>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<HyperVAccessStatus>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<HostStatus>().HaveConversion<string>().HaveMaxLength(16);
        b.Properties<HostConnectionMode>().HaveConversion<string>().HaveMaxLength(16);
        b.Properties<JobStatus>().HaveConversion<string>().HaveMaxLength(16);
        b.Properties<ScopeType>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<AssignmentEffect>().HaveConversion<string>().HaveMaxLength(8);
        b.Properties<AuditOutcome>().HaveConversion<string>().HaveMaxLength(8);
        b.Properties<string>().HaveMaxLength(1024);
    }

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<User>(e =>
        {
            e.HasIndex(x => x.NormalizedUserName).IsUnique();
            e.HasIndex(x => new { x.ExternalTenantId, x.ExternalObjectId }).IsUnique().HasFilter("ExternalObjectId IS NOT NULL");
            e.Property(x => x.UserName).HasMaxLength(128);
            e.Property(x => x.NormalizedUserName).HasMaxLength(128);
            e.Property(x => x.PasswordHash).HasMaxLength(256);
            e.Property(x => x.Version).IsConcurrencyToken();
        });
        m.Entity<RecoveryCode>().HasIndex(x => x.UserId);
        m.Entity<UserSession>(e =>
        {
            e.HasIndex(x => x.UserId);
            e.Property(x => x.UserAgent).HasMaxLength(256);
        });
        m.Entity<MfaChallenge>().HasIndex(x => x.TokenHash).IsUnique();
        m.Entity<LoginThrottle>().HasKey(x => x.Key);
        m.Entity<OneTimeToken>().HasIndex(x => x.TokenHash).IsUnique();

        m.Entity<Role>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.HasIndex(x => x.BuiltInKey).IsUnique().HasFilter("BuiltInKey IS NOT NULL");
            e.HasMany(x => x.Permissions).WithOne().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<RolePermission>().HasKey(x => new { x.RoleId, x.Permission });
        m.Entity<RoleAssignment>(e =>
        {
            e.HasIndex(x => new { x.PrincipalType, x.PrincipalId });
            e.HasIndex(x => new { x.PrincipalType, x.PrincipalId, x.RoleId, x.ScopeType, x.ScopeId, x.Effect }).IsUnique();
            e.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<LogicalGroup>(e =>
        {
            e.HasIndex(x => new { x.ParentId, x.Name }).IsUnique();
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.PolicyJson).HasMaxLength(32000);
        });
        m.Entity<VmTemplate>(e =>
        {
            e.HasIndex(x => new { x.HostId, x.Name }).IsUnique();
            e.HasIndex(x => x.JobId);
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.Error).HasMaxLength(2000);
            e.Property(x => x.LibraryPath).HasMaxLength(260);
            e.Property(x => x.FolderPath).HasMaxLength(300);
            e.Property(x => x.SourceVmName).HasMaxLength(200);
            e.Property(x => x.SecureBootTemplate).HasMaxLength(100);
            e.Property(x => x.DisksJson).HasMaxLength(8000);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        });
        m.Entity<GroupNetwork>(e =>
        {
            e.HasIndex(x => new { x.HostId, x.SwitchName }).IsUnique();
            e.HasIndex(x => x.GroupId);
            e.Property(x => x.SwitchName).HasMaxLength(100);
        });
        m.Entity<LogicalGroupMember>(e =>
        {
            e.HasKey(x => new { x.GroupId, x.VmId });
            e.HasIndex(x => x.VmId);
        });

        m.Entity<ManagedHost>(e =>
        {
            e.HasIndex(x => x.DisplayName).IsUnique();
            e.Property(x => x.HostFactsJson).HasMaxLength(64000);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.InventoryWarningsJson).HasMaxLength(16000);
            e.Property(x => x.AccessMessage).HasMaxLength(2000);
            e.Property(x => x.IsoLibraryPathsJson).HasMaxLength(8000);
            e.Property(x => x.TemplateLibraryPath).HasMaxLength(260);
            e.Property(x => x.VmGroupsSyncError).HasMaxLength(4000);
            e.Property(x => x.VmGroupsPlanHash).HasMaxLength(64);
            e.Property(x => x.RemoteComputerName).HasMaxLength(253);
            e.Property(x => x.RemoteUserName).HasMaxLength(256);
            e.HasIndex(x => x.ManagementAgentId);
        });
        m.Entity<ManagementAgent>(e =>
        {
            e.HasIndex(x => x.DisplayName).IsUnique();
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.MachineName).HasMaxLength(63);
            e.Property(x => x.AgentVersion).HasMaxLength(32);
        });
        m.Entity<HostVmGroupLink>(e =>
        {
            e.HasIndex(x => new { x.HostId, x.NativeId }).IsUnique();
            e.HasIndex(x => new { x.HostId, x.GroupId, x.Role }).IsUnique();
            e.Property(x => x.Role).HasMaxLength(8);
        });
        m.Entity<AgentCertificate>(e =>
        {
            e.HasIndex(x => x.ThumbprintSha256).IsUnique();
            e.HasIndex(x => x.HostId);
        });
        m.Entity<EnrollmentToken>().HasIndex(x => x.TokenHash).IsUnique();
        m.Entity<VirtualMachine>(e =>
        {
            e.Property(x => x.HyperVNotes).HasMaxLength(4000);
            e.Property(x => x.ConfigurationPath).HasMaxLength(520);
            e.HasIndex(x => new { x.HostId, x.HyperVId }).IsUnique();
            e.Property(x => x.DetailsJson).HasMaxLength(64000);
            e.HasIndex(x => x.Name);
        });
        m.Entity<InventoryEvent>().HasIndex(x => new { x.HostId, x.At });
        m.Entity<InventoryEvent>().HasIndex(x => new { x.VmId, x.At });

        m.Entity<Job>(e =>
        {
            e.HasIndex(x => new { x.Status, x.CreatedAt });
            e.HasIndex(x => new { x.TargetId, x.CreatedAt });
            e.HasIndex(x => new { x.RequestedByUserId, x.IdempotencyKey }).IsUnique().HasFilter("IdempotencyKey IS NOT NULL");
            e.Property(x => x.ResultMessage).HasMaxLength(2000);
            e.Property(x => x.ParametersJson).HasMaxLength(16000);
            e.Property(x => x.ResultJson).HasMaxLength(16000);
            e.Property(x => x.Version).IsConcurrencyToken();
        });
        m.Entity<JobEvent>().HasIndex(x => new { x.JobId, x.At });
        m.Entity<ResourceLock>().HasKey(x => x.ResourceKey);

        m.Entity<AuditEntry>(e =>
        {
            e.HasIndex(x => x.TimestampUtc);
            e.HasIndex(x => new { x.ActorUserId, x.TimestampUtc });
            e.Property(x => x.DetailsJson).HasMaxLength(4000);
        });
        m.Entity<Setting>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Value).HasMaxLength(8000);
        });
        m.Entity<ManagedHost>(e =>
        {
            e.Property(x => x.StorageFoldersJson).HasMaxLength(4000);
            e.Property(x => x.BackupStagingPath).HasMaxLength(260);
            e.Property(x => x.AgentLastUpdateResult).HasMaxLength(500);
        });
        m.Entity<ManagementAgent>().Property(x => x.LastUpdateResult).HasMaxLength(500);
        m.Entity<PortalNetwork>(e =>
        {
            e.HasIndex(x => new { x.HostId, x.Name }).IsUnique();
            e.HasIndex(x => new { x.HostId, x.SwitchName, x.VlanId }).IsUnique();
            e.Property(x => x.Name).HasMaxLength(64);
            e.Property(x => x.SwitchName).HasMaxLength(100);
            e.Property(x => x.Description).HasMaxLength(500);
        });
        m.Entity<BackupRepository>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Name).HasMaxLength(64);
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.Path).HasMaxLength(260);
            e.Property(x => x.UserName).HasMaxLength(256);
            e.Property(x => x.LastCheckJson).HasMaxLength(16000);
        });
        m.Entity<BackupJob>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.HasIndex(x => x.NextRunAt);
            e.Property(x => x.Name).HasMaxLength(64);
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.ScheduleKind).HasMaxLength(16);
            e.Property(x => x.TimesJson).HasMaxLength(400);
            e.Property(x => x.TimeZoneId).HasMaxLength(64);
            e.Property(x => x.CheckpointType).HasMaxLength(24);
            e.Property(x => x.NotifyOn).HasMaxLength(16);
        });
        m.Entity<BackupJobTarget>(e =>
        {
            e.HasKey(x => new { x.BackupJobId, x.Kind, x.TargetId });
            e.Property(x => x.Kind).HasMaxLength(8);
        });
        m.Entity<BackupRun>(e =>
        {
            e.HasIndex(x => x.StartedAt);
            e.HasIndex(x => new { x.BackupJobId, x.StartedAt });
            e.Property(x => x.Trigger).HasMaxLength(16);
            e.Property(x => x.Message).HasMaxLength(2000);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        });
        m.Entity<BackupPoint>(e =>
        {
            e.HasIndex(x => new { x.VmId, x.CreatedAt });
            e.HasIndex(x => x.RunId);
            e.HasIndex(x => x.JobId);
            e.HasIndex(x => new { x.RepositoryId, x.Status });
            e.Property(x => x.VmName).HasMaxLength(200);
            e.Property(x => x.Folder).HasMaxLength(200);
            e.Property(x => x.CheckpointType).HasMaxLength(24);
            e.Property(x => x.CheckpointTypeUsed).HasMaxLength(40);
            e.Property(x => x.ManifestJson).HasMaxLength(64000);
            e.Property(x => x.Error).HasMaxLength(2000);
            e.Property(x => x.VerifyDetail).HasMaxLength(2000);
            e.Property(x => x.TestDetail).HasMaxLength(1000);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        });
        m.Entity<ApiToken>(e =>
        {
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.Property(x => x.Name).HasMaxLength(64);
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.Property(x => x.Prefix).HasMaxLength(16);
            e.Property(x => x.Scopes).HasMaxLength(200);
            e.Property(x => x.LastUsedIp).HasMaxLength(64);
        });
    }
}
