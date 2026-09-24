using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ndeipi.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ndeipi");

            migrationBuilder.CreateTable(
                name: "AuthCodes",
                columns: table => new
                {
                    CodeHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClerkSessionId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RedirectUri = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CodeChallenge = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthCodes", x => x.CodeHash);
                });

            migrationBuilder.CreateTable(
                name: "BankTransfers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SenderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(38,6)", precision: 38, scale: 6, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Memo = table.Column<string>(type: "nvarchar(280)", maxLength: 280, nullable: true),
                    BridgeTransferId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ProviderState = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Error = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankTransfers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Conversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    DirectKey = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastActivityAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Conversations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RefreshTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClerkSessionId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefreshTokens", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TokenTransferQueue",
                schema: "ndeipi",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Operation = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Chain = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    TokenStandard = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    TokenSymbol = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ContractAddress = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    TokenId = table.Column<string>(type: "nvarchar(78)", maxLength: 78, nullable: true),
                    Decimals = table.Column<int>(type: "int", nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    SenderUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SenderClerkId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SenderWalletAddress = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    RecipientUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipientClerkId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RecipientWalletAddress = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Memo = table.Column<string>(type: "nvarchar(280)", maxLength: 280, nullable: true),
                    TxHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Error = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    ClaimedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ClaimedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    NotifyPending = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TokenTransferQueue", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClerkUserId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Username = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    AvatarUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ProfileSyncedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WebhookEvents",
                columns: table => new
                {
                    EventId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookEvents", x => x.EventId);
                });

            migrationBuilder.CreateTable(
                name: "BankingProfiles",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BridgeCustomerId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    KycLinkId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    KycStatus = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    TosStatus = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    KycUrl = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    TosUrl = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    WalletId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    WalletChain = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    WalletAddress = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankingProfiles", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_BankingProfiles_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Members",
                columns: table => new
                {
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JoinedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastReadSeq = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Members", x => new { x.ConversationId, x.UserId });
                    table.ForeignKey(
                        name: "FK_Members_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Members_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SenderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    StateJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClientMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Messages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Messages_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Messages_Users_SenderId",
                        column: x => x.SenderId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UserWallets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Chain = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Address = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserWallets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserWallets_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BankingProfiles_BridgeCustomerId",
                table: "BankingProfiles",
                column: "BridgeCustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_BankingProfiles_KycLinkId",
                table: "BankingProfiles",
                column: "KycLinkId");

            migrationBuilder.CreateIndex(
                name: "IX_BankTransfers_BridgeTransferId",
                table: "BankTransfers",
                column: "BridgeTransferId",
                unique: true,
                filter: "[BridgeTransferId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BankTransfers_MessageId",
                table: "BankTransfers",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_BankTransfers_Status",
                table: "BankTransfers",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_DirectKey",
                table: "Conversations",
                column: "DirectKey",
                unique: true,
                filter: "[DirectKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Members_UserId",
                table: "Members",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ConversationId_Seq",
                table: "Messages",
                columns: new[] { "ConversationId", "Seq" });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_SenderId_ClientMessageId",
                table: "Messages",
                columns: new[] { "SenderId", "ClientMessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_ClerkSessionId",
                table: "RefreshTokens",
                column: "ClerkSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_TokenHash",
                table: "RefreshTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TokenTransferQueue_MessageId",
                schema: "ndeipi",
                table: "TokenTransferQueue",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_TokenTransferQueue_NotifyPending",
                schema: "ndeipi",
                table: "TokenTransferQueue",
                column: "NotifyPending",
                filter: "[NotifyPending] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_TokenTransferQueue_Status_CreatedAt",
                schema: "ndeipi",
                table: "TokenTransferQueue",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Users_ClerkUserId",
                table: "Users",
                column: "ClerkUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                table: "Users",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_UserWallets_UserId_Chain",
                table: "UserWallets",
                columns: new[] { "UserId", "Chain" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuthCodes");

            migrationBuilder.DropTable(
                name: "BankingProfiles");

            migrationBuilder.DropTable(
                name: "BankTransfers");

            migrationBuilder.DropTable(
                name: "Members");

            migrationBuilder.DropTable(
                name: "Messages");

            migrationBuilder.DropTable(
                name: "RefreshTokens");

            migrationBuilder.DropTable(
                name: "TokenTransferQueue",
                schema: "ndeipi");

            migrationBuilder.DropTable(
                name: "UserWallets");

            migrationBuilder.DropTable(
                name: "WebhookEvents");

            migrationBuilder.DropTable(
                name: "Conversations");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
