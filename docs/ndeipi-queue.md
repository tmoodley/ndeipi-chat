# Token transfer queue — contract for Ndeipi Enterprise Server

When someone sends a token in a chat, the API writes one row to `ndeipi.TokenTransferQueue`, in
the same transaction as the chat message. Ndeipi Enterprise Server takes rows off the queue and
mints each one onto the chain. It writes the outcome back, and the chat shows it to both people
within about two seconds.

Ndeipi only needs SQL Server access. It never calls the chat API.

## Access

The migration creates a database role that holds exactly what a worker needs. Add Ndeipi's login to it:

```sql
CREATE USER [ndeipi-enterprise] FOR LOGIN [ndeipi-enterprise];
ALTER ROLE ndeipi_worker ADD MEMBER [ndeipi-enterprise];
```

`ndeipi_worker` can `SELECT` from the queue and `EXECUTE` the four procedures below. It can't write to the table directly.

## Lifecycle

```
Pending ──claim──▶ Processing ──complete──▶ Confirmed
                       │
                       ├──fail──────────▶ Failed
                       └──release───────▶ Pending   (only if nothing was broadcast)
```

| Status | Meaning | Shown in chat as |
| --- | --- | --- |
| `Pending` | Queued, not yet picked up | "Queued" |
| `Processing` | A worker has claimed it | "Minting on-chain…" |
| `Confirmed` | On-chain; `TxHash` set | "Sent" / "Received" and the tx hash |
| `Failed` | Won't happen; `Error` says why | The `Error` text, shown to both people |

`Error` is displayed as-is, so write it for the user, e.g. "Insufficient NMX balance". Don't put a stack trace there.

## Procedures

### `ndeipi.usp_ClaimTokenTransfers @WorkerId, @BatchSize = 10`

This claims up to `@BatchSize` of the oldest `Pending` rows, marks them `Processing` for
`@WorkerId`, and returns them. It uses `READPAST`, so any number of workers can call it at once.
Each gets different rows and none blocks the others.

Columns returned:

