using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NdeipiChat.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class PostCommentsAndReposts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "RepostOfId",
                table: "Posts",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PostComments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PostId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AuthorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PostComments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PostComments_Posts_PostId",
                        column: x => x.PostId,
                        principalTable: "Posts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PostComments_Users_AuthorId",
                        column: x => x.AuthorId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Posts_AuthorId_RepostOfId",
                table: "Posts",
                columns: new[] { "AuthorId", "RepostOfId" },
                unique: true,
                filter: "[RepostOfId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Posts_RepostOfId",
                table: "Posts",
                column: "RepostOfId");

            migrationBuilder.CreateIndex(
                name: "IX_PostComments_AuthorId_CreatedAt",
                table: "PostComments",
                columns: new[] { "AuthorId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PostComments_PostId_CreatedAt",
                table: "PostComments",
                columns: new[] { "PostId", "CreatedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_Posts_Posts_RepostOfId",
                table: "Posts",
                column: "RepostOfId",
                principalTable: "Posts",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Posts_Posts_RepostOfId",
                table: "Posts");

            migrationBuilder.DropTable(
                name: "PostComments");

            migrationBuilder.DropIndex(
                name: "IX_Posts_AuthorId_RepostOfId",
                table: "Posts");

            migrationBuilder.DropIndex(
                name: "IX_Posts_RepostOfId",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "RepostOfId",
                table: "Posts");
        }
    }
}
