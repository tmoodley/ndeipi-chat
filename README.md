# Ndeipi Chat

A WeChat-style messenger. You chat in real time and send any token, or money, to the people you
talk to, right in the conversation.

- **App:** .NET MAUI for Android and iOS, plus Mac Catalyst.
- **Backend:** ASP.NET Core with SignalR on SQL Server.
- **Sign-in:** Clerk.
- **Token transfers:** queued in SQL Server for **Ndeipi Enterprise Server** to mint on-chain.
- **Banking:** runs on **Bridge**, which handles KYC, custodial wallets and user-to-user transfers.
- **Shamwaris (friends):** add people by email or phone number. They accept, and then you can chat
  and send money. See [Shamwaris](#shamwaris).
- **Livestock registry:** farmers register cattle from a face photo and a side photo. Claude grades
  breed, body condition and visible health, and a muzzle-print model stops the same animal being
  registered twice. See [docs/livestock-registry.md](docs/livestock-registry.md).

```
 ┌──────────────────────┐   HTTPS + SignalR    ┌───────────────────────────┐
 │  NdeipiChat.App      │ ───────────────────▶ │  NdeipiChat.Api           │
 │  (MAUI)              │ ◀─────────────────── │  /hubs/chat  /api/...     │
 │   └ Client.Core      │   live messages,     │                           │
 └──────────┬───────────┘   transfer status    │  Clerk JWT validation     │──▶ Clerk Backend API
            │ system browser                   │  message pipeline         │──▶ Bridge API ◀── webhooks
            ▼                                  │                           │
   Clerk sign-in page ◀─ served by the API ─── └────────────┬──────────────┘
                                                            │ EF Core
                                                  ┌─────────▼──────────┐   procedures   ┌──────────────────────┐
                                                  │ SQL Server         │ ◀────────────▶ │ Ndeipi Enterprise    │
                                                  │ ndeipi.TokenTransferQueue           │ Server → blockchain  │
                                                  └────────────────────┘                └──────────────────────┘
```

| Project | What it is |
| --- | --- |
| `src/NdeipiChat.Contracts` | DTOs, hub method names and message payloads shared by server and app. |
| `src/NdeipiChat.Api` | The backend: SignalR hub, REST endpoints, Clerk auth, the token queue, Bridge. |
| `src/NdeipiChat.Client.Core` | Everything in the app except the screens: sign-in, API and SignalR clients, chat extensions, view models. A plain .NET library, so it's tested without a device. |
| `src/NdeipiChat.App` | The MAUI app: pages, message views, platform glue. |
| `tests/NdeipiChat.Tests` | Integration tests: the real API on SQL Server LocalDB, driven through SignalR, HTTP and `Client.Core`'s view models. |
| `docs/ndeipi-queue.md` | The queue contract for Ndeipi Enterprise Server. |
| `docs/livestock-registry.md` | The livestock registry: how it maps to the spec, where it deviates, how the muzzle model plugs in. |

## Running the API

Prerequisites: the .NET 10 SDK and SQL Server (LocalDB is enough for development).

```bash
cd src/NdeipiChat.Api
dotnet user-secrets init
dotnet user-secrets set "Clerk:Authority" "https://your-app.clerk.accounts.dev"
dotnet user-secrets set "Clerk:PublishableKey" "pk_test_..."
dotnet user-secrets set "Clerk:SecretKey" "sk_test_..."
dotnet user-secrets set "Bridge:ApiKey" "sk-test-..."
dotnet user-secrets set "Livestock:Claude:ApiKey" "sk-ant-..."
dotnet user-secrets set "Livestock:Muzzle:ModelPath" "C:/models/muzzle-print.onnx"
dotnet ef database update
dotnet run
```

`ConnectionStrings:Chat` defaults to LocalDB. Set `Database:MigrateOnStartup` to `true` if you'd
rather have the API apply migrations itself.

In Development the API serves Swagger UI at `/swagger` and the OpenAPI document at
`/openapi/v1.json`. For endpoints that need sign-in, click **Authorize** and paste a Clerk session
token.

### Clerk

1. Create a Clerk application. From **API keys**, copy:
   - the **Frontend API URL**, into `Clerk:Authority`
   - the **publishable key**, into `Clerk:PublishableKey`
   - the **secret key**, into `Clerk:SecretKey`
2. Recommended: create a **JWT template** (for example `ndeipi-chat`) with a lifetime of about an
   hour, and set `Clerk:SessionTokenTemplate`. Plain session tokens last 60 seconds, which means
   the app would refresh every minute.
3. In production, set `Clerk:AuthorizedParties` to the API's origin.

Clerk has no .NET MAUI SDK, so sign-in works like OAuth:

1. The app opens `/auth/mobile/sign-in` in the system browser with a PKCE challenge.
2. That page runs Clerk's own sign-in UI (`clerk-js`).
3. Once signed in, the page swaps its session token for a one-time code and redirects to `ndeipichat://auth`.
4. The app redeems the code with its PKCE verifier. It gets back a Clerk session token and a
   rotating refresh token.

Every refresh asks Clerk to mint a new session token, so signing out or revoking the session in
Clerk also signs the app out. A refresh token that gets reused ends the session.

The page has to be served from a domain your Clerk instance accepts. Development instances accept any.

### Bridge

1. In the Bridge dashboard, create a sandbox API key and put it in `Bridge:ApiKey`.
2. Add a webhook endpoint at `https://<api-host>/webhooks/bridge` for the `kyc_link`, `customer`
   and `transfer` events. Put the endpoint's public key (PEM) in `Bridge:WebhookPublicKey`.
3. `Bridge:WalletChain` (default `solana`) sets the chain each user's custodial wallet is created
   on. `Bridge:Currency` (default `usdc`) sets the stablecoin users send each other.

How it works:

- **Me → Wallet → Verify identity** creates a Bridge KYC link and opens Bridge's terms, then its
  hosted KYC.
- When Bridge approves the user, by webhook or on pull-to-refresh, the API creates their Bridge wallet.
- **Send money** in a chat moves stablecoin from the sender's Bridge wallet to the recipient's.
  Each transfer's id is its Bridge idempotency key, and a background poller retries any whose
  outcome wasn't known.

### Shamwaris

The **Shamwaris** tab adds friends by email address or phone number. Numbers need their country
code (`+263 77 123 4567`); spaces, dashes and a `00` prefix are fine.

- If someone on Ndeipi has that email or number, they get a request live and appear under
  **Requests**. Once they accept, you're Shamwaris on both sides. If they'd already asked you,
  adding them back accepts straight away.
- If no one does yet, the invite waits. When someone signs up, or later verifies that email or
  number in Clerk, the invite becomes a request to them.
- Only **verified** emails and numbers count, both for being found and for claiming invites.
  Otherwise anyone could sign up with someone else's email and collect their requests. Turn on
  phone numbers in Clerk (**User & authentication → Phone**) for adding by phone to work.
- Tap a Shamwari to open your chat. The chat's **+** panel sends money or tokens. Swipe a Shamwari
  left to remove them.
- No email or SMS goes out yet. Tell the person to join Ndeipi yourself.

## Running the app

1. `dotnet workload install maui`.
2. Set `AppConfig.ApiBaseUrl` in `src/NdeipiChat.App/AppConfig.cs`. Phones can't reach
   `localhost`, so during development expose the API over HTTPS. `dotnet run` serves it on port
   5057, so for example use `devtunnel host -p 5057 --allow-anonymous`.
3. Build and run:

```bash
dotnet build src/NdeipiChat.App -t:Run -f net10.0-android
```

The redirect scheme `ndeipichat://auth` is registered on each platform. On Android that's
`WebAuthenticationCallbackActivity`; on iOS and Mac it's `CFBundleURLTypes`. It must also appear
in the API's `MobileAuth:RedirectUris`.

## Extending the chat

Every message has a `Kind`. The server accepts only kinds it has a handler for, and the app draws
each kind with its own view. To add one, say a poll:

**1. Contracts:** a payload record, plus optional state for anything that changes later.

```csharp
public sealed record PollPayload(string Question, IReadOnlyList<string> Options);
```

**2. API:** a handler that validates and normalises the payload. Anything it adds to the
`ChatDbContext` is saved in the same transaction as the message. For calls to outside services,
use `AfterSendAsync`.

```csharp
public sealed class PollHandler : IMessageKindHandler
{
    public string Kind => "poll";

    public Task<PreparedMessage> PrepareAsync(MessageContext context, JsonElement payload, CancellationToken ct)
    {
        var poll = MessagePayload.Read<PollPayload>(payload);
        if (poll.Options.Count is < 2 or > 10)
            throw new ChatRejectedException("A poll needs 2 to 10 options.");
        return Task.FromResult(new PreparedMessage(ContractJson.ToElement(poll)));
    }
}

builder.Services.AddMessageKind<PollHandler>();
```

Use `MessageStateService.SetAsync(messageId, state)` to change a message's state later, e.g. a
vote count. Everyone in the conversation gets `MessageStateChanged`.

**3. App:** a view model, a renderer, a view and optionally a tile in the "+" panel.

```csharp
public sealed class PollMessageViewModel(MessageDto m, MessageRenderContext c) : MessageViewModel(m, c) { ... }
public sealed class PollRenderer : IMessageRenderer { public string Kind => "poll"; ... }

builder.Services.AddMessageRenderer<PollRenderer>();
builder.Services.AddComposerAction<CreatePollAction>();
builder.Services.AddMessageTemplate<PollMessageViewModel, PollMessageView>();   // PollMessageView : MessageRow
```

Older app builds show messages of a kind they don't know as "needs a newer version of the app",
rather than failing.

Token transfers (`asset.transfer`) and money (`bank.transfer`) are built exactly this way. They
are the best worked examples.

## Tests

```bash
dotnet test tests/NdeipiChat.Tests
```

The tests need SQL Server LocalDB, and each test class gets its own throwaway database. Clerk's
Backend API and Bridge are faked. Everything else is real: JWT validation, SignalR, EF Core, the
queue procedures and trigger, and the app's view models.

## Before going live

- **Bridge specifics to confirm against your Bridge account:**
  - that a wallet-to-wallet transfer uses a `bridge_wallet` source and the recipient's wallet
    address on its chain as the destination (`BankingService.SubmitTransferAsync`)
  - the webhook signature scheme (`BridgeWebhookVerifier`)
- **Balances for token transfers** are Ndeipi's to check. See `docs/ndeipi-queue.md`.
- **More than one API instance** needs a SignalR backplane, e.g.
  `AddSignalR().AddStackExchangeRedis(...)` or Azure SignalR Service. Otherwise messages only
  reach people connected to the same instance.
- **Push notifications** for people who aren't connected (FCM/APNs) aren't included yet.
- **Shamwari invites** aren't delivered to the invited person yet. Sending them needs an email or
  SMS provider, such as SendGrid or Twilio.
- **Livestock:**
  - Supply and tune a muzzle-print ONNX model. Without one, duplicates can't be detected.
  - Validate Claude's body-condition and breed calls against your vets' scores.
  - Move photo storage to blob storage when running more than one API instance.
