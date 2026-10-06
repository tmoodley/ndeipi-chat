using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ndeipi.Payments.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "payments");

            migrationBuilder.CreateTable(
                name: "AuditLog",
                schema: "payments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    IntegratorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ApiKeyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Operator = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RequestId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Method = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Path = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    StatusCode = table.Column<int>(type: "int", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Events",
                schema: "payments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    IntegratorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ObjectType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ObjectId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ObjectVersion = table.Column<int>(type: "int", nullable: false),
                    DataJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PreviousAttributesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IdempotencyRecords",
                schema: "payments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    IntegratorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    RequestHash = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    ResponseStatus = table.Column<int>(type: "int", nullable: true),
                    ResponseBody = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdempotencyRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Integrators",
                schema: "payments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Integrators", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LedgerAccounts",
                schema: "payments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    IntegratorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Bucket = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    WalletId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Provider = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Asset = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Balance = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    AllowNegative = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LedgerAccounts", x => x.Id);
                    table.CheckConstraint("CK_LedgerAccounts_NonNegative", "[AllowNegative] = 1 OR [Balance] >= 0");
                });

            migrationBuilder.CreateTable(
                name: "OtcTrades",
                schema: "payments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Side = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    CoinAmount = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    UsdAmount = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    UsdPerCoin = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    AbsaReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DeskReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RecordedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ExecutedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OtcTrades", x => x.Id);
                    table.CheckConstraint("CK_OtcTrades_Positive", "[CoinAmount] > 0 AND [UsdAmount] > 0 AND [UsdPerCoin] > 0");
                });

            migrationBuilder.CreateTable(
                name: "Postings",
                schema: "payments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    IntegratorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TransferId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    RequestId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    ApiKeyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    ReversesPostingId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Postings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                schema: "payments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    IntegratorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ExternalReference = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    FirstName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    LastName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    BusinessName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Country = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    KycStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    TermsStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RejectionReasonsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    MetadataJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApiKeys",
                schema: "payments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IntegratorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Environment = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    KeyHash = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    Last4 = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApiKeys_Integrators_IntegratorId",
                        column: x => x.IntegratorId,
                        principalSchema: "payments",
                        principalTable: "Integrators",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PostingLines",
                schema: "payments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PostingId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    AccountId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Asset = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    BalanceAfter = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PostingLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PostingLines_LedgerAccounts_AccountId",
                        column: x => x.AccountId,
                        principalSchema: "payments",
                        principalTable: "LedgerAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PostingLines_Postings_PostingId",
                        column: x => x.PostingId,
                        principalSchema: "payments",
                        principalTable: "Postings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_IntegratorId",
                schema: "payments",
                table: "ApiKeys",
                column: "IntegratorId");

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_KeyHash",
                schema: "payments",
                table: "ApiKeys",
                column: "KeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_IntegratorId_CreatedAt",
                schema: "payments",
                table: "AuditLog",
                columns: new[] { "IntegratorId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Events_IntegratorId_CreatedAt",
                schema: "payments",
                table: "Events",
                columns: new[] { "IntegratorId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Events_ObjectId",
                schema: "payments",
                table: "Events",
                column: "ObjectId");

            migrationBuilder.CreateIndex(
                name: "IX_IdempotencyRecords_ExpiresAt",
                schema: "payments",
                table: "IdempotencyRecords",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_IdempotencyRecords_IntegratorId_Key",
                schema: "payments",
                table: "IdempotencyRecords",
                columns: new[] { "IntegratorId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LedgerAccounts_Kind_Provider_Asset",
                schema: "payments",
                table: "LedgerAccounts",
                columns: new[] { "Kind", "Provider", "Asset" });

            migrationBuilder.CreateIndex(
                name: "IX_LedgerAccounts_WalletId_Bucket",
                schema: "payments",
                table: "LedgerAccounts",
                columns: new[] { "WalletId", "Bucket" },
                unique: true,
                filter: "[WalletId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OtcTrades_DeskReference",
                schema: "payments",
                table: "OtcTrades",
                column: "DeskReference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OtcTrades_ExecutedAt",
                schema: "payments",
                table: "OtcTrades",
                column: "ExecutedAt");

            migrationBuilder.CreateIndex(
                name: "IX_PostingLines_AccountId_Id",
                schema: "payments",
                table: "PostingLines",
                columns: new[] { "AccountId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_PostingLines_PostingId",
                schema: "payments",
                table: "PostingLines",
                column: "PostingId");

            migrationBuilder.CreateIndex(
                name: "IX_Postings_IntegratorId_IdempotencyKey",
                schema: "payments",
                table: "Postings",
                columns: new[] { "IntegratorId", "IdempotencyKey" },
                unique: true,
                filter: "[IdempotencyKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Postings_ReversesPostingId",
                schema: "payments",
                table: "Postings",
                column: "ReversesPostingId",
                unique: true,
                filter: "[ReversesPostingId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Postings_TransferId",
                schema: "payments",
                table: "Postings",
                column: "TransferId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_IntegratorId_ExternalReference",
                schema: "payments",
                table: "Users",
                columns: new[] { "IntegratorId", "ExternalReference" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApiKeys",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "AuditLog",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "Events",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "IdempotencyRecords",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "OtcTrades",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "PostingLines",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "Users",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "Integrators",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "LedgerAccounts",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "Postings",
                schema: "payments");
        }
    }
}
