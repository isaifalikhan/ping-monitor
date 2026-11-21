using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetWatch.Data.Events.Migrations
{
    /// <inheritdoc />
    public partial class Phase4 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DcSecurityEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: false),
                    EventId = table.Column<int>(type: "INTEGER", nullable: false),
                    RecordId = table.Column<long>(type: "INTEGER", nullable: false),
                    Severity = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SubjectUser = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    SubjectDomain = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    TargetUser = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    TargetDomain = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    GroupName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    ObjectName = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    SourceIp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Workstation = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    LogonType = table.Column<int>(type: "INTEGER", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    SubStatus = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    Message = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DcSecurityEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DcSecurityEvents_DeviceId_RecordId",
                table: "DcSecurityEvents",
                columns: new[] { "DeviceId", "RecordId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DcSecurityEvents_EventId_TimestampUtc",
                table: "DcSecurityEvents",
                columns: new[] { "EventId", "TimestampUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DcSecurityEvents_SourceIp",
                table: "DcSecurityEvents",
                column: "SourceIp");

            migrationBuilder.CreateIndex(
                name: "IX_DcSecurityEvents_TargetUser",
                table: "DcSecurityEvents",
                column: "TargetUser");

            migrationBuilder.CreateIndex(
                name: "IX_DcSecurityEvents_TimestampUtc",
                table: "DcSecurityEvents",
                column: "TimestampUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DcSecurityEvents");
        }
    }
}
