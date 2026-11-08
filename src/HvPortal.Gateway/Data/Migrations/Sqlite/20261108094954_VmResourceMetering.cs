using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HvPortal.Gateway.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class VmResourceMetering : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VmMeterings",
                columns: table => new
                {
                    VmId = table.Column<Guid>(type: "TEXT", nullable: false),
                    HostId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    EnabledAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastResetAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastSampleAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    LastErrorAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VmMeterings", x => x.VmId);
                });

            migrationBuilder.CreateTable(
                name: "VmMetricSamples",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    VmId = table.Column<Guid>(type: "TEXT", nullable: false),
                    At = table.Column<long>(type: "INTEGER", nullable: false),
                    ObservedSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    ProcessorCount = table.Column<int>(type: "INTEGER", nullable: true),
                    CpuAvgPercent = table.Column<double>(type: "REAL", nullable: true),
                    CpuMaxPercent = table.Column<double>(type: "REAL", nullable: true),
                    MemoryAssignedAvgMb = table.Column<long>(type: "INTEGER", nullable: true),
                    MemoryAssignedMaxMb = table.Column<long>(type: "INTEGER", nullable: true),
                    MemoryDemandMb = table.Column<long>(type: "INTEGER", nullable: true),
                    MemoryMaximumMb = table.Column<long>(type: "INTEGER", nullable: true),
                    MemoryStartupMb = table.Column<long>(type: "INTEGER", nullable: true),
                    DynamicMemory = table.Column<bool>(type: "INTEGER", nullable: true),
                    MemoryStatus = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    MemoryAvailableMinPercent = table.Column<int>(type: "INTEGER", nullable: true),
                    MeteringSeconds = table.Column<double>(type: "REAL", nullable: true),
                    AverageCpuMhz = table.Column<long>(type: "INTEGER", nullable: true),
                    MaximumMemoryMb = table.Column<long>(type: "INTEGER", nullable: true),
                    TotalDiskMb = table.Column<long>(type: "INTEGER", nullable: true),
                    NormalizedIoCount = table.Column<long>(type: "INTEGER", nullable: true),
                    AverageLatencyMs = table.Column<long>(type: "INTEGER", nullable: true),
                    DiskReadMb = table.Column<long>(type: "INTEGER", nullable: true),
                    DiskWrittenMb = table.Column<long>(type: "INTEGER", nullable: true),
                    NetworkInboundMb = table.Column<long>(type: "INTEGER", nullable: true),
                    NetworkOutboundMb = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VmMetricSamples", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VmMeterings_HostId",
                table: "VmMeterings",
                column: "HostId");

            migrationBuilder.CreateIndex(
                name: "IX_VmMetricSamples_At",
                table: "VmMetricSamples",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_VmMetricSamples_VmId_At",
                table: "VmMetricSamples",
                columns: new[] { "VmId", "At" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VmMeterings");

            migrationBuilder.DropTable(
                name: "VmMetricSamples");
        }
    }
}
