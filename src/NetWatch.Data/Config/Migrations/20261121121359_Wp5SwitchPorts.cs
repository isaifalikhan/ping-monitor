using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetWatch.Data.Config.Migrations
{
    /// <inheritdoc />
    public partial class Wp5SwitchPorts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Duplex",
                table: "Interfaces",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ExpectedSpeedBps",
                table: "Interfaces",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FlapCount24h",
                table: "Interfaces",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "NeighborLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: false),
                    LocalInterfaceId = table.Column<int>(type: "INTEGER", nullable: true),
                    LocalPort = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Protocol = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    RemoteName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    RemoteChassisId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    RemotePort = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    RemotePortDescription = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    RemoteAddress = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    RemotePlatform = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    RemoteDeviceId = table.Column<int>(type: "INTEGER", nullable: true),
                    FirstSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NeighborLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NeighborLinks_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NeighborLinks_DeviceId",
                table: "NeighborLinks",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_NeighborLinks_RemoteDeviceId",
                table: "NeighborLinks",
                column: "RemoteDeviceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NeighborLinks");

            migrationBuilder.DropColumn(
                name: "Duplex",
                table: "Interfaces");

            migrationBuilder.DropColumn(
                name: "ExpectedSpeedBps",
                table: "Interfaces");

            migrationBuilder.DropColumn(
                name: "FlapCount24h",
                table: "Interfaces");
        }
    }
}
