using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetWatch.Data.Config.Migrations
{
    /// <inheritdoc />
    public partial class Phase4 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GroupByField",
                table: "AlertRules",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DcInfo",
                columns: table => new
                {
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: false),
                    Domain = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    Forest = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    Site = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    DcName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    IsGlobalCatalog = table.Column<bool>(type: "INTEGER", nullable: false),
                    Roles = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    PdcEmulator = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    ClockOffsetSec = table.Column<double>(type: "REAL", nullable: true),
                    SkewToPdcSec = table.Column<double>(type: "REAL", nullable: true),
                    LastReplicationCheckUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReplicationError = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    SecurityLogError = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DcInfo", x => x.DeviceId);
                    table.ForeignKey(
                        name: "FK_DcInfo_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DcReplicationLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: false),
                    Partner = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    NamingContext = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    LastSuccessUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastAttemptUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastResult = table.Column<int>(type: "INTEGER", nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    LastMessage = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DcReplicationLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DcReplicationLinks_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SavedSearches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Page = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    QueryJson = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    IsShared = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SavedSearches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SavedSearches_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DcReplicationLinks_DeviceId_Partner_NamingContext",
                table: "DcReplicationLinks",
                columns: new[] { "DeviceId", "Partner", "NamingContext" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SavedSearches_UserId_Page",
                table: "SavedSearches",
                columns: new[] { "UserId", "Page" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DcInfo");

            migrationBuilder.DropTable(
                name: "DcReplicationLinks");

            migrationBuilder.DropTable(
                name: "SavedSearches");

            migrationBuilder.DropColumn(
                name: "GroupByField",
                table: "AlertRules");
        }
    }
}
