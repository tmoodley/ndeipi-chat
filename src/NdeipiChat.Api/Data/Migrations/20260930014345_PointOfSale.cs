using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NdeipiChat.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class PointOfSale : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PosAudit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    StoreId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StaffId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StaffName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Terminal = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Details = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    PreviousHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PosAudit", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PosCashMovements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ShiftId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StaffId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PosCashMovements", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PosMerchants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    TaxInclusive = table.Column<bool>(type: "bit", nullable: false),
                    DiscountLimitPercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    LockSeconds = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PosMerchants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PosSales",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StoreId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ShiftId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StaffId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientSaleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReceiptNumber = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    StatusReason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Subtotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Discount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Tax = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Total = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Change = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Offline = table.Column<bool>(type: "bit", nullable: false),
                    ReversedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReversedShiftId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PosSales", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PosShifts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StoreId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StaffId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OpenedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    OpeningFloat = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    ClosedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CountedCash = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PosShifts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PosTillSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    StoreId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StaffId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Terminal = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PosTillSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PosCategories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Icon = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Tone = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PosCategories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PosCategories_PosMerchants_MerchantId",
                        column: x => x.MerchantId,
                        principalTable: "PosMerchants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PosProducts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Icon = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Sku = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Barcode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Price = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    TaxRatePercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    SafetyStock = table.Column<int>(type: "int", nullable: false),
                    VariantsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ModifiersJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PosProducts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PosProducts_PosMerchants_MerchantId",
                        column: x => x.MerchantId,
                        principalTable: "PosMerchants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PosStaff",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Role = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    StoreId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PinHash = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: true),
                    PinSalt = table.Column<byte[]>(type: "varbinary(16)", maxLength: 16, nullable: true),
                    AddedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PosStaff", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PosStaff_PosMerchants_MerchantId",
                        column: x => x.MerchantId,
                        principalTable: "PosMerchants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PosStaff_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PosStores",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Address = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    TaxRatePercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    ReceiptCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PosStores", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PosStores_PosMerchants_MerchantId",
                        column: x => x.MerchantId,
                        principalTable: "PosMerchants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PosPayments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SaleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Tender = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PosPayments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PosPayments_PosSales_SaleId",
                        column: x => x.SaleId,
                        principalTable: "PosSales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PosSaleLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SaleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Variant = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Modifiers = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Discount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    TaxRatePercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    Tax = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Total = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PosSaleLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PosSaleLines_PosSales_SaleId",
                        column: x => x.SaleId,
                        principalTable: "PosSales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PosStock",
                columns: table => new
                {
                    StoreId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Variant = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PosStock", x => new { x.StoreId, x.ProductId, x.Variant });
                    table.ForeignKey(
                        name: "FK_PosStock_PosProducts_ProductId",
                        column: x => x.ProductId,
                        principalTable: "PosProducts",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_PosStock_PosStores_StoreId",
                        column: x => x.StoreId,
                        principalTable: "PosStores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PosAudit_MerchantId_Sequence",
                table: "PosAudit",
                columns: new[] { "MerchantId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PosCashMovements_ShiftId",
                table: "PosCashMovements",
                column: "ShiftId");

            migrationBuilder.CreateIndex(
                name: "IX_PosCategories_MerchantId",
                table: "PosCategories",
                column: "MerchantId");

            migrationBuilder.CreateIndex(
                name: "IX_PosMerchants_OwnerId",
                table: "PosMerchants",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_PosPayments_SaleId",
                table: "PosPayments",
                column: "SaleId");

            migrationBuilder.CreateIndex(
                name: "IX_PosProducts_MerchantId_Barcode",
                table: "PosProducts",
                columns: new[] { "MerchantId", "Barcode" });

            migrationBuilder.CreateIndex(
                name: "IX_PosProducts_MerchantId_Sku",
                table: "PosProducts",
                columns: new[] { "MerchantId", "Sku" });

            migrationBuilder.CreateIndex(
                name: "IX_PosSaleLines_SaleId",
                table: "PosSaleLines",
                column: "SaleId");

            migrationBuilder.CreateIndex(
                name: "IX_PosSales_MerchantId_ClientSaleId",
                table: "PosSales",
                columns: new[] { "MerchantId", "ClientSaleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PosSales_ShiftId",
                table: "PosSales",
                column: "ShiftId");

            migrationBuilder.CreateIndex(
                name: "IX_PosSales_StoreId_OccurredAt",
                table: "PosSales",
                columns: new[] { "StoreId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PosShifts_StaffId_ClosedAt",
                table: "PosShifts",
                columns: new[] { "StaffId", "ClosedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PosShifts_StoreId_OpenedAt",
                table: "PosShifts",
                columns: new[] { "StoreId", "OpenedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PosStaff_MerchantId_UserId",
                table: "PosStaff",
                columns: new[] { "MerchantId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PosStaff_UserId",
                table: "PosStaff",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_PosStock_ProductId",
                table: "PosStock",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_PosStores_MerchantId",
                table: "PosStores",
                column: "MerchantId");

            migrationBuilder.CreateIndex(
                name: "IX_PosTillSessions_ExpiresAt",
                table: "PosTillSessions",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_PosTillSessions_TokenHash",
                table: "PosTillSessions",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PosAudit");

            migrationBuilder.DropTable(
                name: "PosCashMovements");

            migrationBuilder.DropTable(
                name: "PosCategories");

            migrationBuilder.DropTable(
                name: "PosPayments");

            migrationBuilder.DropTable(
                name: "PosSaleLines");

            migrationBuilder.DropTable(
                name: "PosShifts");

            migrationBuilder.DropTable(
                name: "PosStaff");

            migrationBuilder.DropTable(
                name: "PosStock");

            migrationBuilder.DropTable(
                name: "PosTillSessions");

            migrationBuilder.DropTable(
                name: "PosSales");

            migrationBuilder.DropTable(
                name: "PosProducts");

            migrationBuilder.DropTable(
                name: "PosStores");

            migrationBuilder.DropTable(
                name: "PosMerchants");
        }
    }
}
