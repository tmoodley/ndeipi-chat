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
| `Users/` | Users and KYC | Built: create, get, list, update, onboarding links, KYC and terms status changes (M2), and deactivation (M3). |
| `Ledger/` | Ledger, wallets and house accounts | Built: wallets (M3); house accounts for Ndeipi's floats, reserve, treasury, NdeipiCoin stock and fees (M4). |
| `Transfers/` | One transfer resource for every money movement | Built: user-to-user (M3), conversions, on-ramps and off-ramps (M4). Cancel is M6. |
| `Ramps/` | Deposit accounts, payout accounts, routes, rates, ramp worker | Built (M4) on the sandbox rails. The real Absa and PayPal adapters wait on onboarding. |
| `Providers/` | Fiat rails (PayPal, Absa), the exchange (Blockfinex), and the registry that routes by rail code | The interfaces, registry and simulated providers are built. The real adapters wait on Absa onboarding, PayPal approval and Blockfinex access. |
| `Treasury/` | NdeipiCoin: quotes, conversions, manual OTC trades, treasury operations | Built (M4). |
| `Reconciliation/` | Ledger against provider | The balance check is built. A daily schedule, per-movement matching and alerts are not built yet. |
| `Webhooks/` | Endpoints, events, signed delivery | Built (M2): endpoints with their own keys, the events API, redelivery, and a dispatcher that retries with backoff and connects only to public addresses. The test call is M6. |
| `Sandbox/` | Simulated KYC, deposits, payout outcomes | Built. |

Every operation in the contract is mapped. Ones that are not built yet answer
`501 not_implemented` and name their milestone. `ContractTests` fails if the routes and
openapi.yaml drift apart.

## Run it

```bash
dotnet run --project src/Ndeipi.Payments -- issue-key "Local dev"
dotnet run --project src/Ndeipi.Payments
curl -H "Api-Key: nd_test_..." http://localhost:5180/v1/users
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

## Tests

```bash
dotnet test tests/Ndeipi.Payments.Tests
```

Each test class gets its own LocalDB database, created and dropped by `PaymentsApp`.
