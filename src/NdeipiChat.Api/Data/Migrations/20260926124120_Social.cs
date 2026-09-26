using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NdeipiChat.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Social : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "MessageId",
                schema: "ndeipi",
                table: "TokenTransferQueue",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AlterColumn<Guid>(
                name: "ConversationId",
                schema: "ndeipi",
                table: "TokenTransferQueue",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<string>(
                name: "MetadataUri",
                schema: "ndeipi",
                table: "TokenTransferQueue",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PostId",
                schema: "ndeipi",
                table: "TokenTransferQueue",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Posts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AuthorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Caption = table.Column<string>(type: "nvarchar(2200)", maxLength: 2200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    MintTransferId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Posts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Posts_TokenTransferQueue_MintTransferId",
                        column: x => x.MintTransferId,
                        principalSchema: "ndeipi",
                        principalTable: "TokenTransferQueue",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Posts_Users_AuthorId",
                        column: x => x.AuthorId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PostLikes",
                columns: table => new
                {
                    PostId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PostLikes", x => new { x.PostId, x.UserId });
                    table.ForeignKey(
                        name: "FK_PostLikes_Posts_PostId",
                        column: x => x.PostId,
                        principalTable: "Posts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PostLikes_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PostMedia",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PostId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false),
                    Width = table.Column<int>(type: "int", nullable: false),
                    Height = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PostMedia", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PostMedia_Posts_PostId",
                        column: x => x.PostId,
                        principalTable: "Posts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TokenTransferQueue_PostId",
                schema: "ndeipi",
                table: "TokenTransferQueue",
                column: "PostId",
                filter: "[PostId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PostLikes_UserId",
                table: "PostLikes",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_PostMedia_PostId_Position",
                table: "PostMedia",
                columns: new[] { "PostId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Posts_AuthorId_CreatedAt",
                table: "Posts",
                columns: new[] { "AuthorId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Posts_CreatedAt_Id",
                table: "Posts",
                columns: new[] { "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Posts_MintTransferId",
                table: "Posts",
                column: "MintTransferId");

            // Workers need the new columns for a mint. See docs/ndeipi-queue.md.
            migrationBuilder.Sql(ClaimProcedure(includeMintColumns: true));
        }

        /// <summary>usp_ClaimTokenTransfers, as TokenQueueContract created it, optionally returning PostId and MetadataUri.</summary>
        static string ClaimProcedure(bool includeMintColumns) => $"""
            CREATE OR ALTER PROCEDURE ndeipi.usp_ClaimTokenTransfers
                @WorkerId nvarchar(100),
                @BatchSize int = 10
            AS
            BEGIN
                SET NOCOUNT ON;
                DECLARE @claimed TABLE (Id uniqueidentifier PRIMARY KEY);

                WITH next AS (
                    SELECT TOP (@BatchSize) *
                      FROM ndeipi.TokenTransferQueue WITH (ROWLOCK, UPDLOCK, READPAST)
                     WHERE Status = N'Pending'
                     ORDER BY CreatedAt
                )
                UPDATE next
                   SET Status = N'Processing',
                       ClaimedBy = @WorkerId,
                       ClaimedAt = SYSUTCDATETIME(),
                       UpdatedAt = SYSUTCDATETIME(),
                       Attempts = Attempts + 1
                OUTPUT inserted.Id INTO @claimed;

                SELECT q.Id, q.Operation, q.Status, q.Chain, q.TokenStandard, q.TokenSymbol, q.ContractAddress,
                       q.TokenId, q.Decimals, q.Amount,
                       q.SenderUserId, q.SenderClerkId, q.SenderWalletAddress,
                       q.RecipientUserId, q.RecipientClerkId, q.RecipientWalletAddress,
                       q.ConversationId, q.MessageId, q.Memo,{(includeMintColumns ? " q.PostId, q.MetadataUri," : "")} q.Attempts, q.ClaimedBy, q.ClaimedAt, q.CreatedAt
                  FROM ndeipi.TokenTransferQueue q
                  JOIN @claimed c ON c.Id = q.Id
                 ORDER BY q.CreatedAt;
            END
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(ClaimProcedure(includeMintColumns: false));

            migrationBuilder.DropTable(
                name: "PostLikes");

            migrationBuilder.DropTable(
                name: "PostMedia");

            migrationBuilder.DropTable(
                name: "Posts");

            migrationBuilder.DropIndex(
                name: "IX_TokenTransferQueue_PostId",
                schema: "ndeipi",
                table: "TokenTransferQueue");

            migrationBuilder.DropColumn(
                name: "MetadataUri",
                schema: "ndeipi",
                table: "TokenTransferQueue");

            migrationBuilder.DropColumn(
                name: "PostId",
                schema: "ndeipi",
                table: "TokenTransferQueue");

            migrationBuilder.AlterColumn<Guid>(
                name: "MessageId",
                schema: "ndeipi",
                table: "TokenTransferQueue",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "ConversationId",
                schema: "ndeipi",
                table: "TokenTransferQueue",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);
        }
    }
}
