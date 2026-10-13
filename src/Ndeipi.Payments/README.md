# Ndeipi.Payments

The Ndeipi Enterprise Server payments API, built to the contract in
[docs/payments-api/openapi.yaml](../../docs/payments-api/openapi.yaml) and the SRS it comes from.
It is a separate host from the chat API. Integrators authenticate with API keys, not Clerk
sessions, and the sandbox is a second deployment of this same build with simulated providers
(SC-06).

## Layout

The folders follow the six server components in SRS §8.2.

| Folder | Component | State |
|---|---|---|
| `Api/` | Payments API: API keys, idempotency, errors, request IDs, rate limits, audit log, pagination | Built (M1) |
| `Users/` | Users and KYC | Built: create, get, list, update, onboarding links, KYC and terms status changes (M2), and deactivation (M3). Names and contact details are encrypted at rest, and every read is audited (M5). |
| `Ledger/` | Ledger, wallets and house accounts | Built: wallets (M3); house accounts for Ndeipi's floats, reserve, treasury, NdeipiCoin stock and fees (M4). |
| `Transfers/` | One transfer resource for every money movement | Built: user-to-user (M3), conversions, on-ramps and off-ramps (M4), and transaction monitoring that holds transfers for review (M5). Cancel is M6. |
| `Ramps/` | Deposit accounts, payout accounts, routes, rates, ramp worker | Built (M4) on the sandbox rails. The real Absa and PayPal adapters wait on onboarding. |
| `Providers/` | Fiat rails (PayPal, Absa), the exchange (Blockfinex), and the registry that routes by rail code | The interfaces, registry and simulated providers are built. The real adapters wait on Absa onboarding, PayPal approval and Blockfinex access. |
| `Treasury/` | NdeipiCoin: quotes, conversions, manual OTC trades, treasury operations | Built (M4). |
| `Reconciliation/` | Ledger against provider | The balance check is built. A daily schedule, per-movement matching and alerts are not built yet. |
| `Webhooks/` | Endpoints, events, signed delivery | Built (M2): endpoints with their own keys, the events API, redelivery, and a dispatcher that retries with backoff and connects only to public addresses. The test call is M6. |
| `Sandbox/` | Simulated KYC, deposits, payout outcomes | Built. |
| `Ops/` | Operator API at `/ops/v1`: review queue, release and reject, four-eyes manual refunds | Built (M5). Not part of the integrator contract; operators authenticate with operator keys. |
| `Banking/` | Ndeipi's own treasury at Meow (SRS-NDE-MEOW-001), at `/ops/v1/banking` and `/webhooks/meow` | Built: accounts and statements, payees, ACH/wire/international/USDC payments with sanctions screening and dual approval, virtual cards, signed webhooks, the write-once journal, and the Claude assistant over Meow's read-only MCP tools. Off unless `Payments:Banking:Provider` is set. |

Every operation in the contract is mapped. Ones that are not built yet answer
`501 not_implemented` and name their milestone. `ContractTests` fails if the routes and
openapi.yaml drift apart.

## Run it

```bash
dotnet run --project src/Ndeipi.Payments -- issue-key "Local dev"
dotnet run --project src/Ndeipi.Payments
curl -H "Api-Key: nd_test_..." http://localhost:5180/v1/users
```

Each operator gets their own key for the operator API (`/ops/v1`), printed once:

```bash
dotnet run --project src/Ndeipi.Payments -- issue-operator-key "Nomsa (compliance)"
```

Treasury operations stand in for the operator console. These three steps set up NdeipiCoin:

1. Put Ndeipi's own dollars in the treasury:

   ```bash
   dotnet run --project src/Ndeipi.Payments -- treasury-deposit absa_eft usd 20000 "Capital"
   ```

2. Record a settled trade with Blockfinex's OTC desk. This posts both legs and sets the NdeipiCoin price:

   ```bash
   dotnet run --project src/Ndeipi.Payments -- record-otc-trade buy 10000 1100 ABSA-REF DESK-REF "your name"
   ```

3. Have the treasury buy points, so it can pay users who sell NdeipiCoin back:

   ```bash
   dotnet run --project src/Ndeipi.Payments -- treasury-buy-points usd 1000
   ```

Development migrates the LocalDB database `NdeipiPayments` on startup. Production refuses to
start on the simulated providers.

## Business banking (Meow)

Treasury officers use it from the Ndeipi app's Business Banking sub-app; the chat API passes their
requests here with a gateway key that names the officer, and only this server talks to Meow.
Development runs on a simulated bank (`appsettings.Development.json`). To use Meow's sandbox, keep
the keys in user-secrets, never in a file:

```bash
dotnet user-secrets set "Payments:Banking:Provider" "meow" --project src/Ndeipi.Payments
dotnet user-secrets set "Payments:Banking:Meow:ApiKey" "<sandbox key>" --project src/Ndeipi.Payments
dotnet run --project src/Ndeipi.Payments -- meow-check
dotnet run --project src/Ndeipi.Payments -- meow-subscribe https://<public host>/webhooks/meow
dotnet user-secrets set "Payments:Banking:Meow:WebhookSecret" "<whsec_ printed above>" --project src/Ndeipi.Payments
```

The Meow key needs the scopes `accounts:read`, `accounts:balances`, `accounts:transactions`,
`contacts:read`, `contacts:write`, `transfers:ach:write`, `transfers:wire:write`,
`transfers:international:write`, `transfers:crypto:write`, `cards:read`, `cards:write`,
`webhooks:read` and `webhooks:write`. International payments and card number retrieval need Meow to
turn them on for the account. The assistant needs `Payments:Banking:Assistant:ApiKey` (an
Anthropic key); production also needs OFAC's `sdn.csv` at `Payments:Banking:Sanctions:SdnCsvPath`.

Then give the chat API its way in, and give officers the `treasury` role:

```bash
dotnet run --project src/Ndeipi.Payments -- issue-gateway-key "Ndeipi app"
dotnet user-secrets set "Treasury:PaymentsBaseUrl" "http://localhost:5180" --project src/NdeipiChat.Api
dotnet user-secrets set "Treasury:GatewayKey" "<nd_op_ key printed above>" --project src/NdeipiChat.Api
```

## Tests

```bash
dotnet test tests/Ndeipi.Payments.Tests
```

Each test class gets its own LocalDB database, created and dropped by `PaymentsApp`.
