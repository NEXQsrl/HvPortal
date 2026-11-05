using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HvPortal.Gateway.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentCertificates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    HostId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ThumbprintSha256 = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    SerialNumber = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    NotBefore = table.Column<long>(type: "INTEGER", nullable: false),
                    NotAfter = table.Column<long>(type: "INTEGER", nullable: false),
                    IssuedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RevokedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    RevokedReason = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentCertificates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TimestampUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ActorName = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    AuthMethod = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    SourceIp = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    Action = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    ResourceType = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    ResourceId = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    CorrelationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DetailsJson = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    KeyId = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    PreviousHash = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Hash = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EnrollmentTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    HostId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ConsumedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ConsumedFromIp = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnrollmentTokens", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Hosts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    MachineName = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    DnsHostName = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    EnrolledAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastSeenAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastInventoryAt = table.Column<long>(type: "INTEGER", nullable: true),
                    AgentVersion = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    HostFactsJson = table.Column<string>(type: "TEXT", maxLength: 16000, nullable: true),
                    Access = table.Column<string>(type: "TEXT", maxLength: 24, nullable: true),
                    AccessMessage = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    InventoryPartial = table.Column<bool>(type: "INTEGER", nullable: false),
                    InventoryWarningsJson = table.Column<string>(type: "TEXT", maxLength: 16000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Hosts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InventoryEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    HostId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VmId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Details = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    At = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JobEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    At = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Operation = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    TargetType = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    TargetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetName = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    HostId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RequestedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequiredPermission = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Progress = table.Column<int>(type: "INTEGER", nullable: false),
                    ProgressMessage = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    NotBefore = table.Column<long>(type: "INTEGER", nullable: true),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    DispatchedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    HeartbeatAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LeaseOwner = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    LeaseExpiresAt = table.Column<long>(type: "INTEGER", nullable: true),
                    TimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    Attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    CorrelationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CancelRequested = table.Column<bool>(type: "INTEGER", nullable: false),
                    ResultMessage = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    ErrorCode = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    HyperVCode = table.Column<long>(type: "INTEGER", nullable: true),
                    ReconcileStartedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LogicalGroupMembers",
                columns: table => new
                {
                    GroupId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VmId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LogicalGroupMembers", x => new { x.GroupId, x.VmId });
                });

            migrationBuilder.CreateTable(
                name: "LogicalGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LogicalGroups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LoginThrottles",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Failures = table.Column<int>(type: "INTEGER", nullable: false),
                    LastFailureAt = table.Column<long>(type: "INTEGER", nullable: false),
                    BlockedUntil = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoginThrottles", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "MfaChallenges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    ConsumedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    IpAddress = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MfaChallenges", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OneTimeTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Purpose = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ConsumedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OneTimeTokens", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RecoveryCodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CodeHash = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UsedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecoveryCodes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ResourceLocks",
                columns: table => new
                {
                    ResourceKey = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AcquiredAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResourceLocks", x => x.ResourceKey);
                });

            migrationBuilder.CreateTable(
                name: "Roles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    IsBuiltIn = table.Column<bool>(type: "INTEGER", nullable: false),
                    BuiltInKey = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SecurityStamp = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSeenAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RevokedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    RevokedReason = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    AuthMethods = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    MfaSatisfied = table.Column<bool>(type: "INTEGER", nullable: false),
                    ReauthenticatedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    IpAddress = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    UserAgent = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Value = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    NormalizedUserName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    AuthSource = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    ExternalTenantId = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    ExternalObjectId = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    PasswordHash = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    MustChangePassword = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsBreakGlass = table.Column<bool>(type: "INTEGER", nullable: false),
                    SecurityStamp = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    PasswordChangedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastLoginAt = table.Column<long>(type: "INTEGER", nullable: true),
                    TotpSecretEncrypted = table.Column<byte[]>(type: "BLOB", nullable: true),
                    TotpEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    TotpEnabledAt = table.Column<long>(type: "INTEGER", nullable: true),
                    TotpLastUsedStep = table.Column<long>(type: "INTEGER", nullable: false),
                    PendingTotpSecretEncrypted = table.Column<byte[]>(type: "BLOB", nullable: true),
                    PendingTotpCreatedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VirtualMachines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    HostId = table.Column<Guid>(type: "TEXT", nullable: false),
                    HyperVId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    PowerState = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    EnabledStateRaw = table.Column<int>(type: "INTEGER", nullable: false),
                    IsCritical = table.Column<bool>(type: "INTEGER", nullable: false),
                    Generation = table.Column<int>(type: "INTEGER", nullable: true),
                    ConfigurationVersion = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    ProcessorCount = table.Column<int>(type: "INTEGER", nullable: true),
                    MemoryStartupMb = table.Column<long>(type: "INTEGER", nullable: true),
                    MemoryAssignedMb = table.Column<long>(type: "INTEGER", nullable: true),
                    DynamicMemoryEnabled = table.Column<bool>(type: "INTEGER", nullable: true),
                    UptimeMs = table.Column<long>(type: "INTEGER", nullable: true),
                    Heartbeat = table.Column<int>(type: "INTEGER", nullable: true),
                    HealthState = table.Column<int>(type: "INTEGER", nullable: true),
                    ProcessorLoadPercent = table.Column<int>(type: "INTEGER", nullable: true),
                    GuestOperatingSystem = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    ShutdownServiceAvailable = table.Column<bool>(type: "INTEGER", nullable: true),
                    OperationalStatusJson = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    FirstSeenAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSeenAt = table.Column<long>(type: "INTEGER", nullable: false),
                    StateChangedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    IsMissing = table.Column<bool>(type: "INTEGER", nullable: false),
                    MissingSince = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VirtualMachines", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RoleAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PrincipalType = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    PrincipalId = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    RoleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ScopeType = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    ScopeId = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Effect = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoleAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RoleAssignments_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RolePermissions",
                columns: table => new
                {
                    RoleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Permission = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RolePermissions", x => new { x.RoleId, x.Permission });
                    table.ForeignKey(
                        name: "FK_RolePermissions_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentCertificates_HostId",
                table: "AgentCertificates",
                column: "HostId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentCertificates_ThumbprintSha256",
                table: "AgentCertificates",
                column: "ThumbprintSha256",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_ActorUserId_TimestampUtc",
                table: "AuditEntries",
                columns: new[] { "ActorUserId", "TimestampUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_TimestampUtc",
                table: "AuditEntries",
                column: "TimestampUtc");

            migrationBuilder.CreateIndex(
                name: "IX_EnrollmentTokens_TokenHash",
                table: "EnrollmentTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Hosts_DisplayName",
                table: "Hosts",
                column: "DisplayName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryEvents_HostId_At",
                table: "InventoryEvents",
                columns: new[] { "HostId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryEvents_VmId_At",
                table: "InventoryEvents",
                columns: new[] { "VmId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_JobEvents_JobId_At",
                table: "JobEvents",
                columns: new[] { "JobId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_RequestedByUserId_IdempotencyKey",
                table: "Jobs",
                columns: new[] { "RequestedByUserId", "IdempotencyKey" },
                unique: true,
                filter: "IdempotencyKey IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_Status_CreatedAt",
                table: "Jobs",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_TargetId_CreatedAt",
                table: "Jobs",
                columns: new[] { "TargetId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_LogicalGroupMembers_VmId",
                table: "LogicalGroupMembers",
                column: "VmId");

            migrationBuilder.CreateIndex(
                name: "IX_LogicalGroups_Name",
                table: "LogicalGroups",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MfaChallenges_TokenHash",
                table: "MfaChallenges",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OneTimeTokens_TokenHash",
                table: "OneTimeTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecoveryCodes_UserId",
                table: "RecoveryCodes",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignments_PrincipalType_PrincipalId",
                table: "RoleAssignments",
                columns: new[] { "PrincipalType", "PrincipalId" });

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignments_PrincipalType_PrincipalId_RoleId_ScopeType_ScopeId_Effect",
                table: "RoleAssignments",
                columns: new[] { "PrincipalType", "PrincipalId", "RoleId", "ScopeType", "ScopeId", "Effect" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignments_RoleId",
                table: "RoleAssignments",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_Roles_BuiltInKey",
                table: "Roles",
                column: "BuiltInKey",
                unique: true,
                filter: "BuiltInKey IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Roles_Name",
                table: "Roles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_UserId",
                table: "Sessions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_ExternalTenantId_ExternalObjectId",
                table: "Users",
                columns: new[] { "ExternalTenantId", "ExternalObjectId" },
                unique: true,
                filter: "ExternalObjectId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Users_NormalizedUserName",
                table: "Users",
                column: "NormalizedUserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VirtualMachines_HostId_HyperVId",
                table: "VirtualMachines",
                columns: new[] { "HostId", "HyperVId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VirtualMachines_Name",
                table: "VirtualMachines",
                column: "Name");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentCertificates");

            migrationBuilder.DropTable(
                name: "AuditEntries");

            migrationBuilder.DropTable(
                name: "EnrollmentTokens");

            migrationBuilder.DropTable(
                name: "Hosts");

            migrationBuilder.DropTable(
                name: "InventoryEvents");

            migrationBuilder.DropTable(
                name: "JobEvents");

            migrationBuilder.DropTable(
                name: "Jobs");

            migrationBuilder.DropTable(
                name: "LogicalGroupMembers");

            migrationBuilder.DropTable(
                name: "LogicalGroups");

            migrationBuilder.DropTable(
                name: "LoginThrottles");

            migrationBuilder.DropTable(
                name: "MfaChallenges");

            migrationBuilder.DropTable(
                name: "OneTimeTokens");

            migrationBuilder.DropTable(
                name: "RecoveryCodes");

            migrationBuilder.DropTable(
                name: "ResourceLocks");

            migrationBuilder.DropTable(
                name: "RoleAssignments");

            migrationBuilder.DropTable(
                name: "RolePermissions");

            migrationBuilder.DropTable(
                name: "Sessions");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "VirtualMachines");

            migrationBuilder.DropTable(
                name: "Roles");
        }
    }
}
