using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ndeipi.Payments.Data.Migrations
{
    /// <inheritdoc />
    public partial class WalletsAndTransfers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Transfers",
                schema: "payments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    IntegratorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    State = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    StateReasonJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SourceWalletId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    SourceUserId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    DestinationType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    DestinationWalletId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    DestinationUserId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Asset = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    ReceiptJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IntegratorReference = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    MetadataJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Transfers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Wallets",
                schema: "payments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    IntegratorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Asset = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Decimals = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PriceRiskAcknowledgedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    MetadataJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Wallets", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LedgerAccounts_IntegratorId_Kind_Bucket_Asset",
                schema: "payments",
                table: "LedgerAccounts",
                columns: new[] { "IntegratorId", "Kind", "Bucket", "Asset" },
                unique: true,
                filter: "[WalletId] IS NULL AND [Provider] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Transfers_DestinationUserId",
                schema: "payments",
                table: "Transfers",
                column: "DestinationUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Transfers_DestinationWalletId",
                schema: "payments",
                table: "Transfers",
                column: "DestinationWalletId");

            migrationBuilder.CreateIndex(
                name: "IX_Transfers_IntegratorId_IdempotencyKey",
                schema: "payments",
                table: "Transfers",
                columns: new[] { "IntegratorId", "IdempotencyKey" },
                unique: true,
                filter: "[IdempotencyKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Transfers_IntegratorId_IntegratorReference",
                schema: "payments",
                table: "Transfers",
                columns: new[] { "IntegratorId", "IntegratorReference" });

            migrationBuilder.CreateIndex(
                name: "IX_Transfers_SourceUserId",
                schema: "payments",
                table: "Transfers",
                column: "SourceUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Transfers_SourceWalletId",
                schema: "payments",
                table: "Transfers",
                column: "SourceWalletId");

            migrationBuilder.CreateIndex(
                name: "IX_Wallets_UserId_Asset",
                schema: "payments",
                table: "Wallets",
                columns: new[] { "UserId", "Asset" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Transfers",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "Wallets",
                schema: "payments");

            migrationBuilder.DropIndex(
                name: "IX_LedgerAccounts_IntegratorId_Kind_Bucket_Asset",
                schema: "payments",
                table: "LedgerAccounts");
        }
    }
}
