using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetWatch.Data.Config.Migrations
{
    /// <inheritdoc />
    public partial class Wp6Hardware : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BmcAddress",
                table: "Devices",
                type: "TEXT",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BmcTlsThumbprint",
                table: "Devices",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "HardwareComponents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Health = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Reading = table.Column<double>(type: "REAL", nullable: true),
                    Unit = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    Details = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HardwareComponents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HardwareComponents_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ServerGpus",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: false),
                    Index = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Vendor = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    DriverVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    UtilizationPct = table.Column<double>(type: "REAL", nullable: true),
                    MemoryUsedMb = table.Column<double>(type: "REAL", nullable: true),
                    MemoryTotalMb = table.Column<double>(type: "REAL", nullable: true),
                    TemperatureC = table.Column<double>(type: "REAL", nullable: true),
                    PowerW = table.Column<double>(type: "REAL", nullable: true),
                    FanPct = table.Column<double>(type: "REAL", nullable: true),
                    Problem = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    FirstSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServerGpus", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServerGpus_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HardwareComponents_DeviceId_Category_Name",
                table: "HardwareComponents",
                columns: new[] { "DeviceId", "Category", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServerGpus_DeviceId_Index",
                table: "ServerGpus",
                columns: new[] { "DeviceId", "Index" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HardwareComponents");

            migrationBuilder.DropTable(
                name: "ServerGpus");

            migrationBuilder.DropColumn(
                name: "BmcAddress",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "BmcTlsThumbprint",
                table: "Devices");
        }
    }
}