| Column | Type | Notes |
| --- | --- | --- |
| `Id` | `uniqueidentifier` | The transfer id. **Use it as your idempotency key on-chain.** |
| `Operation` | `nvarchar(20)` | `Transfer` (Ndeipi's `TransactionType`) or `Mint`. See [Minting posts](#minting-posts). |
| `Chain` | `nvarchar(50)` | Lower case, e.g. `polygon`. |
| `TokenStandard` | `nvarchar(20)` | `native`, `erc20`, `erc721`, `erc1155`, `spl` or `other`. |
| `TokenSymbol` | `nvarchar(32)` | e.g. `NMX`. For tokens in the API's catalogue, the server sets this, not the app. |
| `ContractAddress` | `nvarchar(128)` | `NULL` for a chain's native coin. |
| `TokenId` | `nvarchar(78)` | NFT id as a decimal string; `NULL` for fungible tokens. |
| `Decimals` | `int` | Fungible tokens only, when known. |
| `Amount` | `decimal(38,18)` | In whole tokens (`12.5`), not base units. For ERC-721 it's always `1`. |
| `SenderUserId` / `RecipientUserId` | `uniqueidentifier` | The chat's user ids. |
| `SenderClerkId` / `RecipientClerkId` | `nvarchar(64)` | Clerk user ids (`user_...`), stable across systems. |
| `SenderWalletAddress` / `RecipientWalletAddress` | `nvarchar(128)` | The address each user saved for this chain in the app's Wallet screen, or `NULL`. If your wallets are custodial, resolve them from the Clerk id instead. |
| `ConversationId`, `MessageId` | `uniqueidentifier` | Where the transfer was sent from. `NULL` for a `Mint`. |
| `PostId`, `MetadataUri` | `uniqueidentifier`, `nvarchar(500)` | A `Mint` only: the post being minted, and the NFT's tokenURI. `NULL` for a `Transfer`. |
| `Memo` | `nvarchar(280)` | Optional note from the sender. |
| `Attempts`, `ClaimedBy`, `ClaimedAt`, `CreatedAt` | | `datetime2`, UTC. |

### `ndeipi.usp_CompleteTokenTransfer @Id, @WorkerId, @TxHash`

Marks the transfer `Confirmed` and records the transaction hash.

### `ndeipi.usp_FailTokenTransfer @Id, @WorkerId, @Error`

Marks the transfer `Failed`, for example for an insufficient balance, a missing wallet or a
rejected transaction.

### `ndeipi.usp_ReleaseTokenTransfer @Id, @WorkerId`

Puts a claimed transfer back to `Pending`, for a worker shutting down before it sent anything.
**Never release a transfer that may have been broadcast**, because the next worker would send it
again. If you can't tell, leave it `Processing` and resolve it by hand.

Complete, fail and release all refuse, with error 50001, a transfer that isn't `Processing` for
the `@WorkerId` you name. So a stale or confused worker can't overwrite another worker's result.

## Minting posts

People can mint their feed posts (photos and a caption) as ERC-721 NFTs. A mint is a row on the
same queue, with the same procedures and lifecycle, and differs from a transfer like this:

| Column | For a `Mint` |
| --- | --- |
| `Operation` | `Mint`: create the token rather than move it. |
| `Chain`, `ContractAddress`, `TokenStandard`, `TokenSymbol` | The API's `Social:Nft` settings, e.g. your NFT contract on NdeipiCoin, `erc721`, `NDPOST`. |
| `TokenId` | Chosen by the API: the post id as an unsigned 128-bit number, in decimal. It's the same on every retry of the same post, so a second mint of it on-chain is a duplicate. Mint exactly this id. |
| `Amount` | `1`. |
| `RecipientUserId` / `RecipientClerkId` / `RecipientWalletAddress` | The author. That's who the token is minted to. The sender columns hold the author too. |
| `MetadataUri` | The token's `tokenURI`, e.g. `https://chat.ndeipi.com/nft/posts/{postId}`. It serves ERC-721 metadata JSON (`name`, `description`, `image`, `attributes`), publicly, with no sign-in. Set it on the token as you mint. |
| `PostId` | The post. `ConversationId` and `MessageId` are `NULL`. |

Complete it with `usp_CompleteTokenTransfer` and the mint's transaction hash, as for a transfer;
the author sees "NFT #… on {chain}" within about two seconds. Fail it with `usp_FailTokenTransfer`
and a message for the author ("Gas price too high, try later"). They can retry, which queues a new
row with a new `Id` and the same `TokenId`. So before minting, check the token doesn't already
exist, in case an earlier attempt landed after all.

Once a post has a mint that hasn't failed, it can't be deleted, so the metadata and photos the
token points at stay up.

## A worker loop

```csharp
while (!stopping)
{
    var batch = await db.QueryAsync<Transfer>("ndeipi.usp_ClaimTokenTransfers",
        new { WorkerId = workerId, BatchSize = 20 }, commandType: CommandType.StoredProcedure);
    if (!batch.Any()) { await Task.Delay(1000); continue; }

    foreach (var t in batch)
    {
        try
        {
            // Idempotent on t.Id: if this transfer was already minted (a crash after broadcast,
            // before completing), return the existing hash instead of sending again.
            var txHash = await chain.MintTransferAsync(t, idempotencyKey: t.Id);
            await db.ExecuteAsync("ndeipi.usp_CompleteTokenTransfer",
                new { t.Id, WorkerId = workerId, TxHash = txHash }, commandType: CommandType.StoredProcedure);
        }
        catch (UserFacingException ex)
        {
            await db.ExecuteAsync("ndeipi.usp_FailTokenTransfer",
                new { t.Id, WorkerId = workerId, Error = ex.Message }, commandType: CommandType.StoredProcedure);
        }
    }
}
```

## Things Ndeipi must own

- **Balances.** The chat doesn't know what anyone holds, so it can't refuse to queue a transfer
  the sender can't cover. Check the balance when you process the transfer, and fail it with a
  clear `Error` if it's short.
- **Exactly-once on-chain.** A worker can crash between broadcasting and calling `usp_CompleteTokenTransfer`.
  Keep your own record keyed by `Id` so a restart finds the broadcast instead of repeating it.
- **Stuck rows.** Nothing re-queues a `Processing` row automatically, on purpose. Alert on rows
  that stay `Processing` longer than your chain's confirmation time.

## How the chat hears back

A trigger on the table sets `NotifyPending = 1` whenever `Status` changes, including changes made
by a direct `UPDATE`. Every two seconds the API relays flagged rows to both people in the chat,
then clears the flag. Ndeipi never touches `NotifyPending`.
