using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HvPortal.Gateway.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class GroupsOperationsConsole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LogicalGroups_Name",
                table: "LogicalGroups");

            migrationBuilder.AddColumn<Guid>(
                name: "ParentId",
                table: "LogicalGroups",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParametersJson",
                table: "Jobs",
                type: "TEXT",
                maxLength: 16000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResultJson",
                table: "Jobs",
                type: "TEXT",
                maxLength: 16000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IsoLibraryPathsJson",
                table: "Hosts",
                type: "TEXT",
                maxLength: 8000,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_LogicalGroups_ParentId_Name",
                table: "LogicalGroups",
                columns: new[] { "ParentId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LogicalGroups_ParentId_Name",
                table: "LogicalGroups");

            migrationBuilder.DropColumn(
                name: "ParentId",
                table: "LogicalGroups");

            migrationBuilder.DropColumn(
                name: "ParametersJson",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "ResultJson",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "IsoLibraryPathsJson",
                table: "Hosts");

            migrationBuilder.CreateIndex(
                name: "IX_LogicalGroups_Name",
                table: "LogicalGroups",
                column: "Name",
                unique: true);
        }
    }
}
