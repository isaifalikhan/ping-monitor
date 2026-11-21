using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetWatch.Data.Config.Migrations
{
    /// <inheritdoc />
    public partial class Phase3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TlsThumbprint",
                table: "Devices",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "VmwareAlarms",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceDeviceId = table.Column<int>(type: "INTEGER", nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    AlarmName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    EntityMoRef = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    EntityName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Acknowledged = table.Column<bool>(type: "INTEGER", nullable: false),
                    TriggeredUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VmwareAlarms", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VmwareAlarms_Devices_SourceDeviceId",
                        column: x => x.SourceDeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VmwareDatastores",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceDeviceId = table.Column<int>(type: "INTEGER", nullable: false),
                    MoRef = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    CapacityBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    FreeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    UncommittedBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    Accessible = table.Column<bool>(type: "INTEGER", nullable: false),
                    MaintenanceMode = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    HostCount = table.Column<int>(type: "INTEGER", nullable: false),
                    VmCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VmwareDatastores", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VmwareDatastores_Devices_SourceDeviceId",
                        column: x => x.SourceDeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VmwareHosts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceDeviceId = table.Column<int>(type: "INTEGER", nullable: false),
                    MoRef = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Cluster = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    ConnectionState = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    PowerState = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    InMaintenance = table.Column<bool>(type: "INTEGER", nullable: false),
                    OverallStatus = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    Vendor = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Model = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Product = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CpuCores = table.Column<int>(type: "INTEGER", nullable: true),
                    CpuTotalMhz = table.Column<long>(type: "INTEGER", nullable: true),
                    CpuUsageMhz = table.Column<long>(type: "INTEGER", nullable: true),
                    MemTotalBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    MemUsageBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    BootTimeUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SensorWarnings = table.Column<int>(type: "INTEGER", nullable: false),
                    SensorAlarms = table.Column<int>(type: "INTEGER", nullable: false),
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: true),
                    LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VmwareHosts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VmwareHosts_Devices_SourceDeviceId",
                        column: x => x.SourceDeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VmwareSnapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceDeviceId = table.Column<int>(type: "INTEGER", nullable: false),
                    VmMoRef = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    VmName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    MoRef = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    IsCurrent = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VmwareSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VmwareSnapshots_Devices_SourceDeviceId",
                        column: x => x.SourceDeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VmwareVms",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceDeviceId = table.Column<int>(type: "INTEGER", nullable: false),
                    MoRef = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    HostMoRef = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    HostName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    PowerState = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    GuestOs = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    IpAddress = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    GuestHostName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    ToolsRunningStatus = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ToolsVersionStatus = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    OverallStatus = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    NumCpu = table.Column<int>(type: "INTEGER", nullable: true),
                    MemoryMb = table.Column<int>(type: "INTEGER", nullable: true),
                    CpuPct = table.Column<double>(type: "REAL", nullable: true),
                    CpuReadyPct = table.Column<double>(type: "REAL", nullable: true),
                    MemPct = table.Column<double>(type: "REAL", nullable: true),
                    BalloonedMb = table.Column<long>(type: "INTEGER", nullable: true),
                    CommittedBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    UncommittedBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    GuestDiskMaxPct = table.Column<double>(type: "REAL", nullable: true),
                    Datastores = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    SnapshotCount = table.Column<int>(type: "INTEGER", nullable: false),
                    BootTimeUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: true),
                    LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VmwareVms", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VmwareVms_Devices_SourceDeviceId",
                        column: x => x.SourceDeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VmwareAlarms_SourceDeviceId_Key",
                table: "VmwareAlarms",
                columns: new[] { "SourceDeviceId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VmwareDatastores_SourceDeviceId_MoRef",
                table: "VmwareDatastores",
                columns: new[] { "SourceDeviceId", "MoRef" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VmwareHosts_SourceDeviceId_MoRef",
                table: "VmwareHosts",
                columns: new[] { "SourceDeviceId", "MoRef" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VmwareSnapshots_SourceDeviceId_MoRef",
                table: "VmwareSnapshots",
                columns: new[] { "SourceDeviceId", "MoRef" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VmwareVms_DeviceId",
                table: "VmwareVms",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_VmwareVms_SourceDeviceId_MoRef",
                table: "VmwareVms",
                columns: new[] { "SourceDeviceId", "MoRef" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VmwareAlarms");

            migrationBuilder.DropTable(
                name: "VmwareDatastores");

            migrationBuilder.DropTable(
                name: "VmwareHosts");

            migrationBuilder.DropTable(
                name: "VmwareSnapshots");

            migrationBuilder.DropTable(
                name: "VmwareVms");

            migrationBuilder.DropColumn(
                name: "TlsThumbprint",
                table: "Devices");
        }
    }
}
