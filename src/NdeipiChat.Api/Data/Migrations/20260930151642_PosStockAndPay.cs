using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NdeipiChat.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class PosStockAndPay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "TrackStock",
                table: "PosProducts",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "PosQrPayments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StoreId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StaffId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Error = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    PayerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BankTransferId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SaleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PosQrPayments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PosQrPayments_PosStores_StoreId",
                        column: x => x.StoreId,
                        principalTable: "PosStores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Stock used to be allowed below zero. It can't be now: what's below zero is none.
            migrationBuilder.Sql("UPDATE [PosStock] SET [Quantity] = 0 WHERE [Quantity] < 0;");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PosStock_NotNegative",
                table: "PosStock",
                sql: "[Quantity] >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_PosQrPayments_BankTransferId",
                table: "PosQrPayments",
                column: "BankTransferId",
                filter: "[BankTransferId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PosQrPayments_Code",
                table: "PosQrPayments",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PosQrPayments_SaleId",
                table: "PosQrPayments",
                column: "SaleId",
                unique: true,
                filter: "[SaleId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PosQrPayments_StoreId_CreatedAt",
                table: "PosQrPayments",
                columns: new[] { "StoreId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PosQrPayments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PosStock_NotNegative",
                table: "PosStock");

            migrationBuilder.DropColumn(
                name: "TrackStock",
                table: "PosProducts");
        }
    }
}
