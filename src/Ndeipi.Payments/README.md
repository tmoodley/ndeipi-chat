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
| `Users/` | Users and KYC | Create, get, list, update, onboarding links and KYC and terms status changes are built (M2). Deactivate comes with wallets in M3. |
| `Ledger/` | Ledger and wallets | The ledger is built (M1). Wallet endpoints are M3. |
| `Transfers/` | One transfer resource for all three money movements | The state machine is defined. Endpoints are M3 (user-to-user) and M4 (ramps). |
| `Ramps/` | Deposit accounts, payout accounts, routes, rates | M4 |
| `Providers/` | Fiat rails (PayPal, Absa), the exchange (Blockfinex), and the registry that routes by rail code | The interfaces, registry and simulated providers are built. The real adapters are M4. |
| `Treasury/` | NdeipiCoin: manual OTC trades with Blockfinex, and conversion pricing | The trade record and pricing are built. Quotes, conversions and house-account postings are M4. |
| `Reconciliation/` | Ledger against provider | The balance check is built. The schedule and alerts are M4. |
| `Webhooks/` | Endpoints, events, signed delivery | Built (M2): endpoints with their own keys, the events API, redelivery, and a dispatcher that retries with backoff and connects only to public addresses. The test call is M6. |
| `Sandbox/` | Simulated KYC, deposits, payout outcomes | `sandbox_only` and the KYC call are built. Deposits and payout outcomes are M4. |

Every operation in the contract is mapped. Ones that are not built yet answer
`501 not_implemented` and name their milestone. `ContractTests` fails if the routes and
openapi.yaml drift apart.

## Run it

```bash
dotnet run --project src/Ndeipi.Payments -- issue-key "Local dev"
dotnet run --project src/Ndeipi.Payments
curl -H "Api-Key: nd_test_..." http://localhost:5180/v1/users
```

To record a settled trade with Blockfinex's OTC desk, which sets the NdeipiCoin price:

```bash
dotnet run --project src/Ndeipi.Payments -- record-otc-trade buy 10000 1100 ABSA-REF DESK-REF "your name"
```

Development migrates the LocalDB database `NdeipiPayments` on startup. Production refuses to
start on the simulated providers.

## Tests

```bash
dotnet test tests/Ndeipi.Payments.Tests
```

Each test class gets its own LocalDB database, created and dropped by `PaymentsApp`.
