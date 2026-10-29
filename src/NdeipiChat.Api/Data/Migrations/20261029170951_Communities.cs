using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NdeipiChat.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Communities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastSeenAt",
                table: "Users",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MutedUntil",
                table: "Members",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Role",
                table: "Members",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "member");

            migrationBuilder.AddColumn<Guid>(
                name: "CardMessageId",
                table: "CyberWarsStrikes",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ChatGroups",
                columns: table => new
                {
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Icon = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    AppId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    AppRef = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatGroups", x => x.ConversationId);
                    table.ForeignKey(
                        name: "FK_ChatGroups_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChatGroups_AppId_AppRef",
                table: "ChatGroups",
                columns: new[] { "AppId", "AppRef" });

            // Every existing group becomes a community: war rooms belong to their alliance and are
            // owned by its leader; any other group is owned by its longest-standing member.
            migrationBuilder.Sql("""
                INSERT INTO ChatGroups (ConversationId, Kind, Icon, AppId, AppRef, CreatedAt)
                SELECT a.ConversationId, 'alliance', N'⚔️', 'cyberwars', a.Id, a.CreatedAt
                FROM CyberWarsAlliances a
                WHERE EXISTS (SELECT 1 FROM Conversations c WHERE c.Id = a.ConversationId);

                INSERT INTO ChatGroups (ConversationId, Kind, Icon, CreatedAt)
                SELECT c.Id, 'group', N'👥', c.CreatedAt
                FROM Conversations c
                WHERE c.Type = 'Group' AND NOT EXISTS (SELECT 1 FROM ChatGroups g WHERE g.ConversationId = c.Id);

                UPDATE m SET Role = 'owner'
                FROM Members m
                JOIN CyberWarsAlliances a ON a.ConversationId = m.ConversationId AND a.LeaderId = m.UserId;

                UPDATE m SET Role = 'owner'
                FROM Members m
                JOIN ChatGroups g ON g.ConversationId = m.ConversationId AND g.Kind = 'group'
                WHERE m.UserId = (SELECT TOP 1 x.UserId FROM Members x WHERE x.ConversationId = m.ConversationId ORDER BY x.JoinedAt, x.UserId);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChatGroups");

            migrationBuilder.DropColumn(
                name: "LastSeenAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "MutedUntil",
                table: "Members");

            migrationBuilder.DropColumn(
                name: "Role",
                table: "Members");

            migrationBuilder.DropColumn(
                name: "CardMessageId",
                table: "CyberWarsStrikes");
        }
    }
}
