# Payments API contract (draft)

[`openapi.yaml`](openapi.yaml) is the draft contract for the Ndeipi Enterprise Server payments API
in "SRS: Ndeipi Enterprise Server Payments API and SDK" (2026-10-06). It is the M0 exit artifact:
once approved, the server validates against it and the SDKs are generated from it (DC-01, SC-01).

Every operation lists the SRS requirements it implements in `x-requirements` and its priority in
`x-priority`, so requirement-to-test tracing can start from the file.

## Decisions to approve

The SRS left these open or inconsistent. The draft picks one answer for each; reviewers should
confirm or change them.

| # | Decision in the draft | Why |
|---|---|---|
| 1 | Webhooks are signed with **Ed25519** over `<t>.<raw body>`. `t` is the **delivery attempt** time, not the event time. | The SRS retries for 2 days but the SDK rejects anything older than 10 minutes. Signing the event time would make every late retry fail. Ed25519 is one standard sign call with no pre-hashing (FR-WH-03). |
| 2 | **One `TransferState` enum** for all kinds, with each kind's path documented on the schema. | DC-04 requires one transfer model; the SRS gave three separate state lists. |
| 3 | Every mutable object has an integer **`version`**, and events carry `object_version`. | Webhooks arrive out of order. Receivers need a way to drop stale snapshots. |
| 4 | **Amount precision** is published per asset and currency on `GET /routes`. Extra decimal places return `422 invalid_amount_precision`. Caller amounts are never rounded. Computed amounts round half-even. | The SRS required decimal strings but set no precision or rounding rule. |
| 5 | **Deactivating a user** is refused while any wallet holds a balance or a transfer is open. | The SRS did not say what happens to the funds of a deactivated user. |
| 6 | An **on-ramp credits whatever arrives**, recorded as `amount_expected` and `amount_received`. Below the route minimum, the funds are returned. | The SRS did not cover over- or under-payment. |
| 7 | **Each standing-account deposit creates an `onramp` transfer.** | Deposits then share states, receipts and events with one-off on-ramps instead of being a second model. |
| 8 | **One wallet per user per asset** in 1.0. | This makes "the user's default wallet" for `transfers.send` unambiguous. |
| 9 | Key prefixes are `nd_test_` and `nd_live_`. ID prefixes are `usr_`, `wal_`, `trf_` and so on, followed by a ULID. | FR-CORE-03 requires keys to be distinguishable; prefixed IDs make logs readable. |
| 10 | Wire fields are `snake_case`. SDKs expose idiomatic names, such as camelCase in TypeScript. | This matches the reference API's conventions without copying its names (LEG-04 is still open). |

## Providers and Ndeipi Points

Decided on 2026-10-06:

- **Fiat moves on Absa Bank and PayPal.**
- **Wallets hold Ndeipi Points**, not crypto. Users buy points with fiat at a fixed price per
  currency and can cash them out the same way.
