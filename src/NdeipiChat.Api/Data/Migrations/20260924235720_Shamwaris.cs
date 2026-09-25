using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NdeipiChat.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Shamwaris : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EmailVerified",
                table: "Users",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Phone",
                table: "Users",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Shamwaris",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequesterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AddresseeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    InviteContact = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    PairKey = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    Accepted = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Shamwaris", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Shamwaris_Users_AddresseeId",
                        column: x => x.AddresseeId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Shamwaris_Users_RequesterId",
                        column: x => x.RequesterId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Users_Phone",
                table: "Users",
                column: "Phone");

            migrationBuilder.CreateIndex(
                name: "IX_Shamwaris_AddresseeId",
                table: "Shamwaris",
                column: "AddresseeId");

            migrationBuilder.CreateIndex(
                name: "IX_Shamwaris_InviteContact",
                table: "Shamwaris",
                column: "InviteContact",
                filter: "[InviteContact] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Shamwaris_PairKey",
                table: "Shamwaris",
                column: "PairKey",
                unique: true,
                filter: "[PairKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Shamwaris_RequesterId_InviteContact",
                table: "Shamwaris",
                columns: new[] { "RequesterId", "InviteContact" },
                unique: true,
                filter: "[InviteContact] IS NOT NULL");

            // Existing users re-sync their profile on their next request, picking up whether their
            // email is verified and their phone number.
            migrationBuilder.Sql("UPDATE [Users] SET [ProfileSyncedAt] = '0001-01-01T00:00:00+00:00'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Shamwaris");

            migrationBuilder.DropIndex(
                name: "IX_Users_Phone",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "EmailVerified",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Phone",
                table: "Users");
        }
    }
}
