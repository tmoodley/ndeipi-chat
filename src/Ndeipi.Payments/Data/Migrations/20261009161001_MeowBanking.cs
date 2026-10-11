using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ndeipi.Payments.Data.Migrations
{
    /// <inheritdoc />
    public partial class MeowBanking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ActsForOfficers",
                schema: "payments",
                table: "OperatorKeys",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "AssistantMessages",
                schema: "payments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SessionId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Role = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Content = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AssistantSessions",
                schema: "payments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    OfficerId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Officer = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BankingJournal",
                schema: "payments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ActorType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Detail = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    AgentSessionId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    AgentModel = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankingJournal", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BankWebhookEvents",
                schema: "payments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RawPayload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Signature = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankWebhookEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CorporateCards",
                schema: "payments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ProviderCardId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Nickname = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Last4 = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: true),
                    Cardholder = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DebitAccountId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    PerTransactionLimit = table.Column<int>(type: "int", nullable: false),
                    DailyLimit = table.Column<int>(type: "int", nullable: true),
                    MonthlyLimit = table.Column<int>(type: "int", nullable: true),
                    AllowedCategories = table.Column<string>(type: "nvarchar(1500)", maxLength: 1500, nullable: false),
                    State = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Purpose = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    NumberRevealed = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CorporateCards", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Payees",
                schema: "payments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Display = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Country = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: true),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: true),
                    Address = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ProviderContactId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ProviderPayeeId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payees", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PaymentApprovals",
                schema: "payments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PaymentId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    OfficerId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Officer = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Decision = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    TermsDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentApprovals", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TreasuryAccounts",
                schema: "payments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Last4 = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: true),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Available = table.Column<decimal>(type: "decimal(38,2)", precision: 38, scale: 2, nullable: false),
                    Current = table.Column<decimal>(type: "decimal(38,2)", precision: 38, scale: 2, nullable: false),
                    Bank = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RefreshedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TreasuryAccounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TreasuryPayments",
                schema: "payments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Rail = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AccountId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    PayeeId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(38,2)", precision: 38, scale: 2, nullable: false),
                    RecipientCurrency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: true),
                    Purpose = table.Column<string>(type: "nvarchar(140)", maxLength: 140, nullable: false),
                    Memo = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    RequestKey = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    InitiatorType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    InitiatedBy = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    InitiatedById = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AgentSessionId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    AgentModel = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    RequiredApprovals = table.Column<int>(type: "int", nullable: false),
                    ApprovalExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ProviderApprovalId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ProviderTransferId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ProviderStatus = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    FailureReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    DispatchAttempts = table.Column<int>(type: "int", nullable: false),
                    NextDispatchAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DispatchedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TreasuryPayments", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantMessages_SessionId_Id",
                schema: "payments",
                table: "AssistantMessages",
                columns: new[] { "SessionId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_BankingJournal_Subject",
                schema: "payments",
                table: "BankingJournal",
                column: "Subject");

            migrationBuilder.CreateIndex(
                name: "IX_BankWebhookEvents_EventId",
                schema: "payments",
                table: "BankWebhookEvents",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BankWebhookEvents_Status_Id",
                schema: "payments",
                table: "BankWebhookEvents",
                columns: new[] { "Status", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_CorporateCards_ProviderCardId",
                schema: "payments",
                table: "CorporateCards",
                column: "ProviderCardId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentApprovals_PaymentId_OfficerId",
                schema: "payments",
                table: "PaymentApprovals",
                columns: new[] { "PaymentId", "OfficerId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TreasuryPayments_CreatedAt",
                schema: "payments",
                table: "TreasuryPayments",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_TreasuryPayments_ProviderTransferId",
                schema: "payments",
                table: "TreasuryPayments",
                column: "ProviderTransferId");

            migrationBuilder.CreateIndex(
                name: "IX_TreasuryPayments_RequestKey",
                schema: "payments",
                table: "TreasuryPayments",
                column: "RequestKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TreasuryPayments_Status_NextDispatchAt",
                schema: "payments",
                table: "TreasuryPayments",
                columns: new[] { "Status", "NextDispatchAt" });

            // Write once, read many (NFR-SEC-03): the database itself refuses to change or delete these
            // rows, whatever the application or a SQL login tries.
            foreach (var table in new[] { "BankingJournal", "PaymentApprovals", "AssistantMessages" })
                migrationBuilder.Sql($"""
                    CREATE TRIGGER [payments].[TR_{table}_WriteOnce] ON [payments].[{table}]
                    INSTEAD OF UPDATE, DELETE AS
                    BEGIN
                        THROW 51000, '{table} rows are write-once.', 1;
                    END
                    """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssistantMessages",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "AssistantSessions",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "BankingJournal",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "BankWebhookEvents",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "CorporateCards",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "Payees",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "PaymentApprovals",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "TreasuryAccounts",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "TreasuryPayments",
                schema: "payments");

            migrationBuilder.DropColumn(
                name: "ActsForOfficers",
                schema: "payments",
                table: "OperatorKeys");
        }
    }
}
