using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HvPortal.Gateway.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class NativeVmGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "VmGroupsPlanHash",
                table: "Hosts",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VmGroupsSyncError",
                table: "Hosts",
                type: "TEXT",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "VmGroupsSyncedAt",
                table: "Hosts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "HostVmGroupLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    HostId = table.Column<Guid>(type: "TEXT", nullable: false),
                    GroupId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Role = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    NativeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HostVmGroupLinks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HostVmGroupLinks_HostId_GroupId_Role",
                table: "HostVmGroupLinks",
                columns: new[] { "HostId", "GroupId", "Role" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HostVmGroupLinks_HostId_NativeId",
                table: "HostVmGroupLinks",
                columns: new[] { "HostId", "NativeId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HostVmGroupLinks");

            migrationBuilder.DropColumn(
                name: "VmGroupsPlanHash",
                table: "Hosts");

            migrationBuilder.DropColumn(
                name: "VmGroupsSyncError",
                table: "Hosts");

            migrationBuilder.DropColumn(
                name: "VmGroupsSyncedAt",
                table: "Hosts");
        }
    }
}
