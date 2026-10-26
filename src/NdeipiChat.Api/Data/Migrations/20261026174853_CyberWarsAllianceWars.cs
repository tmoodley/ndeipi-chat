using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NdeipiChat.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class CyberWarsAllianceWars : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Side",
                table: "CyberWarsMatchPlayers",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "solo");

            migrationBuilder.AddColumn<string>(
                name: "Mode",
                table: "CyberWarsMatches",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "skirmish");

            migrationBuilder.AddColumn<int>(
                name: "SectorQ",
                table: "CyberWarsMatches",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SectorR",
                table: "CyberWarsMatches",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WinnerUserId",
                table: "CyberWarsMatches",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CyberWarsAlliances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Province = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LeaderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CyberWarsAlliances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CyberWarsAlliances_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CyberWarsBases",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Province = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    SectorQ = table.Column<int>(type: "int", nullable: false),
                    SectorR = table.Column<int>(type: "int", nullable: false),
                    LayoutJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CyberWarsBases", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_CyberWarsBases_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CyberWarsSectorControls",
                columns: table => new
                {
                    Province = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    SectorQ = table.Column<int>(type: "int", nullable: false),
                    SectorR = table.Column<int>(type: "int", nullable: false),
                    ControllerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CapturedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CyberWarsSectorControls", x => new { x.Province, x.SectorQ, x.SectorR });
                    table.ForeignKey(
                        name: "FK_CyberWarsSectorControls_Users_ControllerUserId",
                        column: x => x.ControllerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CyberWarsAllianceMembers",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AllianceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JoinedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastReinforceAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CyberWarsAllianceMembers", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_CyberWarsAllianceMembers_CyberWarsAlliances_AllianceId",
                        column: x => x.AllianceId,
                        principalTable: "CyberWarsAlliances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CyberWarsAllianceMembers_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CyberWarsStrikes",
                columns: table => new
                {
                    AllianceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Province = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    SectorQ = table.Column<int>(type: "int", nullable: false),
                    SectorR = table.Column<int>(type: "int", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ApprovedUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CyberWarsStrikes", x => x.AllianceId);
                    table.ForeignKey(
                        name: "FK_CyberWarsStrikes_CyberWarsAlliances_AllianceId",
                        column: x => x.AllianceId,
                        principalTable: "CyberWarsAlliances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CyberWarsStrikeBallots",
                columns: table => new
                {
                    AllianceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CyberWarsStrikeBallots", x => new { x.AllianceId, x.UserId });
                    table.ForeignKey(
                        name: "FK_CyberWarsStrikeBallots_CyberWarsStrikes_AllianceId",
                        column: x => x.AllianceId,
                        principalTable: "CyberWarsStrikes",
                        principalColumn: "AllianceId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CyberWarsMatches_Province_SectorQ_SectorR_Status",
                table: "CyberWarsMatches",
                columns: new[] { "Province", "SectorQ", "SectorR", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_CyberWarsAllianceMembers_AllianceId",
                table: "CyberWarsAllianceMembers",
                column: "AllianceId");

            migrationBuilder.CreateIndex(
                name: "IX_CyberWarsAlliances_ConversationId",
                table: "CyberWarsAlliances",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_CyberWarsAlliances_Name",
                table: "CyberWarsAlliances",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CyberWarsBases_Province_SectorQ_SectorR",
                table: "CyberWarsBases",
                columns: new[] { "Province", "SectorQ", "SectorR" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CyberWarsSectorControls_ControllerUserId",
                table: "CyberWarsSectorControls",
                column: "ControllerUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CyberWarsAllianceMembers");

            migrationBuilder.DropTable(
                name: "CyberWarsBases");

            migrationBuilder.DropTable(
                name: "CyberWarsSectorControls");

            migrationBuilder.DropTable(
                name: "CyberWarsStrikeBallots");

            migrationBuilder.DropTable(
                name: "CyberWarsStrikes");

            migrationBuilder.DropTable(
                name: "CyberWarsAlliances");

            migrationBuilder.DropIndex(
                name: "IX_CyberWarsMatches_Province_SectorQ_SectorR_Status",
                table: "CyberWarsMatches");

            migrationBuilder.DropColumn(
                name: "Side",
                table: "CyberWarsMatchPlayers");

            migrationBuilder.DropColumn(
                name: "Mode",
                table: "CyberWarsMatches");

            migrationBuilder.DropColumn(
                name: "SectorQ",
                table: "CyberWarsMatches");

            migrationBuilder.DropColumn(
                name: "SectorR",
                table: "CyberWarsMatches");

            migrationBuilder.DropColumn(
                name: "WinnerUserId",
                table: "CyberWarsMatches");
        }
    }
}
