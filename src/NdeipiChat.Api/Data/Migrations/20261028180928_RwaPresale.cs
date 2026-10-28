using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NdeipiChat.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class RwaPresale : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RwaAssets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Location = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    AssetClass = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    TokenStandard = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Highlights = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Bedrooms = table.Column<int>(type: "int", nullable: false),
                    UnitPriceUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    BaseNavUsd = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    CurrentNavUsd = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    GoldWeightGramsPerUnit = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    ConstructionPhase = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    PolygonContractAddress = table.Column<string>(type: "nvarchar(42)", maxLength: 42, nullable: true),
                    NavUpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RwaAssets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RwaAuditEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    HoldingId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Detail = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RwaAuditEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RwaSimAccounts",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BalanceUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RwaSimAccounts", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_RwaSimAccounts_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RwaHoldings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IntervalType = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false),
                    IntervalIndex = table.Column<int>(type: "int", nullable: false),
                    Season = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false),
                    SeasonWeight = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    ShareFraction = table.Column<decimal>(type: "decimal(18,10)", precision: 18, scale: 10, nullable: false),
                    PrincipalUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    PaymentMethod = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    PurchasedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CoolingOffEndsAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CancelledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    VestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    VaultShares = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    PolygonTxHash = table.Column<string>(type: "nvarchar(66)", maxLength: 66, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RwaHoldings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RwaHoldings_RwaAssets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "RwaAssets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RwaHoldings_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RwaWeekSlots",
                columns: table => new
                {
                    AssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Week = table.Column<int>(type: "int", nullable: false),
                    HoldingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RwaWeekSlots", x => new { x.AssetId, x.Week });
                    table.ForeignKey(
                        name: "FK_RwaWeekSlots_RwaAssets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "RwaAssets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RwaWeekSlots_RwaHoldings_HoldingId",
                        column: x => x.HoldingId,
                        principalTable: "RwaHoldings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RwaAssets_Code",
                table: "RwaAssets",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RwaAuditEvents_UserId_At",
                table: "RwaAuditEvents",
                columns: new[] { "UserId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_RwaHoldings_AssetId",
                table: "RwaHoldings",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_RwaHoldings_Status_CoolingOffEndsAt",
                table: "RwaHoldings",
                columns: new[] { "Status", "CoolingOffEndsAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RwaHoldings_TokenId",
                table: "RwaHoldings",
                column: "TokenId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RwaHoldings_UserId_RequestId",
                table: "RwaHoldings",
                columns: new[] { "UserId", "RequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RwaWeekSlots_HoldingId",
                table: "RwaWeekSlots",
                column: "HoldingId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RwaAuditEvents");

            migrationBuilder.DropTable(
                name: "RwaSimAccounts");

            migrationBuilder.DropTable(
                name: "RwaWeekSlots");

            migrationBuilder.DropTable(
                name: "RwaHoldings");

            migrationBuilder.DropTable(
                name: "RwaAssets");
        }
    }
}
