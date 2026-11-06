using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HvPortal.Gateway.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class VmGroupAdoption : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "VmGroupsAdoptedAt",
                table: "Hosts",
                type: "INTEGER",
                nullable: true);

            // Hosts where the portal already owns native groups (mirrored or imported on request) are settled:
            // the automatic import of existing groups (ADR-027) is for hosts the portal has not organised yet.
            migrationBuilder.Sql(
                "UPDATE Hosts SET VmGroupsAdoptedAt = COALESCE(VmGroupsSyncedAt, CreatedAt) " +
                "WHERE Id IN (SELECT HostId FROM HostVmGroupLinks);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VmGroupsAdoptedAt",
                table: "Hosts");
        }
    }
}
