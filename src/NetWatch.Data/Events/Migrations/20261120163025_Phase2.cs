using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetWatch.Data.Events.Migrations
{
    /// <inheritdoc />
    public partial class Phase2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EventBookmarks",
                columns: table => new
                {
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: false),
                    LogName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    LastRecordId = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventBookmarks", x => new { x.DeviceId, x.LogName });
                });

            migrationBuilder.CreateTable(
                name: "SnmpTraps",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ReceivedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SourceIp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: true),
                    Version = table.Column<string>(type: "TEXT", maxLength: 4, nullable: false),
                    TrapOid = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    TrapName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Severity = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    VarbindsJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SnmpTraps", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyslogMessages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ReceivedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    MessageUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SourceIp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: true),
                    Facility = table.Column<int>(type: "INTEGER", nullable: false),
                    SyslogSeverity = table.Column<int>(type: "INTEGER", nullable: false),
                    Hostname = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    AppName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ProcId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    MsgId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Message = table.Column<string>(type: "TEXT", maxLength: 8192, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyslogMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WindowsEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: false),
                    LogName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    EventId = table.Column<int>(type: "INTEGER", nullable: false),
                    Level = table.Column<int>(type: "INTEGER", nullable: false),
                    RecordId = table.Column<long>(type: "INTEGER", nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 8192, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WindowsEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SnmpTraps_DeviceId_ReceivedUtc",
                table: "SnmpTraps",
                columns: new[] { "DeviceId", "ReceivedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SnmpTraps_ReceivedUtc",
                table: "SnmpTraps",
                column: "ReceivedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_SyslogMessages_DeviceId_ReceivedUtc",
                table: "SyslogMessages",
                columns: new[] { "DeviceId", "ReceivedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SyslogMessages_ReceivedUtc",
                table: "SyslogMessages",
                column: "ReceivedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_SyslogMessages_SyslogSeverity_ReceivedUtc",
                table: "SyslogMessages",
                columns: new[] { "SyslogSeverity", "ReceivedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WindowsEvents_DeviceId_LogName_RecordId",
                table: "WindowsEvents",
                columns: new[] { "DeviceId", "LogName", "RecordId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WindowsEvents_DeviceId_TimestampUtc",
                table: "WindowsEvents",
                columns: new[] { "DeviceId", "TimestampUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WindowsEvents_TimestampUtc",
                table: "WindowsEvents",
                column: "TimestampUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EventBookmarks");

            migrationBuilder.DropTable(
                name: "SnmpTraps");

            migrationBuilder.DropTable(
                name: "SyslogMessages");

            migrationBuilder.DropTable(
                name: "WindowsEvents");
        }
    }
}