- **On-ramp and off-ramp never touch Blockfinex.** Its API
  ([docs](https://exchangedocsv2.gitbook.io/open-api-doc-v2)) is crypto only, and it stays
  configured only for crypto features if any are added (`Payments:Exchange`, `none` by default).

| Provider | Rail code | Pays in | Pays out |
|---|---|---|---|
| Absa Bank | `absa_eft` | Standing and one-off deposit details, matched by reference | EFT to a bank account |
| PayPal | `paypal` | One-off only, through a hosted approval link | Payouts to a PayPal email or phone |

How money and points move:

- **On-ramp:** the user pays ZAR into Ndeipi's Absa account, or pays through PayPal. The server
  credits points at that currency's fixed price (`GET /rates` returns it with `type: fixed`).
- **User to user:** points move on the ledger alone, with no provider involved.
- **Off-ramp:** points are debited at the same fixed price and the fiat is paid out from Ndeipi's
  Absa or PayPal balance.
- **Rounding:** points bought and fiat paid out both round *down*, an exception to decision 4.
  Rounding up, even by a fraction of a cent, could pay out more than came in once enough partial
  cash-outs add up.
- **Ledger:**
  - The fiat sits in a clearing account per provider and currency, and reconciliation compares
    each one with that provider's statement (SRV-PROV-06).
  - Points outstanding are a liability account.
  - Every on-ramp posting moves both, so the fiat held always covers the points that can be cashed
    out.
- **Rebalancing:** if a user pays in through PayPal and cashes out through Absa, money has to move
  between Ndeipi's own balances. That is a treasury operation outside the API. M4 needs a minimum
  balance per provider and an operator alert before one runs dry.

**Purchased and earned points.** The app's Believe Points become Ndeipi Points. The user sees one
balance, but the ledger keeps two kinds:

- **Purchased** points, bought with fiat, can be cashed out.
- **Earned** points, from rewards, can be spent and sent but never cashed out.

Spending uses earned points first. `WalletBalance.cashable` shows how much can be cashed out, and
an off-ramp above it returns `422 amount_exceeds_cashable`. Without this split, rewards Ndeipi gave
away for free would become money users can withdraw. That invites farming, and Ndeipi would owe
cash it never received. *This is a proposal to confirm.*

What this changes in the contract:

- `ndeipi-points` is the only wallet asset in 1.0.
- `GET /rates` returns the fixed price (`type: fixed`).
- Wallet balances gain `cashable`.
- Payout accounts gain a `paypal` type, and deposit instructions gain a `paypal` variant with an
  `approval_url`.
- Standing deposit accounts exist only on bank rails.
- No `mobile_money` rail has a provider yet. The type stays in the contract for when one is added.

Risks and dependencies:

- **Cashable points are stored value.** Points that users buy with money, send to each other and
  redeem for money generally fall under e-money or payment-service rules. Counsel should confirm
  which licence or partner covers this in each market before production keys are issued (LEG-07,
  LEG-11). Holding the fiat 1:1 against cashable points, which the ledger enforces, is what such
  rules usually expect.
- **PayPal's Acceptable Use Policy** needs pre-approval for "any digital representation of value
  that can be digitally traded, transferred, or used for payment", which explicitly includes virtual
  in-game currencies, not just crypto. Points users can send and spend most likely fall under it,
  so ask PayPal early.
- **Absa's API documentation is not public.** The interface (Absa Access API or host-to-host
  files), payment-reference matching, statement access, payout idempotency and supported countries
  all wait on onboarding with Absa Corporate and Investment Banking.
- **Price changes.** A fixed price that changes moves the fiat value of points already held. The
  rule for announcing a change, and whether points bought at the old price cash out at that price,
  needs deciding before launch.

## NdeipiCoin (opt-in)

Decided on 2026-10-06: NdeipiCoin is a second wallet asset (`ndeipi-coin`) that users choose to
hold. Ndeipi Points stay the default.

**How users get it.** A user converts points to NdeipiCoin, or back, at a locked quote:
`POST /quotes`, then `POST /transfers` with that `quote_id` as a `conversion`. Rules:

- **Fiat never buys NdeipiCoin directly.** It buys points, which can then be converted.
- **Only cashable (purchased) points can buy NdeipiCoin.** Otherwise free reward points could be
  converted to NdeipiCoin, sold back for cashable points and withdrawn as cash.
- **Points from selling NdeipiCoin are cashable.**
- **Opting in is explicit.** A NdeipiCoin wallet needs `acknowledge_price_risk: true`, and the time
  is recorded.

**Where the coin comes from.** Conversions settle from Ndeipi's own NdeipiCoin stock in one ledger
transaction, so they never wait on the desk. The stock is topped up, or sold down, through
Blockfinex's OTC desk, which works manually for now:

1. Ndeipi's treasury team agrees a trade with the desk.
2. The USD leg settles through Ndeipi's USD account at Absa Bank.
3. The NdeipiCoin leg settles into Ndeipi's Blockfinex account.
4. Once both legs have landed, the team records the trade:
   `dotnet run --project src/Ndeipi.Payments -- record-otc-trade buy <coin> <usd> <absa ref> <desk ref> <name>`.
   The server never calls the desk.

**Price.** The last recorded trade sets the price: its USD per coin, divided by the points' own USD
price (`Payments:Points:Prices:usd`), gives points per coin.

- **Spread.** Each quote adds Ndeipi's spread (`Payments:Coin:SpreadBasisPoints`, 150 by default),
  charged in the asset paid and rounded up. What the user receives rounds down, so converting there
  and back never returns more than went in.
- **Staleness.** If no trade has been recorded, or the last is older than
  `Payments:Coin:MaxPriceAge` (3 days by default), quotes answer `503 provider_unavailable` rather
  than quote a stale price.

**Ledger.** Customer money never buys NdeipiCoin, and the points reserve is never touched by a
conversion.

- **Buying.** When a user converts points to coin, the points move into Ndeipi's own **treasury
  points** account, less the spread, which goes to **fees**. The coin comes out of Ndeipi's
  **inventory**. The points still exist, and Ndeipi now holds them, so the reserve still backs them.
- **Selling.** Selling coin back runs the other way: the coin goes into inventory, and cashable
  points come out of the treasury's points.
- **Topping up.** The treasury tops up its points by buying them at the fixed price, like anyone
  else (`treasury-buy-points`), so that fiat also lands in the reserve.
- **OTC trades.** Recording an OTC trade posts both legs. Treasury USD leaves through Ndeipi's Absa
  float, and NdeipiCoin arrives in inventory through a `blockfinex_otc` clearing account. So the
  treasury needs the USD first (`treasury-deposit`).
- **Liquidity.** Inventory, treasury points and the reserve may not go negative. A conversion
  either can't cover returns `422 insufficient_liquidity`, checked when quoting and again,
  atomically, when settling.

So the reserve always covers every purchased point 1:1, and NdeipiCoin's price risk sits only with
users who opted in and with Ndeipi's treasury.

**House accounts.** The float at each provider, the reserve, the treasury, the inventory and fees
belong to a reserved house owner, not to any integrator. One posting may touch an integrator's
wallets and house accounts together. Treasury operations with no integrator scope touch only house
accounts, and no API key can read them.

**Risks:**

- **Ndeipi sells its own token to its users.** Ndeipi is the issuer, the dealer through its
  Inventory and the price-setter through its OTC trades. Counsel needs to confirm the licensing for
  dealing in a crypto asset in each market (in South Africa, crypto-asset services need FSCA
  authorisation), and how to manage the conflict of interest. Recording every trade and every quote
  helps show prices weren't moved for Ndeipi's benefit.
- **Thin market.** Users selling back depend on Ndeipi's Treasury, and the desk's appetite to
  buy, at a fair price. A selling rush can exhaust Treasury. `insufficient_liquidity` protects the
  points reserve, but users can't exit until it recovers.
- **Price set manually.** Between trades the price doesn't move, and the market might. The 3-day
  limit bounds that, but the treasury team should trade, or at least re-record, often enough to keep
  quotes honest.
- **PayPal and Absa.** PayPal-funded points that can become NdeipiCoin are an indirect crypto
  purchase. Raise this explicitly in PayPal's pre-approval, and tell Absa about the USD flows to
  the OTC desk.

## Not decided here

- **Business users before associated persons ship.** FR-USER-01 allows business users at P0, but
  owners and controllers are P1 (FR-USER-10). Compliance should decide whether business users can
  move money in 1.0 without them. The draft does not block it.
- **Rate-limit numbers.** These are set per integrator. The contract fixes only the headers and
  the `429` behaviour.
- **Which currencies and countries each rail supports, and the points price in each currency.**
  These are data on `GET /routes` and `GET /rates`. They wait on Absa onboarding, PayPal's approval
  and a pricing decision.
- **P1 and P2 scope** beyond a few placeholders: transfer cancel, deposit-account reactivate and
  the webhook test call. Associated persons, integrator fees, locked quotes, compliance submissions
  and standing payout addresses are not drafted yet.
