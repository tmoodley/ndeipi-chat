using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NdeipiChat.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Gigs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GigProfiles",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Headline = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    Skills = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Region = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    Latitude = table.Column<double>(type: "float", nullable: false),
                    Longitude = table.Column<double>(type: "float", nullable: false),
                    IsAvailable = table.Column<bool>(type: "bit", nullable: false),
                    CompletedGigs = table.Column<int>(type: "int", nullable: false),
                    RatingSum = table.Column<int>(type: "int", nullable: false),
                    RatingCount = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GigProfiles", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_GigProfiles_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Gigs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Title = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Skill = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Latitude = table.Column<double>(type: "float", nullable: false),
                    Longitude = table.Column<double>(type: "float", nullable: false),
                    Region = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    Budget = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    TokenSymbol = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    SourceConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PaymentMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WorkerStars = table.Column<int>(type: "int", nullable: true),
                    ClientStars = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AssignedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Gigs", x => x.Id);
                    table.CheckConstraint("CK_Gigs_Stars", "([WorkerStars] IS NULL OR [WorkerStars] BETWEEN 1 AND 5) AND ([ClientStars] IS NULL OR [ClientStars] BETWEEN 1 AND 5)");
                    table.ForeignKey(
                        name: "FK_Gigs_Users_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Gigs_Users_WorkerId",
                        column: x => x.WorkerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GigOffers",
                columns: table => new
                {
                    GigId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DistanceKm = table.Column<double>(type: "float", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GigOffers", x => new { x.GigId, x.WorkerId });
                    table.ForeignKey(
                        name: "FK_GigOffers_Gigs_GigId",
                        column: x => x.GigId,
                        principalTable: "Gigs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GigOffers_Users_WorkerId",
                        column: x => x.WorkerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GigOffers_WorkerId_Status",
                table: "GigOffers",
                columns: new[] { "WorkerId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_GigProfiles_IsAvailable_Latitude",
                table: "GigProfiles",
                columns: new[] { "IsAvailable", "Latitude" });

            migrationBuilder.CreateIndex(
                name: "IX_Gigs_ClientId_CreatedAt",
                table: "Gigs",
                columns: new[] { "ClientId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Gigs_Status_Latitude",
                table: "Gigs",
                columns: new[] { "Status", "Latitude" });

            migrationBuilder.CreateIndex(
                name: "IX_Gigs_WorkerId_CreatedAt",
                table: "Gigs",
                columns: new[] { "WorkerId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GigOffers");

            migrationBuilder.DropTable(
                name: "GigProfiles");

            migrationBuilder.DropTable(
                name: "Gigs");
        }
    }
}
