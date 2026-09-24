using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ndeipi.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Livestock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LivestockMaster",
                columns: table => new
                {
                    CowId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RanchId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    OwnerWallet = table.Column<string>(type: "nvarchar(42)", maxLength: 42, nullable: true),
                    OwnerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BiometricMuzzleHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    MuzzleEmbedding = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    EmbeddingModel = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Breed = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    BreedConfidence = table.Column<decimal>(type: "decimal(5,4)", precision: 5, scale: 4, nullable: false),
                    BreedSource = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Sex = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    ApproximateAgeMonths = table.Column<int>(type: "int", nullable: true),
                    RegistrationTimestamp = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Latitude = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: false),
                    Longitude = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: false),
                    GpsAccuracyMeters = table.Column<decimal>(type: "decimal(8,2)", precision: 8, scale: 2, nullable: false),
                    FaceImageRef = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    MorphologyJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LivestockMaster", x => x.CowId);
                    table.ForeignKey(
                        name: "FK_LivestockMaster_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LivestockOperatorKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PublicKey = table.Column<byte[]>(type: "varbinary(200)", maxLength: 200, nullable: false),
                    Label = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LivestockOperatorKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LivestockOperatorKeys_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LivestockHealthAudit",
                columns: table => new
                {
                    AuditId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CowId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ClientCapturedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    BodyConditionScore = table.Column<decimal>(type: "decimal(3,1)", precision: 3, scale: 1, nullable: false),
                    HealthRating = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    HydrationStatus = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    AnomalySummary = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RequiresVetInspection = table.Column<bool>(type: "bit", nullable: false),
                    FaceImageRef = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    FlankImageRef = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    AttestationHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    OperatorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OperatorKeyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Signature = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SubmissionHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AssessmentModel = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ResponseJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LivestockHealthAudit", x => x.AuditId);
                    table.ForeignKey(
                        name: "FK_LivestockHealthAudit_LivestockMaster_CowId",
                        column: x => x.CowId,
                        principalTable: "LivestockMaster",
                        principalColumn: "CowId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LivestockHealthAudit_CowId_TimestampUtc",
                table: "LivestockHealthAudit",
                columns: new[] { "CowId", "TimestampUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LivestockHealthAudit_SubmissionHash",
                table: "LivestockHealthAudit",
                column: "SubmissionHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Livestock_RanchId",
                table: "LivestockMaster",
                column: "RanchId");

            migrationBuilder.CreateIndex(
                name: "IX_LivestockMaster_BiometricMuzzleHash",
                table: "LivestockMaster",
                column: "BiometricMuzzleHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LivestockMaster_OwnerUserId",
                table: "LivestockMaster",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_LivestockOperatorKeys_UserId",
                table: "LivestockOperatorKeys",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LivestockHealthAudit");

            migrationBuilder.DropTable(
                name: "LivestockOperatorKeys");

            migrationBuilder.DropTable(
                name: "LivestockMaster");
        }
    }
}
