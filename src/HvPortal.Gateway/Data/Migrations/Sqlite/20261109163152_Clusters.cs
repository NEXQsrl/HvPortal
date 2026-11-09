using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HvPortal.Gateway.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class Clusters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsClustered",
                table: "VirtualMachines",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ClusterId",
                table: "Hosts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Clusters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    WindowsName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    LastStatusJson = table.Column<string>(type: "TEXT", maxLength: 256000, nullable: true),
                    LastStatusAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastStatusLevel = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    LastStatusError = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Clusters", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Hosts_ClusterId",
                table: "Hosts",
                column: "ClusterId");

            migrationBuilder.CreateIndex(
                name: "IX_Clusters_Name",
                table: "Clusters",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Clusters");

            migrationBuilder.DropIndex(
                name: "IX_Hosts_ClusterId",
                table: "Hosts");

            migrationBuilder.DropColumn(
                name: "IsClustered",
                table: "VirtualMachines");

            migrationBuilder.DropColumn(
                name: "ClusterId",
                table: "Hosts");
        }
    }
}
