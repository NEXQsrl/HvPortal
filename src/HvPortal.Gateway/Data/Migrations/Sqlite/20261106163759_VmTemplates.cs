using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HvPortal.Gateway.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class VmTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TemplateLibraryPath",
                table: "Hosts",
                type: "TEXT",
                maxLength: 260,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "VmTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    HostId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    LibraryPath = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                    FolderPath = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    SourceVmName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Generation = table.Column<int>(type: "INTEGER", nullable: false),
                    ProcessorCount = table.Column<int>(type: "INTEGER", nullable: false),
                    MemoryStartupMb = table.Column<long>(type: "INTEGER", nullable: false),
                    DynamicMemory = table.Column<bool>(type: "INTEGER", nullable: false),
                    MemoryMinimumMb = table.Column<long>(type: "INTEGER", nullable: true),
                    MemoryMaximumMb = table.Column<long>(type: "INTEGER", nullable: true),
                    SecureBootTemplate = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    TpmEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    DisksJson = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VmTemplates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VmTemplates_HostId_Name",
                table: "VmTemplates",
                columns: new[] { "HostId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VmTemplates_JobId",
                table: "VmTemplates",
                column: "JobId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VmTemplates");

            migrationBuilder.DropColumn(
                name: "TemplateLibraryPath",
                table: "Hosts");
        }
    }
}
