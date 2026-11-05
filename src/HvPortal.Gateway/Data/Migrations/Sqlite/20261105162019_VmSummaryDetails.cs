using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HvPortal.Gateway.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class VmSummaryDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ConfigurationPath",
                table: "VirtualMachines",
                type: "TEXT",
                maxLength: 520,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "HyperVCreatedAt",
                table: "VirtualMachines",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HyperVNotes",
                table: "VirtualMachines",
                type: "TEXT",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MemoryAvailablePercent",
                table: "VirtualMachines",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MemoryBufferPercent",
                table: "VirtualMachines",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReplicationHealth",
                table: "VirtualMachines",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReplicationMode",
                table: "VirtualMachines",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReplicationState",
                table: "VirtualMachines",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConfigurationPath",
                table: "VirtualMachines");

            migrationBuilder.DropColumn(
                name: "HyperVCreatedAt",
                table: "VirtualMachines");

            migrationBuilder.DropColumn(
                name: "HyperVNotes",
                table: "VirtualMachines");

            migrationBuilder.DropColumn(
                name: "MemoryAvailablePercent",
                table: "VirtualMachines");

            migrationBuilder.DropColumn(
                name: "MemoryBufferPercent",
                table: "VirtualMachines");

            migrationBuilder.DropColumn(
                name: "ReplicationHealth",
                table: "VirtualMachines");

            migrationBuilder.DropColumn(
                name: "ReplicationMode",
                table: "VirtualMachines");

            migrationBuilder.DropColumn(
                name: "ReplicationState",
                table: "VirtualMachines");
        }
    }
}
