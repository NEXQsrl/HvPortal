using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HvPortal.Gateway.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class ManagementAgents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ConnectionMode",
                table: "Hosts",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "Agent"); // existing hosts run their own agent

            migrationBuilder.AddColumn<Guid>(
                name: "ManagementAgentId",
                table: "Hosts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RemoteComputerName",
                table: "Hosts",
                type: "TEXT",
                maxLength: 253,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RemoteCredentialUpdatedAt",
                table: "Hosts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RemotePasswordEncrypted",
                table: "Hosts",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RemoteUseHttps",
                table: "Hosts",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "RemoteUserName",
                table: "Hosts",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ManagementAgents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    MachineName = table.Column<string>(type: "TEXT", maxLength: 63, nullable: true),
                    AgentVersion = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    EnrolledAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastSeenAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManagementAgents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Hosts_ManagementAgentId",
                table: "Hosts",
                column: "ManagementAgentId");

            migrationBuilder.CreateIndex(
                name: "IX_ManagementAgents_DisplayName",
                table: "ManagementAgents",
                column: "DisplayName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ManagementAgents");

            migrationBuilder.DropIndex(
                name: "IX_Hosts_ManagementAgentId",
                table: "Hosts");

            migrationBuilder.DropColumn(
                name: "ConnectionMode",
                table: "Hosts");

            migrationBuilder.DropColumn(
                name: "ManagementAgentId",
                table: "Hosts");

            migrationBuilder.DropColumn(
                name: "RemoteComputerName",
                table: "Hosts");

            migrationBuilder.DropColumn(
                name: "RemoteCredentialUpdatedAt",
                table: "Hosts");

            migrationBuilder.DropColumn(
                name: "RemotePasswordEncrypted",
                table: "Hosts");

            migrationBuilder.DropColumn(
                name: "RemoteUseHttps",
                table: "Hosts");

            migrationBuilder.DropColumn(
                name: "RemoteUserName",
                table: "Hosts");
        }
    }
}
