using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HvPortal.Gateway.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class BackupsNetworksAgentUpdates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastUpdateResult",
                table: "ManagementAgents",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "UpdaterInstalled",
                table: "ManagementAgents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AgentLastUpdateResult",
                table: "Hosts",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AgentUpdaterInstalled",
                table: "Hosts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BackupStagingPath",
                table: "Hosts",
                type: "TEXT",
                maxLength: 260,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StorageFoldersJson",
                table: "Hosts",
                type: "TEXT",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ApiTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Prefix = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Scopes = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastUsedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastUsedIp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    RevokedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiTokens", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BackupJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    RepositoryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ScheduleKind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    DaysMask = table.Column<int>(type: "INTEGER", nullable: false),
                    TimesJson = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    IntervalHours = table.Column<int>(type: "INTEGER", nullable: false),
                    TimeZoneId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    RetentionPoints = table.Column<int>(type: "INTEGER", nullable: false),
                    RetentionDays = table.Column<int>(type: "INTEGER", nullable: true),
                    CheckpointType = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    VerifyAfterCopy = table.Column<bool>(type: "INTEGER", nullable: false),
                    RestoreTestDays = table.Column<int>(type: "INTEGER", nullable: false),
                    NotifyOn = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    NextRunAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastRunAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastRestoreTestAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BackupJobTargets",
                columns: table => new
                {
                    BackupJobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    TargetId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupJobTargets", x => new { x.BackupJobId, x.Kind, x.TargetId });
                });

            migrationBuilder.CreateTable(
                name: "BackupPoints",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: true),
                    BackupJobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RepositoryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VmId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VmHyperVId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VmName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    HostId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Folder = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    CheckpointType = table.Column<string>(type: "TEXT", maxLength: 24, nullable: true),
                    CheckpointTypeUsed = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    ManifestJson = table.Column<string>(type: "TEXT", maxLength: 64000, nullable: true),
                    Error = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    VerifiedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    VerifyOk = table.Column<bool>(type: "INTEGER", nullable: true),
                    VerifyDetail = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    TestedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    TestPassed = table.Column<bool>(type: "INTEGER", nullable: true),
                    TestDetail = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Keep = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupPoints", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BackupRepositories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Path = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                    HostId = table.Column<Guid>(type: "TEXT", nullable: true),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    PasswordEncrypted = table.Column<byte[]>(type: "BLOB", nullable: true),
                    CredentialUpdatedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LastCheckedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastCheckHostId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LastCheckJson = table.Column<string>(type: "TEXT", maxLength: 16000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupRepositories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BackupRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BackupJobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Trigger = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Total = table.Column<int>(type: "INTEGER", nullable: false),
                    Succeeded = table.Column<int>(type: "INTEGER", nullable: false),
                    Warnings = table.Column<int>(type: "INTEGER", nullable: false),
                    Failed = table.Column<int>(type: "INTEGER", nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Notified = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PortalNetworks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    HostId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SwitchName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    VlanId = table.Column<int>(type: "INTEGER", nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PortalNetworks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApiTokens_TokenHash",
                table: "ApiTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BackupJobs_Name",
                table: "BackupJobs",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BackupJobs_NextRunAt",
                table: "BackupJobs",
                column: "NextRunAt");

            migrationBuilder.CreateIndex(
                name: "IX_BackupPoints_JobId",
                table: "BackupPoints",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_BackupPoints_RepositoryId_Status",
                table: "BackupPoints",
                columns: new[] { "RepositoryId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_BackupPoints_RunId",
                table: "BackupPoints",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_BackupPoints_VmId_CreatedAt",
                table: "BackupPoints",
                columns: new[] { "VmId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BackupRepositories_Name",
                table: "BackupRepositories",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BackupRuns_BackupJobId_StartedAt",
                table: "BackupRuns",
                columns: new[] { "BackupJobId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BackupRuns_StartedAt",
                table: "BackupRuns",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_PortalNetworks_HostId_Name",
                table: "PortalNetworks",
                columns: new[] { "HostId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PortalNetworks_HostId_SwitchName_VlanId",
                table: "PortalNetworks",
                columns: new[] { "HostId", "SwitchName", "VlanId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApiTokens");

            migrationBuilder.DropTable(
                name: "BackupJobs");

            migrationBuilder.DropTable(
                name: "BackupJobTargets");

            migrationBuilder.DropTable(
                name: "BackupPoints");

            migrationBuilder.DropTable(
                name: "BackupRepositories");

            migrationBuilder.DropTable(
                name: "BackupRuns");

            migrationBuilder.DropTable(
                name: "PortalNetworks");

            migrationBuilder.DropColumn(
                name: "LastUpdateResult",
                table: "ManagementAgents");

            migrationBuilder.DropColumn(
                name: "UpdaterInstalled",
                table: "ManagementAgents");

            migrationBuilder.DropColumn(
                name: "AgentLastUpdateResult",
                table: "Hosts");

            migrationBuilder.DropColumn(
                name: "AgentUpdaterInstalled",
                table: "Hosts");

            migrationBuilder.DropColumn(
                name: "BackupStagingPath",
                table: "Hosts");

            migrationBuilder.DropColumn(
                name: "StorageFoldersJson",
                table: "Hosts");
        }
    }
}
