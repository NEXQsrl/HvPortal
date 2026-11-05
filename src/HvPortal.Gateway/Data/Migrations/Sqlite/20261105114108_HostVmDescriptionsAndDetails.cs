using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HvPortal.Gateway.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class HostVmDescriptionsAndDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CheckpointCount",
                table: "VirtualMachines",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "VirtualMachines",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DetailsCollectedAt",
                table: "VirtualMachines",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DetailsJson",
                table: "VirtualMachines",
                type: "TEXT",
                maxLength: 64000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "Hosts",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CheckpointCount",
                table: "VirtualMachines");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "VirtualMachines");

            migrationBuilder.DropColumn(
                name: "DetailsCollectedAt",
                table: "VirtualMachines");

            migrationBuilder.DropColumn(
                name: "DetailsJson",
                table: "VirtualMachines");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "Hosts");
        }
    }
}
