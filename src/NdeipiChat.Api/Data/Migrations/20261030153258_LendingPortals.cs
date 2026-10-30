using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NdeipiChat.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class LendingPortals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LendingAuditEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DealId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Before = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    After = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LendingAuditEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LendingDeals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Location = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    State = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    OfferingBasis = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    RwaAssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SeniorTargetUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    JuniorTargetUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    SeniorAprBps = table.Column<int>(type: "int", nullable: false),
                    JuniorAprBps = table.Column<int>(type: "int", nullable: false),
                    FeesAprBps = table.Column<int>(type: "int", nullable: false),
                    MaxLtvBps = table.Column<int>(type: "int", nullable: false),
                    BrokerFeeBps = table.Column<int>(type: "int", nullable: false),
                    PlatformFeeBps = table.Column<int>(type: "int", nullable: false),
                    ReserveTargetUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    ScheduledSeniorPrincipalUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    MaxHoldingUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    BlockedJurisdictions = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SeniorRedeemedUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    JuniorRedeemedUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReturnReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SeniorFundedUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    JuniorFundedUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    SeniorOutUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    JuniorOutUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    SeniorDueUsd = table.Column<decimal>(type: "decimal(24,10)", precision: 24, scale: 10, nullable: false),
                    JuniorDueUsd = table.Column<decimal>(type: "decimal(24,10)", precision: 24, scale: 10, nullable: false),
                    FeesDueUsd = table.Column<decimal>(type: "decimal(24,10)", precision: 24, scale: 10, nullable: false),
                    SeniorInterestPaidUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    JuniorInterestPaidUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    SeniorPrincipalPaidUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    JuniorPrincipalPaidUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    SeniorLossUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    JuniorLossUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    ReserveUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    ValuationUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    ValuedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CollateralLocked = table.Column<bool>(type: "bit", nullable: false),
                    CollateralFrozen = table.Column<bool>(type: "bit", nullable: false),
                    LandChargeRegistered = table.Column<bool>(type: "bit", nullable: false),
                    BrokerFeeUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AccruedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LendingDeals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LendingDeals_RwaAssets_RwaAssetId",
                        column: x => x.RwaAssetId,
                        principalTable: "RwaAssets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LendingDeposits",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DealId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Tranche = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    AmountUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LendingDeposits", x => new { x.UserId, x.RequestId });
                    table.ForeignKey(
                        name: "FK_LendingDeposits_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LendingNotices",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DealId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Text = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReadAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LendingNotices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LendingProfiles",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvestorClass = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Jurisdiction = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: false),
                    KybApproved = table.Column<bool>(type: "bit", nullable: false),
                    Frozen = table.Column<bool>(type: "bit", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LendingProfiles", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_LendingProfiles_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LendingAttestations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DealId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    AttestorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReportSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ValueUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    DrawNumber = table.Column<int>(type: "int", nullable: true),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LendingAttestations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LendingAttestations_LendingDeals_DealId",
                        column: x => x.DealId,
                        principalTable: "LendingDeals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LendingDealRoles",
                columns: table => new
                {
                    DealId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Role = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LendingDealRoles", x => new { x.DealId, x.Role });
                    table.ForeignKey(
                        name: "FK_LendingDealRoles_LendingDeals_DealId",
                        column: x => x.DealId,
                        principalTable: "LendingDeals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LendingDealRoles_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LendingDefaultVotes",
                columns: table => new
                {
                    DealId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Role = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LendingDefaultVotes", x => new { x.DealId, x.Role });
                    table.ForeignKey(
                        name: "FK_LendingDefaultVotes_LendingDeals_DealId",
                        column: x => x.DealId,
                        principalTable: "LendingDeals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LendingDocuments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DealId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    UploadedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LendingDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LendingDocuments_LendingDeals_DealId",
                        column: x => x.DealId,
                        principalTable: "LendingDeals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LendingDraws",
                columns: table => new
                {
                    DealId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<int>(type: "int", nullable: false),
                    AmountUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Milestone = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    DrawnAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    BrokerFeeUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    PlatformFeeUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    NetUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LendingDraws", x => new { x.DealId, x.Number });
                    table.ForeignKey(
                        name: "FK_LendingDraws_LendingDeals_DealId",
                        column: x => x.DealId,
                        principalTable: "LendingDeals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LendingPayments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DealId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AmountUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Fees = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    SeniorInterest = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    SeniorPrincipal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    JuniorInterest = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    SeniorPrincipalSweep = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    JuniorPrincipal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Reserve = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Residual = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    RecordedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LendingPayments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LendingPayments_LendingDeals_DealId",
                        column: x => x.DealId,
                        principalTable: "LendingDeals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LendingPositions",
                columns: table => new
                {
                    DealId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Tranche = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    DepositedUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    RedeemedUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    InterestPaidUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    PrincipalPaidUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    LossUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LendingPositions", x => new { x.DealId, x.UserId, x.Tranche });
                    table.ForeignKey(
                        name: "FK_LendingPositions_LendingDeals_DealId",
                        column: x => x.DealId,
                        principalTable: "LendingDeals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LendingPositions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LendingRedemptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DealId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Tranche = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    AmountUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    WindowOpensAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    WindowClosesAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RedeemedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LendingRedemptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LendingRedemptions_LendingDeals_DealId",
                        column: x => x.DealId,
                        principalTable: "LendingDeals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LendingRedemptions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LendingAttestations_DealId_Kind",
                table: "LendingAttestations",
                columns: new[] { "DealId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_LendingAuditEvents_DealId_At",
                table: "LendingAuditEvents",
                columns: new[] { "DealId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_LendingDealRoles_UserId",
                table: "LendingDealRoles",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_LendingDeals_Code",
                table: "LendingDeals",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LendingDeals_RwaAssetId",
                table: "LendingDeals",
                column: "RwaAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_LendingDeals_State",
                table: "LendingDeals",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_LendingDocuments_DealId_Name_Version",
                table: "LendingDocuments",
                columns: new[] { "DealId", "Name", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LendingNotices_UserId_At",
                table: "LendingNotices",
                columns: new[] { "UserId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_LendingPayments_DealId_Reference",
                table: "LendingPayments",
                columns: new[] { "DealId", "Reference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LendingPositions_UserId",
                table: "LendingPositions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_LendingRedemptions_DealId_UserId",
                table: "LendingRedemptions",
                columns: new[] { "DealId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_LendingRedemptions_UserId",
                table: "LendingRedemptions",
                column: "UserId");

            // PLT-AUD-01: the audit log can only be added to, whoever is connected.
            migrationBuilder.Sql("""
                CREATE TRIGGER [TR_LendingAuditEvents_AppendOnly] ON [LendingAuditEvents] INSTEAD OF UPDATE, DELETE AS
                BEGIN
                    THROW 51000, 'LendingAuditEvents is append-only.', 1;
                END
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LendingAttestations");

            migrationBuilder.DropTable(
                name: "LendingAuditEvents");

            migrationBuilder.DropTable(
                name: "LendingDealRoles");

            migrationBuilder.DropTable(
                name: "LendingDefaultVotes");

            migrationBuilder.DropTable(
                name: "LendingDeposits");

            migrationBuilder.DropTable(
                name: "LendingDocuments");

            migrationBuilder.DropTable(
                name: "LendingDraws");

            migrationBuilder.DropTable(
                name: "LendingNotices");

            migrationBuilder.DropTable(
                name: "LendingPayments");

            migrationBuilder.DropTable(
                name: "LendingPositions");

            migrationBuilder.DropTable(
                name: "LendingProfiles");

            migrationBuilder.DropTable(
                name: "LendingRedemptions");

            migrationBuilder.DropTable(
                name: "LendingDeals");
        }
    }
}
