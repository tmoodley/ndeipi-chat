using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ndeipi.Payments.Data.Migrations
{
    /// <inheritdoc />
    public partial class RampsAndConversions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DepositAccountId",
                schema: "payments",
                table: "Transfers",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DepositInstructionsJson",
                schema: "payments",
                table: "Transfers",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DestinationAsset",
                schema: "payments",
                table: "Transfers",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExpiresAt",
                schema: "payments",
                table: "Transfers",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PayoutAccountId",
                schema: "payments",
                table: "Transfers",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderReference",
                schema: "payments",
                table: "Transfers",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QuoteId",
                schema: "payments",
                table: "Transfers",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Rail",
                schema: "payments",
                table: "Transfers",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DepositAccounts",
                schema: "payments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    IntegratorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    WalletId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    Rail = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ProviderReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    InstructionsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    MetadataJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DepositAccounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Deposits",
                schema: "payments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    IntegratorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DepositAccountId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    TransferId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    AmountReceived = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    RailReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Deposits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PayoutAccounts",
                schema: "payments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    IntegratorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Type = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    Country = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: false),
                    Rail = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    AccountOwnerName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ProtectedDetails = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    MaskedJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    MetadataJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayoutAccounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Quotes",
                schema: "payments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    IntegratorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    SourceWalletId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    DestinationWalletId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    From = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    To = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    Fee = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    AmountOut = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    Rate = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    TransferId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Quotes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Transfers_Kind_State",
                schema: "payments",
                table: "Transfers",
                columns: new[] { "Kind", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_Transfers_ProviderReference",
                schema: "payments",
                table: "Transfers",
                column: "ProviderReference");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerAccounts_IntegratorId_Provider_Asset",
                schema: "payments",
                table: "LedgerAccounts",
                columns: new[] { "IntegratorId", "Provider", "Asset" },
                unique: true,
                filter: "[Provider] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DepositAccounts_Reference",
                schema: "payments",
                table: "DepositAccounts",
                column: "Reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DepositAccounts_UserId",
                schema: "payments",
                table: "DepositAccounts",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Deposits_DepositAccountId",
                schema: "payments",
                table: "Deposits",
                column: "DepositAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_PayoutAccounts_UserId",
                schema: "payments",
                table: "PayoutAccounts",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DepositAccounts",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "Deposits",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "PayoutAccounts",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "Quotes",
                schema: "payments");

            migrationBuilder.DropIndex(
                name: "IX_Transfers_Kind_State",
                schema: "payments",
                table: "Transfers");

            migrationBuilder.DropIndex(
                name: "IX_Transfers_ProviderReference",
                schema: "payments",
                table: "Transfers");

            migrationBuilder.DropIndex(
                name: "IX_LedgerAccounts_IntegratorId_Provider_Asset",
                schema: "payments",
                table: "LedgerAccounts");

            migrationBuilder.DropColumn(
                name: "DepositAccountId",
                schema: "payments",
                table: "Transfers");

            migrationBuilder.DropColumn(
                name: "DepositInstructionsJson",
                schema: "payments",
                table: "Transfers");

            migrationBuilder.DropColumn(
                name: "DestinationAsset",
                schema: "payments",
                table: "Transfers");

            migrationBuilder.DropColumn(
                name: "ExpiresAt",
                schema: "payments",
                table: "Transfers");

            migrationBuilder.DropColumn(
                name: "PayoutAccountId",
                schema: "payments",
                table: "Transfers");

            migrationBuilder.DropColumn(
                name: "ProviderReference",
                schema: "payments",
                table: "Transfers");

            migrationBuilder.DropColumn(
                name: "QuoteId",
                schema: "payments",
                table: "Transfers");

            migrationBuilder.DropColumn(
                name: "Rail",
                schema: "payments",
                table: "Transfers");
        }
    }
}
