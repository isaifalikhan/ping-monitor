using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetWatch.Data.Config.Migrations
{
    /// <inheritdoc />
    public partial class Phase2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SshHostKeyFingerprint",
                table: "Devices",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EscalatedUtc",
                table: "Alerts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FlapCount",
                table: "Alerts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "IsFlapping",
                table: "Alerts",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "CountThreshold",
                table: "AlertRules",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "EscalateAfterMin",
                table: "AlertRules",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FilterJson",
                table: "AlertRules",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WindowSec",
                table: "AlertRules",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "MaintenanceWindows",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ScopeJson = table.Column<string>(type: "TEXT", nullable: false),
                    Recurrence = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    StartUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EndUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    StartTimeLocal = table.Column<string>(type: "TEXT", maxLength: 5, nullable: true),
                    DaysOfWeek = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    CronExpression = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    DurationMin = table.Column<int>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenanceWindows", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ServerInfo",
                columns: table => new
                {
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: false),
                    Method = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    OsName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    OsVersion = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    LastBootUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PendingReboot = table.Column<bool>(type: "INTEGER", nullable: true),
                    LastPatchUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastPatchId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CpuCount = table.Column<int>(type: "INTEGER", nullable: true),
                    TotalMemoryBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    Load1 = table.Column<double>(type: "REAL", nullable: true),
                    Load5 = table.Column<double>(type: "REAL", nullable: true),
                    Load15 = table.Column<double>(type: "REAL", nullable: true),
                    LastSuccessUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastAttemptUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    EventLogError = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServerInfo", x => x.DeviceId);
                    table.ForeignKey(
                        name: "FK_ServerInfo_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Volumes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    FileSystem = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    TotalBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    FreeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Volumes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Volumes_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WatchedServices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: false),
                    ServiceName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false, collation: "NOCASE"),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    LastState = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    StartMode = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastChangeUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WatchedServices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WatchedServices_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Volumes_DeviceId_Name",
                table: "Volumes",
                columns: new[] { "DeviceId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WatchedServices_DeviceId_ServiceName",
                table: "WatchedServices",
                columns: new[] { "DeviceId", "ServiceName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MaintenanceWindows");

            migrationBuilder.DropTable(
                name: "ServerInfo");

            migrationBuilder.DropTable(
                name: "Volumes");

            migrationBuilder.DropTable(
                name: "WatchedServices");

            migrationBuilder.DropColumn(
                name: "SshHostKeyFingerprint",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "EscalatedUtc",
                table: "Alerts");

            migrationBuilder.DropColumn(
                name: "FlapCount",
                table: "Alerts");

            migrationBuilder.DropColumn(
                name: "IsFlapping",
                table: "Alerts");

            migrationBuilder.DropColumn(
                name: "CountThreshold",
                table: "AlertRules");

            migrationBuilder.DropColumn(
                name: "EscalateAfterMin",
                table: "AlertRules");

            migrationBuilder.DropColumn(
                name: "FilterJson",
                table: "AlertRules");

            migrationBuilder.DropColumn(
                name: "WindowSec",
                table: "AlertRules");
        }
    }
}
