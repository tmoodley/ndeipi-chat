# Ndeipi Lending: portal requirements (DRAFT)

**Drafted by Claude, not taken from your spec.** The RWA Lending Platform Portal SRS (Oct 29, 2026) says
each portal has its own tab, and those tabs don't exist. You asked for them to be planned and built, so
these BRK-, LND- and BOR- requirements are worked out from the shared tab's roles, lifecycle, contract
functions and waterfall. Please correct anything here; the code follows this document.

Everything is **simulated**: money is a test balance, no contract is deployed and nothing is lent. All
three portals are public, like Ndeipi RWA, and stay simulated through the `Lending:Mode` gate.

"Shall" means built in this PR. "Later" means drafted but not built, with the reason.

## Broker Portal (`apps/lending-broker`)

Who: the Broker / Originator. Derived from lifecycle steps 1, 2 and 5, and from SC-FEE-01 and REG-04.

| ID | Requirement | Acceptance |
|---|---|---|
| BRK-FR-01 | A broker shall create a deal with its terms, tranche targets, offering basis and drawdown plan. | A verified user creates a deal; it starts in Draft, and they're its broker. An unverified user is refused. |
| BRK-FR-02 | A broker shall edit terms until they submit the deal. | Edits save in Draft; once submitted they're refused until the underwriter returns it. |
| BRK-FR-03 | A broker shall invite the developer and name the trustee, servicer, inspector and valuer by email. | Each role holds one person. Servicer and trustee can't be the same person, and the developer can't hold a role that checks their work. |
| BRK-FR-04 | A broker shall add diligence to the data room, as a hash and version. | A new file with the same name becomes the next version. The file stays on the device (PLT-DOC-01). |
| BRK-FR-05 | A broker shall see their pipeline: every deal, its state, funding, and what's still outstanding. | Each deal lists a checklist (data room, roles, KYB, submitted, approved, land charge, tranches full), ticked from live data. |
| BRK-FR-06 | A broker shall submit a deal to the underwriter, who approves, rejects or returns it. | Approval is refused until submitted. A return carries a reason the broker sees, and the deal can be edited again. |
| BRK-FR-07 | A broker shall see a fee statement: the fee taken at source on each draw. | The statement matches each draw's broker fee, paid to the wallet fixed at approval (SC-FEE-01). |
| BRK-FR-08 | The portal shall say broker compensation is under review. | A notice shows on the portal until counsel clears REG-04. |

## Investor / Lender Portal (`apps/lending-lender`)

Who: Senior and Junior lenders. Derived from lifecycle step 4, SC-VLT-01, SC-VLT-02, PLT-CMP and REG-01.

| ID | Requirement | Acceptance |
|---|---|---|
| LND-FR-01 | A lender shall see every open pool, its terms and how full each tranche is. | Deals in Funding appear; earlier ones stay private. |
| LND-FR-02 | The portal shall say plainly why someone can't lend to a deal. | KYC, classification, frozen, offering basis and country each give their own reason. |
| LND-FR-03 | A lender shall deposit into the senior or junior tranche, up to its target and the deal's holding limit. | A retried request deposits once. Over-target and over-limit are refused. |
| LND-FR-04 | The portal shall disclose that junior takes losses first before a deposit. | The notice shows beside the deposit control. |
| LND-FR-05 | A lender shall see their portfolio: deposited, interest and principal received, losses, share price and value. | Totals add up across tranches and deals, and share price falls only per SC-VLT-02. |
| LND-FR-06 | A lender shall see a deal's risk: LTV, health factor, attestations, document hashes, payments and audit log. | All show on the deal page; a margin event and a stale valuation show as warnings. |
| LND-FR-07 | A lender shall request a redemption and redeem it in a window (SC-VLT-01). No instant withdrawal. | The window opens 7 days after the request and closes 14 days later. It pays only from money the drawdown plan doesn't need, and never in default. |
| LND-FR-08 | A lender shall get an in-app notice of each change affecting them (PLT-NTF-01). | Pool active, draws, payments, grace and default each post a notice. Email is Later. |

## Borrower Portal (`apps/lending-borrower`)

Who: the Project Developer. Derived from lifecycle steps 2, 5, 6, 7 and 8, SC-LN-01 and PLT-ORC-02.

| ID | Requirement | Acceptance |
|---|---|---|
| BOR-FR-01 | A developer shall see each loan they're on: state, drawn, what's due, health and next draw. | The numbers match the deal page, up to the second. |
| BOR-FR-02 | The portal shall show their KYB status and what it holds up. | Until Compliance approves, the portal says so, and approval is refused (lifecycle step 2). |
| BOR-FR-03 | A developer shall lock the collateral once the loan is active. | Only the deal's developer can. Draws need it (SC-LN-01). |
| BOR-FR-04 | A developer shall draw the next tranche, seeing every reason it's blocked. | All SC-LN-01 and PLT-ORC-02 reasons are listed; fees come off at source (SC-FEE-01). |
| BOR-FR-05 | A developer shall ask the inspector to attest a milestone. | The inspector gets a notice, and the request is in the audit log. |
| BOR-FR-06 | A developer shall repay from their balance through the waterfall. | The same order as servicer payments; once nothing is owed and nothing is left to draw, the loan is Repaid and the collateral released. |
| BOR-FR-07 | A developer shall be warned of a margin event, grace or default. | A notice and a warning on the deal. |
| BOR-FR-08 | **Later:** a fractional holder shall borrow against their tokens. | Your SRS lists this as an open issue, so it's deferred until a secondary market exists. The portal says so. |

## Attestors and the platform

The SRS's Attestation app and Admin console aren't portals, but the lifecycle needs them. In this PR:
inspector, valuer and trustee sign on the deal page, and underwriter and Compliance controls appear in
the Broker Portal and on the deal page for those roles.

## Where the SRS and this repo differ

| SRS | What this PR does |
|---|---|
| PLT-WAL-01/02: ERC-4337 wallets, WalletConnect, institutional custody | Custodial wallets stay, using the SRS's assumption that Ndeipi Enterprise Server supplies them. Counsel should review it, since Ndeipi would hold lender funds. |
| PLT-ID-02/03: Sumsub or Persona, KYB, daily sanctions screening | Bridge KYC for people. Compliance records KYB, investor class, country and freezes by hand. No sanctions feed yet. |
| PLT-CMP-01: ERC-3643 tokens, per-deal contract set | No Solidity. The server enforces the same rules. |
| Waterfall step 7, read literally, pays junior principal before senior in default | In default, senior principal is swept ahead of junior principal, to keep SC-VLT-02 true. Counsel should confirm. |
| PLT-DOC-02: e-signature bound to the on-chain action | A signed agreement's hash is recorded. No e-signature provider yet. |
| Scheduled senior principal | No amortisation table is specified, so a deal sets a fixed amount per payment. |
