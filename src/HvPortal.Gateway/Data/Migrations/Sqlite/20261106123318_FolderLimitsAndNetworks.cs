using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HvPortal.Gateway.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class FolderLimitsAndNetworks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The portal-only description of VMs is replaced by the Hyper-V notes (synchronized with the host).
            // Existing descriptions are not lost: each one is kept as an entry of the VM's history.
            var at = (long)new Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter().ConvertToProvider(DateTimeOffset.UtcNow)!;
            migrationBuilder.Sql(
                "INSERT INTO InventoryEvents (HostId, VmId, Kind, Details, At) " +
                "SELECT HostId, Id, 'vm.description.removed', substr(Description, 1, 1000), " + at.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                " FROM VirtualMachines WHERE Description IS NOT NULL AND trim(Description) <> ''");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "VirtualMachines");

            migrationBuilder.AddColumn<long>(
                name: "DiskProvisionedBytes",
                table: "VirtualMachines",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "MemoryMaximumMb",
                table: "VirtualMachines",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PolicyJson",
                table: "LogicalGroups",
                type: "TEXT",
                maxLength: 32000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GroupNetworks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    GroupId = table.Column<Guid>(type: "TEXT", nullable: false),
                    HostId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SwitchName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    SwitchId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupNetworks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GroupNetworks_GroupId",
                table: "GroupNetworks",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_GroupNetworks_HostId_SwitchName",
                table: "GroupNetworks",
                columns: new[] { "HostId", "SwitchName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GroupNetworks");

            migrationBuilder.DropColumn(
                name: "DiskProvisionedBytes",
                table: "VirtualMachines");

            migrationBuilder.DropColumn(
                name: "MemoryMaximumMb",
                table: "VirtualMachines");

            migrationBuilder.DropColumn(
                name: "PolicyJson",
                table: "LogicalGroups");

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "VirtualMachines",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);
        }
    }
}
