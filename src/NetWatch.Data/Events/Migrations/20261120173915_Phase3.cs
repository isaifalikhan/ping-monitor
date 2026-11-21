using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetWatch.Data.Events.Migrations
{
    /// <inheritdoc />
    public partial class Phase3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VmwareEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceDeviceId = table.Column<int>(type: "INTEGER", nullable: false),
                    EventKey = table.Column<long>(type: "INTEGER", nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Severity = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    EntityName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    Message = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VmwareEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VmwareEvents_SourceDeviceId_EventKey",
                table: "VmwareEvents",
                columns: new[] { "SourceDeviceId", "EventKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VmwareEvents_TimestampUtc",
                table: "VmwareEvents",
                column: "TimestampUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VmwareEvents");
        }
    }
}
