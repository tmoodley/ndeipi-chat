using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NdeipiChat.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class CyberWars : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CyberWarsMatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Province = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    WinnerAllianceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CyberWarsMatches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CyberWarsOrderLogs",
                columns: table => new
                {
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Tick = table.Column<long>(type: "bigint", nullable: false),
                    Accepted = table.Column<bool>(type: "bit", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CyberWarsOrderLogs", x => x.OrderId);
                });

            migrationBuilder.CreateTable(
                name: "CyberWarsPlayers",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Rank = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    GnTokens = table.Column<long>(type: "bigint", nullable: false),
                    HomeProvince = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    ProtectedUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CyberWarsPlayers", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_CyberWarsPlayers_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CyberWarsMatchPlayers",
                columns: table => new
                {
                    MatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CyberWarsMatchPlayers", x => new { x.MatchId, x.UserId });
                    table.ForeignKey(
                        name: "FK_CyberWarsMatchPlayers_CyberWarsMatches_MatchId",
                        column: x => x.MatchId,
                        principalTable: "CyberWarsMatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CyberWarsMatchPlayers_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CyberWarsSnapshots",
                columns: table => new
                {
                    MatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Tick = table.Column<long>(type: "bigint", nullable: false),
                    StateBlob = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CyberWarsSnapshots", x => x.MatchId);
                    table.ForeignKey(
                        name: "FK_CyberWarsSnapshots_CyberWarsMatches_MatchId",
                        column: x => x.MatchId,
                        principalTable: "CyberWarsMatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CyberWarsMatches_Status_StartedAt",
                table: "CyberWarsMatches",
                columns: new[] { "Status", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CyberWarsMatchPlayers_UserId",
                table: "CyberWarsMatchPlayers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_CyberWarsOrderLogs_At",
                table: "CyberWarsOrderLogs",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_CyberWarsOrderLogs_MatchId_UserId_At",
                table: "CyberWarsOrderLogs",
                columns: new[] { "MatchId", "UserId", "At" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CyberWarsMatchPlayers");

            migrationBuilder.DropTable(
                name: "CyberWarsOrderLogs");

            migrationBuilder.DropTable(
                name: "CyberWarsPlayers");

            migrationBuilder.DropTable(
                name: "CyberWarsSnapshots");

            migrationBuilder.DropTable(
                name: "CyberWarsMatches");
        }
    }
}
