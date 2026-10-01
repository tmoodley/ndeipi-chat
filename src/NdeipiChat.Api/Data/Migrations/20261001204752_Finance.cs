using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace NdeipiChat.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Finance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FinanceConstituencies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Province = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinanceConstituencies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LoanApplications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ApplicantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Stage = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    ZambianOwned = table.Column<bool>(type: "bit", nullable: true),
                    RegistrationBody = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: true),
                    LicenceType = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: true),
                    HasBankAccount = table.Column<bool>(type: "bit", nullable: true),
                    GroupName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    GroupType = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: true),
                    RegistrationNumber = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    Province = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    Constituency = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    Ward = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    Village = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    ClusterType = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    RunningMonths = table.Column<int>(type: "int", nullable: false),
                    EstimateTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Declared = table.Column<bool>(type: "bit", nullable: false),
                    CollectionReference = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoanApplications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LoanApplications_Users_ApplicantId",
                        column: x => x.ApplicantId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LoanCommittees",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Stage = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Province = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Constituency = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    Ward = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    Village = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoanCommittees", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LoanEquipment",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    ClusterType = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Mtp = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: true),
                    HirePerMonth = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    BuyPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    RunningPerMonth = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    Active = table.Column<bool>(type: "bit", nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoanEquipment", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LoanApplicationItems",
                columns: table => new
                {
                    ApplicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EquipmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Buy = table.Column<bool>(type: "bit", nullable: false),
                    HirePerMonth = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    BuyPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    RunningPerMonth = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoanApplicationItems", x => new { x.ApplicationId, x.EquipmentId });
                    table.ForeignKey(
                        name: "FK_LoanApplicationItems_LoanApplications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "LoanApplications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LoanDecisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Stage = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Decision = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    DeciderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CommitteeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Comment = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoanDecisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LoanDecisions_LoanApplications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "LoanApplications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LoanDecisions_Users_DeciderId",
                        column: x => x.DeciderId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LoanDocuments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    StoredAs = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    UploadedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoanDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LoanDocuments_LoanApplications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "LoanApplications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LoanCommitteeMembers",
                columns: table => new
                {
                    CommitteeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoanCommitteeMembers", x => new { x.CommitteeId, x.UserId });
                    table.ForeignKey(
                        name: "FK_LoanCommitteeMembers_LoanCommittees_CommitteeId",
                        column: x => x.CommitteeId,
                        principalTable: "LoanCommittees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LoanCommitteeMembers_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "LoanEquipment",
                columns: new[] { "Id", "Active", "BuyPrice", "ClusterType", "Description", "HirePerMonth", "Mtp", "Name", "Order", "RunningPerMonth" },
                values: new object[,]
                {
                    { new Guid("6a0f1c52-3f7e-4d0b-9a51-1d6f0c2b7a01"), true, null, "processing", "Primary crushing of hard rock ore", null, "MTP 1", "Jaw crusher", 1, null },
                    { new Guid("6a0f1c52-3f7e-4d0b-9a51-1d6f0c2b7a02"), true, null, "processing", "Milling crushed ore to fine particles", null, "MTP 2", "Hammer mill", 2, null },
                    { new Guid("6a0f1c52-3f7e-4d0b-9a51-1d6f0c2b7a03"), true, null, "processing", "Centrifugal concentrator for coarse and fine gold; 1–3 t/h, 2–4 m³ water/h", null, "MTP (TBC)", "Gold kacha", 3, null },
                    { new Guid("6a0f1c52-3f7e-4d0b-9a51-1d6f0c2b7a04"), true, null, "processing", "Final clean-up to smeltable concentrate", null, "MTP (TBC)", "Shaking table", 4, null },
                    { new Guid("6a0f1c52-3f7e-4d0b-9a51-1d6f0c2b7a05"), true, null, "processing", "Portable recovery from gravel and river sand", null, "MTP (TBC)", "Sluice box", 5, null }
                });

            migrationBuilder.CreateIndex(
                name: "IX_FinanceConstituencies_Province_Name",
                table: "FinanceConstituencies",
                columns: new[] { "Province", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LoanApplications_ApplicantId_Status",
                table: "LoanApplications",
                columns: new[] { "ApplicantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_LoanApplications_Reference",
                table: "LoanApplications",
                column: "Reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LoanApplications_RegistrationNumber",
                table: "LoanApplications",
                column: "RegistrationNumber");

            migrationBuilder.CreateIndex(
                name: "IX_LoanApplications_Status_Stage",
                table: "LoanApplications",
                columns: new[] { "Status", "Stage" });

            migrationBuilder.CreateIndex(
                name: "IX_LoanCommitteeMembers_UserId",
                table: "LoanCommitteeMembers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_LoanCommittees_Stage_Province",
                table: "LoanCommittees",
                columns: new[] { "Stage", "Province" });

            migrationBuilder.CreateIndex(
                name: "IX_LoanDecisions_ApplicationId_CreatedAt",
                table: "LoanDecisions",
                columns: new[] { "ApplicationId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_LoanDecisions_DeciderId",
                table: "LoanDecisions",
                column: "DeciderId");

            migrationBuilder.CreateIndex(
                name: "IX_LoanDocuments_ApplicationId_Kind",
                table: "LoanDocuments",
                columns: new[] { "ApplicationId", "Kind" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FinanceConstituencies");

            migrationBuilder.DropTable(
                name: "LoanApplicationItems");

            migrationBuilder.DropTable(
                name: "LoanCommitteeMembers");

            migrationBuilder.DropTable(
                name: "LoanDecisions");

            migrationBuilder.DropTable(
                name: "LoanDocuments");

            migrationBuilder.DropTable(
                name: "LoanEquipment");

            migrationBuilder.DropTable(
                name: "LoanCommittees");

            migrationBuilder.DropTable(
                name: "LoanApplications");
        }
    }
}
