using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ndeipi.Api.Data.Migrations
{
    /// <summary>
    /// The interface Ndeipi Enterprise Server works through: claim, complete, fail and release
    /// procedures over ndeipi.TokenTransferQueue, a trigger that flags status changes for the chat,
    /// and a role holding exactly the permissions a worker needs. See docs/ndeipi-queue.md.
    /// </summary>
    public partial class TokenQueueContract : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // However the status changes -- through the procedures or a direct UPDATE -- the chat hears of it.
            migrationBuilder.Sql("""
                CREATE OR ALTER TRIGGER ndeipi.trg_TokenTransferQueue_StatusChanged
                ON ndeipi.TokenTransferQueue
                AFTER UPDATE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF NOT UPDATE(Status) RETURN;

                    UPDATE q
                       SET NotifyPending = 1
                      FROM ndeipi.TokenTransferQueue q
                      JOIN inserted i ON i.Id = q.Id
                      JOIN deleted d ON d.Id = q.Id
                     WHERE i.Status <> d.Status;
                END
                """);

            // Oldest first. READPAST skips rows another worker has locked, so parallel workers each
            // get different transfers and none waits on another.
            migrationBuilder.Sql("""
                CREATE OR ALTER PROCEDURE ndeipi.usp_ClaimTokenTransfers
                    @WorkerId nvarchar(100),
                    @BatchSize int = 10
                AS
                BEGIN
                    SET NOCOUNT ON;
                    DECLARE @claimed TABLE (Id uniqueidentifier PRIMARY KEY);

                    WITH next AS (
                        SELECT TOP (@BatchSize) *
                          FROM ndeipi.TokenTransferQueue WITH (ROWLOCK, UPDLOCK, READPAST)
                         WHERE Status = N'Pending'
                         ORDER BY CreatedAt
                    )
                    UPDATE next
                       SET Status = N'Processing',
                           ClaimedBy = @WorkerId,
                           ClaimedAt = SYSUTCDATETIME(),
                           UpdatedAt = SYSUTCDATETIME(),
                           Attempts = Attempts + 1
                    OUTPUT inserted.Id INTO @claimed;

                    SELECT q.Id, q.Operation, q.Status, q.Chain, q.TokenStandard, q.TokenSymbol, q.ContractAddress,
                           q.TokenId, q.Decimals, q.Amount,
                           q.SenderUserId, q.SenderClerkId, q.SenderWalletAddress,
                           q.RecipientUserId, q.RecipientClerkId, q.RecipientWalletAddress,
                           q.ConversationId, q.MessageId, q.Memo, q.Attempts, q.ClaimedBy, q.ClaimedAt, q.CreatedAt
                      FROM ndeipi.TokenTransferQueue q
                      JOIN @claimed c ON c.Id = q.Id
                     ORDER BY q.CreatedAt;
                END
                """);

            migrationBuilder.Sql(FinishProcedure("usp_CompleteTokenTransfer", "@TxHash nvarchar(128)",
                "Status = N'Confirmed', TxHash = @TxHash, Error = NULL, CompletedAt = SYSUTCDATETIME()"));

            migrationBuilder.Sql(FinishProcedure("usp_FailTokenTransfer", "@Error nvarchar(1000)",
                "Status = N'Failed', Error = @Error, CompletedAt = SYSUTCDATETIME()"));

            // For a worker that claimed a transfer but put nothing on-chain (e.g. shutting down).
            // Never release one that might have been broadcast: it would be sent twice.
            migrationBuilder.Sql(FinishProcedure("usp_ReleaseTokenTransfer", null,
                "Status = N'Pending', ClaimedBy = NULL, ClaimedAt = NULL"));

            migrationBuilder.Sql("""
                IF DATABASE_PRINCIPAL_ID(N'ndeipi_worker') IS NULL
                    CREATE ROLE ndeipi_worker;
                GRANT SELECT ON ndeipi.TokenTransferQueue TO ndeipi_worker;
                GRANT EXECUTE ON ndeipi.usp_ClaimTokenTransfers TO ndeipi_worker;
                GRANT EXECUTE ON ndeipi.usp_CompleteTokenTransfer TO ndeipi_worker;
                GRANT EXECUTE ON ndeipi.usp_FailTokenTransfer TO ndeipi_worker;
                GRANT EXECUTE ON ndeipi.usp_ReleaseTokenTransfer TO ndeipi_worker;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP PROCEDURE IF EXISTS ndeipi.usp_ReleaseTokenTransfer;
                DROP PROCEDURE IF EXISTS ndeipi.usp_FailTokenTransfer;
                DROP PROCEDURE IF EXISTS ndeipi.usp_CompleteTokenTransfer;
                DROP PROCEDURE IF EXISTS ndeipi.usp_ClaimTokenTransfers;
                DROP TRIGGER IF EXISTS ndeipi.trg_TokenTransferQueue_StatusChanged;
                IF DATABASE_PRINCIPAL_ID(N'ndeipi_worker') IS NOT NULL
                    DROP ROLE ndeipi_worker;
                """);
        }

        /// <summary>
        /// A procedure that moves a transfer the calling worker holds out of Processing. Refuses
        /// anything else, so a stale or confused worker can't overwrite another's result.
        /// </summary>
        static string FinishProcedure(string name, string extraParameter, string set) => $"""
            CREATE OR ALTER PROCEDURE ndeipi.{name}
                @Id uniqueidentifier,
                @WorkerId nvarchar(100){(extraParameter is null ? "" : ",\n    " + extraParameter)}
            AS
            BEGIN
                SET NOCOUNT ON;
                DECLARE @done TABLE (Id uniqueidentifier);

                UPDATE ndeipi.TokenTransferQueue
                   SET {set}, UpdatedAt = SYSUTCDATETIME()
                OUTPUT inserted.Id INTO @done
                 WHERE Id = @Id AND Status = N'Processing' AND ClaimedBy = @WorkerId;

                IF NOT EXISTS (SELECT 1 FROM @done)
                BEGIN
                    DECLARE @message nvarchar(400) = CONCAT(N'Token transfer ', CONVERT(nvarchar(36), @Id), N' isn''t being processed by ', @WorkerId, N'.');
                    THROW 50001, @message, 1;
                END
            END
            """;
    }
}
