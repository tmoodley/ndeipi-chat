using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ndeipi.Payments.Api;
using Ndeipi.Payments.Data;
using Ndeipi.Payments.Ops;

namespace Ndeipi.Payments.Banking;

/// <summary>
/// Ndeipi's treasury at its business bank: balances, payees, payments and cards
/// (SRS-NDE-MEOW-001 FR-1 to FR-4).
///
/// A payment goes through Ndeipi's own steps before the bank sees it:
/// 1. one per Idempotency-Key (FR-3.2): a repeated request gets the first payment back;
/// 2. sanctions screening of the payee (NFR-CMP-01): a match blocks it for good;
/// 3. at or over the threshold, approval by officers other than the one who asked (FR-3.3).
/// Then it is dispatched with its own ID as the bank's idempotency key, so retries after a timeout
/// or an outage send it once. The bank's webhooks report what happens next.
/// </summary>
public sealed class BankingService(
    PaymentsDbContext db, IBusinessBank bank, SanctionsScreen sanctions, IOptions<PaymentsOptions> options, TimeProvider clock,
    ILogger<BankingService> log)
{
    BankingOptions Settings => options.Value.Banking;
    DateTimeOffset Now => clock.GetUtcNow();

    public BankingSettingsDto SettingsDto(bool assistantEnabled) => new(
        Settings.Provider, Settings.ApprovalThreshold, Settings.RequiredApprovals, Settings.ApprovalLifetime,
        Settings.Meow.AllowCardReveal, assistantEnabled, BankStatuses.CardCategories);

    // ------------------------------------------------------------------ accounts and statements

    /// <summary>
    /// The accounts and balances. Served from the cache while fresh; otherwise the bank is asked,
    /// and if it can't be reached the cached balances come back marked stale (NFR-PERF-03).
    /// </summary>
    public async Task<IReadOnlyList<BankAccountDto>> AccountsAsync(bool refresh, CancellationToken ct)
    {
        var cached = await db.TreasuryAccounts.AsNoTracking().OrderBy(a => a.Name).ToListAsync(ct);
        if (!refresh && cached.Count > 0 && cached.All(a => Now - a.RefreshedAt < Settings.BalanceFreshness))
            return [.. cached.Select(a => ToDto(a, stale: false))];

        IReadOnlyList<BankAccountInfo> live;
        try
        {
            live = await bank.ListAccountsAsync(ct);
        }
        catch (BankUnavailableException) when (cached.Count > 0)
        {
            return [.. cached.Select(a => ToDto(a, stale: true))];
        }

        var rows = await db.TreasuryAccounts.ToDictionaryAsync(a => a.Id, ct);
        foreach (var a in live)
        {
            if (!rows.TryGetValue(a.Id, out var row))
            {
                row = new TreasuryAccount { Id = a.Id, Name = a.Name, Type = a.Type, Currency = a.Currency, Status = a.Status };
                db.TreasuryAccounts.Add(row);
            }
            (row.Name, row.Type, row.Last4, row.Currency, row.Status, row.Available, row.Current, row.Bank, row.RefreshedAt) =
                (a.Name, a.Type, a.Last4, a.Currency, a.Status, a.Available, a.Current, a.Bank, Now);
        }
        // Accounts the bank no longer lists are left in the cache but not shown.
        await db.SaveChangesAsync(ct);
        var ids = live.Select(a => a.Id).ToHashSet();
        return [.. db.TreasuryAccounts.Local.Where(a => ids.Contains(a.Id)).OrderBy(a => a.Name).Select(a => ToDto(a, stale: false))];
    }

    public async Task<BankTransactionPageDto> TransactionsAsync(
        string accountId, DateTimeOffset? from, DateTimeOffset? to, string? cursor, int limit, CancellationToken ct)
    {
        var page = await Bank(() => bank.ListTransactionsAsync(accountId, from, to, cursor, limit, ct));
        return new BankTransactionPageDto([.. page.Transactions.Select(ToDto)], page.NextCursor);
    }

    /// <summary>
    /// A statement of one account between two times as CSV (FR-2.3). Meow publishes no statement
    /// endpoint in its API, so this is built from the transaction history.
    /// </summary>
    public async Task<string> StatementCsvAsync(string accountId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var csv = new StringBuilder("date,posted,description,counterparty,direction,amount,currency,status,type\n");
        string? cursor = null;
        var rows = 0;
        do
        {
            var page = await Bank(() => bank.ListTransactionsAsync(accountId, from, to, cursor, 100, ct));
            foreach (var t in page.Transactions)
            {
                csv.Append(string.Join(',',
                    t.At.ToString("O", CultureInfo.InvariantCulture), t.PostedAt?.ToString("O", CultureInfo.InvariantCulture) ?? "",
                    Csv(t.Description), Csv(t.Counterparty ?? ""), t.Credit ? "credit" : "debit",
                    t.Amount.ToString("0.00", CultureInfo.InvariantCulture), t.Currency, t.Status, t.Type ?? "")).Append('\n');
                rows++;
            }
            cursor = page.NextCursor;
        }
        while (cursor is not null && rows < 10_000);
        return csv.ToString();

        // Quoted, and a leading = + - @ neutralised, so a spreadsheet never runs a description as a formula.
        static string Csv(string value)
        {
            if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@')
                value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }

    // ------------------------------------------------------------------ payees

    public async Task<IReadOnlyList<PayeeDto>> PayeesAsync(CancellationToken ct) =>
        [.. (await db.Payees.AsNoTracking().Where(p => p.ArchivedAt == null).ToListAsync(ct)).OrderBy(p => p.Name).Select(ToDto)];

    public async Task<PayeeDto> CreatePayeeAsync(Officer officer, PayeeCreateRequest r, CancellationToken ct)
    {
        var (kind, request) = ValidatePayee(r);
        var name = request.Name;
        // Screened before Ndeipi or the bank keeps anything about them.
        if (sanctions.Check(name, request.Crypto?.Address) is { } hit)
        {
            await JournalAsync(InitiatorType.Officer, officer.Name, "payee.blocked", null, hit, ct: ct);
            throw PaymentsException.Forbidden("sanctions_match", hit + " Compliance must review this before anyone pays them.");
        }

        var created = await Bank(() => bank.CreatePayeeAsync(request, ct));
        var payee = new Payee
        {
            Id = Ids.New("pye", clock),
            Kind = kind,
            Name = name,
            Display = Display(kind, request),
            Country = request.International?.BankCountry ?? request.Bank?.Address.Country,
            Currency = request.International?.Currency,
            Address = request.Crypto?.Address,
            ProviderContactId = created.ContactId,
            ProviderPayeeId = created.PayeeId,
            CreatedBy = officer.Name,
            CreatedAt = Now
        };
        db.Payees.Add(payee);
        Journal(InitiatorType.Officer, officer.Name, "payee.created", payee.Id, payee.Display);
        await db.SaveChangesAsync(ct);
        return ToDto(payee);
    }

    public async Task ArchivePayeeAsync(Officer officer, string id, CancellationToken ct)
    {
        var payee = await db.Payees.FirstOrDefaultAsync(p => p.Id == id && p.ArchivedAt == null, ct) ?? throw PaymentsException.NotFound("payee");
        payee.ArchivedAt = Now;
        Journal(InitiatorType.Officer, officer.Name, "payee.archived", payee.Id, payee.Display);
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------ payments

    public async Task<IReadOnlyList<PaymentDto>> PaymentsAsync(PaymentStatus? status, int limit, CancellationToken ct)
    {
        var query = db.TreasuryPayments.AsNoTracking();
        if (status is not null)
            query = query.Where(p => p.Status == status);
        var payments = await query.OrderByDescending(p => p.CreatedAt).Take(Math.Clamp(limit, 1, 200)).ToListAsync(ct);
        return await ToDtosAsync(payments, ct);
    }

    public async Task<PaymentDto> PaymentAsync(string id, CancellationToken ct) =>
        (await ToDtosAsync([await db.TreasuryPayments.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw PaymentsException.NotFound("payment")], ct))[0];

    /// <summary>
    /// An officer asks to pay someone. <paramref name="requestKey"/> is the caller's Idempotency-Key:
    /// the same key returns the same payment, however many times or however concurrently it is sent.
    /// </summary>
    public async Task<PaymentDto> CreatePaymentAsync(Officer officer, string? requestKey, PaymentCreateRequest r, CancellationToken ct)
    {
        if (requestKey is not { Length: > 0 and <= 255 })
            throw PaymentsException.BadRequest("Every payment needs an Idempotency-Key header (up to 255 characters).", IdempotencyMiddleware.Header);
        if (await ExistingAsync(requestKey, ct) is { } existing)
            return await ReplayAsync(existing, officer, r, ct);

        var payment = await NewPaymentAsync(r, requestKey, InitiatorType.Officer, officer, sessionId: null, model: null, ct);
        return await SubmitAsync(payment, officer, ct);
    }

    /// <summary>
    /// The assistant drafts a payment (FR-5.2). It waits for the officer to confirm; nothing reaches
    /// the bank until they do.
    /// </summary>
    public async Task<PaymentDto> DraftPaymentAsync(Officer officer, string sessionId, string model, string draftKey, PaymentCreateRequest r, CancellationToken ct)
    {
        if (await ExistingAsync(draftKey, ct) is { } existing)
            return (await ToDtosAsync([existing], ct))[0];
        var payment = await NewPaymentAsync(r, draftKey, InitiatorType.Agent, officer, sessionId, model, ct);
        payment.Status = PaymentStatus.AwaitingConfirmation;
        db.TreasuryPayments.Add(payment);
        Journal(InitiatorType.Agent, officer.Name, "payment.drafted", payment.Id, Summary(payment), sessionId, model);
        await SaveNewAsync(payment, ct);
        return (await ToDtosAsync([payment], ct))[0];
    }

    /// <summary>The officer confirms the assistant's draft; from here it is checked and approved like any other payment.</summary>
    public async Task<PaymentDto> ConfirmDraftAsync(Officer officer, string id, CancellationToken ct)
    {
        var payment = await db.TreasuryPayments.FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw PaymentsException.NotFound("payment");
        if (payment.Status != PaymentStatus.AwaitingConfirmation)
            throw PaymentsException.Conflict("invalid_state", $"This payment is {WireEnum.Name(payment.Status)}, not awaiting confirmation.");
        if (payment.InitiatedById != officer.Id)
            throw PaymentsException.Forbidden("not_yours", "Only the officer the assistant drafted this for can confirm it.");
        Journal(InitiatorType.Officer, officer.Name, "payment.confirmed", payment.Id, Summary(payment), payment.AgentSessionId, payment.AgentModel);
        return await SubmitAsync(payment, officer, ct);
    }

    public async Task<PaymentDto> ApproveAsync(Officer officer, string id, ApproveRequest r, CancellationToken ct)
    {
        var payment = await PendingAsync(id, ct);
        if (payment.InitiatedById == officer.Id)
            throw PaymentsException.Forbidden("four_eyes", "A payment must be approved by an officer other than the one who asked for it.");
        if (r.TermsDigest != Digest(payment))
            throw PaymentsException.Conflict("terms_changed", "These are not the terms of this payment. Reload it and approve what is shown.", field: "terms_digest");

        // This approval, not yet saved, plus the earlier ones.
        var approvals = await db.PaymentApprovals.CountAsync(a => a.PaymentId == id && a.Decision == ApprovalDecision.Approve, ct) + 1;
        await DecideAsync(payment, officer, ApprovalDecision.Approve, r.Note, ct);
        if (approvals < payment.RequiredApprovals)
        {
            Journal(InitiatorType.Officer, officer.Name, "payment.approval_added", payment.Id, $"{approvals} of {payment.RequiredApprovals} approvals");
            await SaveAsync(ct);
            return await PaymentAsync(id, ct);
        }

        payment.Status = PaymentStatus.Approved;
        payment.NextDispatchAt = Now;
        payment.Version++;
        Journal(InitiatorType.Officer, officer.Name, "payment.approved", payment.Id, $"{approvals} of {payment.RequiredApprovals} approvals");
        await SaveAsync(ct);
        await DispatchAsync(payment, ct);
        return await PaymentAsync(id, ct);
    }

    public async Task<PaymentDto> RejectAsync(Officer officer, string id, string? reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw PaymentsException.Validation([new("reason", "required", "Say why; the officer who asked sees it.")]);
        var payment = await PendingAsync(id, ct);
        await DecideAsync(payment, officer, ApprovalDecision.Reject, reason.Trim(), ct);
        payment.Status = PaymentStatus.Rejected;
        payment.FailureReason = $"Rejected by {officer.Name}: {reason.Trim()}";
        payment.CompletedAt = Now;
        payment.Version++;
        Journal(InitiatorType.Officer, officer.Name, "payment.rejected", payment.Id, reason.Trim());
        await SaveAsync(ct);
        return await PaymentAsync(id, ct);
    }

    /// <summary>Withdraws a payment the bank has not been sent: a draft, one awaiting approval, or one still waiting for the bank to come back.</summary>
    public async Task<PaymentDto> CancelAsync(Officer officer, string id, CancellationToken ct)
    {
        var payment = await db.TreasuryPayments.FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw PaymentsException.NotFound("payment");
        var unsent = payment.Status is PaymentStatus.AwaitingConfirmation or PaymentStatus.PendingApproval
            || (payment.Status == PaymentStatus.Approved && payment.DispatchAttempts > 0 && payment.ProviderApprovalId is null);
        if (!unsent)
            throw PaymentsException.Conflict("invalid_state", "The bank already has this payment; cancel it in the bank's dashboard if it is still pending there.");
        if (payment.InitiatedById != officer.Id && payment.Status != PaymentStatus.PendingApproval)
            throw PaymentsException.Forbidden("not_yours", "Only the officer who asked for this payment can withdraw it.");
        payment.Status = PaymentStatus.Canceled;
        payment.CompletedAt = Now;
        payment.NextDispatchAt = null;
        payment.Version++;
        Journal(InitiatorType.Officer, officer.Name, "payment.canceled", payment.Id, null, payment.AgentSessionId, payment.AgentModel);
        await SaveAsync(ct);
        return await PaymentAsync(id, ct);
    }

    async Task<TreasuryPayment> NewPaymentAsync(
        PaymentCreateRequest r, string requestKey, InitiatorType initiator, Officer officer, string? sessionId, string? model, CancellationToken ct)
    {
        var problems = new List<ApiErrorDetail>();
        if (r.Rail is null)
            problems.Add(new("rail", "required", "Choose ach, wire, international or crypto."));
        if (string.IsNullOrWhiteSpace(r.AccountId))
            problems.Add(new("account_id", "required", "Choose the account to pay from."));
        if (string.IsNullOrWhiteSpace(r.PayeeId))
            problems.Add(new("payee_id", "required", "Choose who to pay."));
        if (r.Amount is not > 0m || decimal.Round(r.Amount.Value, 2) != r.Amount)
            problems.Add(new("amount", "invalid", "A positive amount in dollars and cents."));
        if (string.IsNullOrWhiteSpace(r.Purpose) || r.Purpose.Trim().Length > 140)
            problems.Add(new("purpose", "required", "Say what the payment is for, in up to 140 characters."));
        if (r.Memo is { Length: > 255 })
            problems.Add(new("memo", "too_long", "At most 255 characters."));
        if (problems.Count > 0)
            throw PaymentsException.Validation(problems);

        var payee = await db.Payees.AsNoTracking().FirstOrDefaultAsync(p => p.Id == r.PayeeId && p.ArchivedAt == null, ct)
            ?? throw PaymentsException.Unprocessable("payee_not_found", "No such payee.", "payee_id");
        var rail = r.Rail!.Value;
        var expected = rail switch
        {
            PaymentRail.Ach => PayeeKind.Ach,
            PaymentRail.Wire => PayeeKind.Wire,
            PaymentRail.International => PayeeKind.International,
            _ => PayeeKind.Crypto
        };
        if (payee.Kind != expected)
            throw PaymentsException.Unprocessable("rail_mismatch", $"This payee is set up for {WireEnum.Name(payee.Kind)} payments, not {WireEnum.Name(rail)}.", "rail");
        var (min, max) = rail switch
        {
            PaymentRail.Ach => (0.01m, 1_000_000m),
            PaymentRail.Wire or PaymentRail.Crypto => (1m, 1_000_000_000m),
            _ => (1m, 1_000_000_000m)
        };
        if (r.Amount < min || r.Amount > max)
            throw PaymentsException.Unprocessable("amount_out_of_range", $"A {WireEnum.Name(rail)} payment is between {min:0.00} and {max:0.00} USD.", "amount");
        var accounts = await AccountsAsync(refresh: false, ct);
        if (accounts.All(a => a.Id != r.AccountId))
            throw PaymentsException.Unprocessable("account_not_found", "No such account at the bank.", "account_id");

        return new TreasuryPayment
        {
            Id = Ids.New("tp", clock),
            Rail = rail,
            AccountId = r.AccountId!,
            PayeeId = payee.Id,
            Amount = r.Amount!.Value,
            RecipientCurrency = rail == PaymentRail.International ? (r.RecipientCurrency ?? payee.Currency)?.ToUpperInvariant() : null,
            Purpose = r.Purpose!.Trim(),
            Memo = string.IsNullOrWhiteSpace(r.Memo) ? null : r.Memo.Trim(),
            RequestKey = requestKey,
            InitiatorType = initiator,
            InitiatedBy = officer.Name,
            InitiatedById = officer.Id,
            AgentSessionId = sessionId,
            AgentModel = model,
            CreatedAt = Now
        };
    }

    /// <summary>Screens, then holds for approval or sends. The payment may be new or a confirmed draft.</summary>
    async Task<PaymentDto> SubmitAsync(TreasuryPayment payment, Officer officer, CancellationToken ct)
    {
        var isNew = db.Entry(payment).State == EntityState.Detached;
        if (isNew)
            db.TreasuryPayments.Add(payment);
        payment.InitiatedBy = officer.Name;
        payment.Version++;

        var payee = await db.Payees.AsNoTracking().SingleAsync(p => p.Id == payment.PayeeId, ct);
        if (sanctions.Check(payee.Name, payee.Address) is { } hit)
        {
            payment.Status = PaymentStatus.Blocked;
            payment.FailureReason = hit;
            payment.CompletedAt = Now;
            Journal(InitiatorType.Officer, officer.Name, "payment.blocked", payment.Id, hit);
        }
        else if (payment.Amount >= Settings.ApprovalThreshold && Settings.RequiredApprovals > 0)
        {
            payment.Status = PaymentStatus.PendingApproval;
            payment.RequiredApprovals = Settings.RequiredApprovals;
            payment.ApprovalExpiresAt = Now + Settings.ApprovalLifetime;
            Journal(InitiatorType.Officer, officer.Name, "payment.requested", payment.Id, Summary(payment) + $"; needs {payment.RequiredApprovals} approval(s)");
        }
        else
        {
            payment.Status = PaymentStatus.Approved;
            payment.NextDispatchAt = Now;
            Journal(InitiatorType.Officer, officer.Name, "payment.requested", payment.Id, Summary(payment));
        }

        if (isNew)
        {
            if (!await SaveNewAsync(payment, ct))
                return await ReplayAsync((await ExistingAsync(payment.RequestKey, ct))!, officer, null, ct);
        }
        else
            await SaveAsync(ct);

        if (payment.Status == PaymentStatus.Approved)
            await DispatchAsync(payment, ct);
        return await PaymentAsync(payment.Id, ct);
    }

    /// <summary>
    /// Sends an approved payment to the bank. The payment's ID is the bank's idempotency key, so a
    /// dispatch that timed out after the bank accepted it is safe to repeat. While the bank is
    /// unreachable the payment stays approved and the worker retries with backoff.
    /// </summary>
    public async Task DispatchAsync(TreasuryPayment payment, CancellationToken ct)
    {
        // Claim it, so two dispatchers never call the bank for it at the same moment.
        payment.NextDispatchAt = Now + TimeSpan.FromMinutes(2);
        payment.DispatchAttempts++;
        payment.Version++;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return;
        }

        var payee = await db.Payees.AsNoTracking().SingleAsync(p => p.Id == payment.PayeeId, ct);
        try
        {
            var sent = await bank.SendAsync(new BankPaymentRequest(
                payment.Id, payment.Rail, payment.AccountId, payee.ProviderPayeeId, payment.Amount, payment.RecipientCurrency, payment.Purpose, payment.Memo), ct);
            payment.ProviderApprovalId = sent.ApprovalId;
            payment.ProviderTransferId ??= sent.TransferId;
            payment.ProviderStatus = sent.Status;
            payment.Status = BankStatuses.ToPayment(sent.Status) ?? PaymentStatus.Processing;
            payment.DispatchedAt = Now;
            payment.NextDispatchAt = null;
            payment.FailureReason = null;
            if (BankStatuses.IsFinal(payment.Status))
                payment.CompletedAt = Now;
            Journal(InitiatorType.Officer, "bank", "payment.dispatched", payment.Id, $"bank status {sent.Status}" + (sent.Message is null ? "" : $": {sent.Message}"));
        }
        catch (BankRefusedException e)
        {
            payment.Status = PaymentStatus.Failed;
            payment.FailureReason = e.Message;
            payment.CompletedAt = Now;
            payment.NextDispatchAt = null;
            Journal(InitiatorType.Officer, "bank", "payment.refused", payment.Id, e.Message);
        }
        catch (BankUnavailableException e)
        {
            // Exponential backoff from 10 seconds, at most 10 minutes between tries.
            var wait = TimeSpan.FromSeconds(Math.Min(600, 10 * Math.Pow(2, Math.Min(payment.DispatchAttempts - 1, 6))));
            payment.NextDispatchAt = Now + wait;
            payment.FailureReason = e.Message;
            Journal(InitiatorType.Officer, "bank", "payment.dispatch_retry", payment.Id, $"attempt {payment.DispatchAttempts}; next in {wait.TotalSeconds:0}s");
        }
        payment.Version++;
        await db.SaveChangesAsync(ct);
    }

    async Task<TreasuryPayment?> ExistingAsync(string requestKey, CancellationToken ct) =>
        await db.TreasuryPayments.AsNoTracking().FirstOrDefaultAsync(p => p.RequestKey == requestKey, ct);

    /// <summary>A repeated request: the same officer and terms get the first payment back; anything else is refused.</summary>
    async Task<PaymentDto> ReplayAsync(TreasuryPayment existing, Officer officer, PaymentCreateRequest? r, CancellationToken ct)
    {
        if (existing.InitiatedById != officer.Id
            || r is not null && (r.Amount != existing.Amount || r.PayeeId != existing.PayeeId || r.AccountId != existing.AccountId || r.Rail != existing.Rail))
            throw PaymentsException.Conflict("idempotency_key_reused", "This Idempotency-Key was already used for a different payment.", existing.Id);
        return await PaymentAsync(existing.Id, ct);
    }

    /// <summary>Saves a new payment; false if another request with the same key won the race.</summary>
    async Task<bool> SaveNewAsync(TreasuryPayment payment, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException e) when (e.InnerException is SqlException { Number: 2601 or 2627 })
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }

    async Task<TreasuryPayment> PendingAsync(string id, CancellationToken ct)
    {
        var payment = await db.TreasuryPayments.FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw PaymentsException.NotFound("payment");
        if (payment.Status != PaymentStatus.PendingApproval)
            throw PaymentsException.Conflict("invalid_state", $"This payment is {WireEnum.Name(payment.Status)}, not awaiting approval.");
        if (payment.ApprovalExpiresAt <= Now)
            throw PaymentsException.Conflict("expired", "This payment's approval window has closed; ask for it again.");
        return payment;
    }

    async Task DecideAsync(TreasuryPayment payment, Officer officer, ApprovalDecision decision, string? note, CancellationToken ct)
    {
        if (await db.PaymentApprovals.AnyAsync(a => a.PaymentId == payment.Id && a.OfficerId == officer.Id, ct))
            throw PaymentsException.Conflict("already_decided", "You have already decided on this payment.");
        db.PaymentApprovals.Add(new PaymentApproval
        {
            PaymentId = payment.Id,
            OfficerId = officer.Id,
            Officer = officer.Name,
            Decision = decision,
            TermsDigest = Digest(payment),
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            At = Now
        });
    }

    /// <summary>Saves a decision; a concurrent decision on the same payment is refused rather than lost.</summary>
    async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw PaymentsException.Conflict("invalid_state", "This payment changed at the same moment; reload it and try again.");
        }
        catch (DbUpdateException e) when (e.InnerException is SqlException { Number: 2601 or 2627 })
        {
            throw PaymentsException.Conflict("already_decided", "You have already decided on this payment.");
        }
    }

    /// <summary>A fingerprint of everything that decides where money goes. Approvals name it.</summary>
    public static string Digest(TreasuryPayment p) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|',
        p.Id, WireEnum.Name(p.Rail), p.AccountId, p.PayeeId, p.Amount.ToString("0.00", CultureInfo.InvariantCulture), p.RecipientCurrency ?? "", p.Purpose, p.Memo ?? ""))));

    static string Summary(TreasuryPayment p) =>
        $"{p.Amount.ToString("0.00", CultureInfo.InvariantCulture)} USD by {WireEnum.Name(p.Rail)}" + (p.RecipientCurrency is { } c ? $" (paid in {c})" : "");

    // ------------------------------------------------------------------ cards

    public async Task<IReadOnlyList<CardDto>> CardsAsync(CancellationToken ct) =>
        [.. (await db.CorporateCards.AsNoTracking().OrderByDescending(c => c.CreatedAt).ToListAsync(ct)).Select(ToDto)];

    public async Task<CardDto> IssueCardAsync(Officer officer, string? requestKey, CardCreateRequest r, CancellationToken ct)
    {
        var problems = new List<ApiErrorDetail>();
        if (string.IsNullOrWhiteSpace(r.Nickname) || r.Nickname.Trim().Length > 30 || r.Nickname.Any(c => c is < ' ' or > '~'))
            problems.Add(new("nickname", "invalid", "1 to 30 plain characters."));
        if (string.IsNullOrWhiteSpace(r.DebitAccountId))
            problems.Add(new("debit_account_id", "required", "Choose the account the card spends from."));
        CheckLimits(r.PerTransactionLimit, r.DailyLimit, r.MonthlyLimit, required: true, problems);
        CheckCategories(r.AllowedCategories, problems);
        if (problems.Count > 0)
            throw PaymentsException.Validation(problems);

        var issued = await Bank(() => bank.IssueCardAsync(new BankCardRequest(
            requestKey is { Length: > 0 and <= 50 } ? requestKey : Ids.New("card", clock), r.Nickname!.Trim(), r.DebitAccountId!, r.PerTransactionLimit!.Value,
            r.DailyLimit, r.MonthlyLimit, r.AllowedCategories ?? [], r.Purpose), ct));
        var card = await db.CorporateCards.FirstOrDefaultAsync(c => c.ProviderCardId == issued.Id, ct);
        if (card is not null)
            return ToDto(card);
        card = new CorporateCard
        {
            Id = Ids.New("card", clock),
            ProviderCardId = issued.Id,
            Nickname = issued.Nickname,
            Last4 = issued.Last4,
            Cardholder = issued.Cardholder,
            DebitAccountId = r.DebitAccountId!,
            PerTransactionLimit = r.PerTransactionLimit!.Value,
            DailyLimit = r.DailyLimit,
            MonthlyLimit = r.MonthlyLimit,
            AllowedCategories = string.Join(',', r.AllowedCategories ?? []),
            State = BankStatuses.ToCard(issued.Status),
            Purpose = r.Purpose,
            CreatedBy = officer.Name,
            CreatedAt = Now
        };
        db.CorporateCards.Add(card);
        Journal(InitiatorType.Officer, officer.Name, "card.issued", card.Id, $"{card.Nickname}; {card.PerTransactionLimit} USD per purchase");
        await db.SaveChangesAsync(ct);
        return ToDto(card);
    }

    public async Task<CardDto> UpdateCardAsync(Officer officer, string id, CardUpdateRequest r, CancellationToken ct)
    {
        var card = await LiveCardAsync(id, ct);
        var problems = new List<ApiErrorDetail>();
        if (r.Nickname is { } nickname && (nickname.Trim().Length is 0 or > 30 || nickname.Any(c => c is < ' ' or > '~')))
            problems.Add(new("nickname", "invalid", "1 to 30 plain characters."));
        var limitsChanged = r.PerTransactionLimit is not null || r.DailyLimit is not null || r.MonthlyLimit is not null;
        // Meow replaces all the limits together, so the unchanged ones are sent as they are.
        var perTransaction = r.PerTransactionLimit ?? card.PerTransactionLimit;
        var daily = r.DailyLimit is null ? card.DailyLimit : r.DailyLimit == 0 ? null : r.DailyLimit;
        var monthly = r.MonthlyLimit is null ? card.MonthlyLimit : r.MonthlyLimit == 0 ? null : r.MonthlyLimit;
        if (limitsChanged)
            CheckLimits(perTransaction, daily, monthly, required: true, problems);
        CheckCategories(r.AllowedCategories, problems);
        if (problems.Count > 0)
            throw PaymentsException.Validation(problems);

        var updated = await Bank(() => bank.UpdateCardAsync(card.ProviderCardId, new BankCardUpdate(
            r.Frozen, r.Nickname?.Trim(), limitsChanged ? perTransaction : null, daily, monthly, r.AllowedCategories), ct));
        card.Nickname = updated.Nickname;
        card.Last4 = updated.Last4 ?? card.Last4;
        card.State = BankStatuses.ToCard(updated.Status);
        if (limitsChanged)
            (card.PerTransactionLimit, card.DailyLimit, card.MonthlyLimit) = (perTransaction, daily, monthly);
        if (r.AllowedCategories is { } categories)
            card.AllowedCategories = string.Join(',', categories);
        card.Version++;
        Journal(InitiatorType.Officer, officer.Name, r.Frozen switch { true => "card.frozen", false => "card.unfrozen", null => "card.updated" }, card.Id, card.Nickname);
        await db.SaveChangesAsync(ct);
        return ToDto(card);
    }

    public async Task<CardDto> RevokeCardAsync(Officer officer, string id, CancellationToken ct)
    {
        var card = await LiveCardAsync(id, ct);
        await Bank(async () =>
        {
            await bank.RevokeCardAsync(card.ProviderCardId, ct);
            return true;
        });
        card.State = CardState.Closed;
        card.Version++;
        Journal(InitiatorType.Officer, officer.Name, "card.revoked", card.Id, card.Nickname);
        await db.SaveChangesAsync(ct);
        return ToDto(card);
    }

    /// <summary>
    /// The card's number, for the officer to use now. The bank releases it once per card; it passes
    /// through memory to the response and is never stored or logged (NFR-SEC-02).
    /// </summary>
    public async Task<CardNumberDto> RevealCardNumberAsync(Officer officer, string id, CancellationToken ct)
    {
        if (!Settings.Meow.AllowCardReveal)
            throw PaymentsException.Forbidden("card_reveal_disabled", "Showing card numbers is turned off. Use the card from the bank's dashboard.");
        var card = await LiveCardAsync(id, ct);
        if (card.NumberRevealed)
            throw PaymentsException.Conflict("already_revealed", "This card's number was already shown once. Revoke it and issue a new card if it was lost.");
        card.NumberRevealed = true;
        card.Version++;
        // Recorded before the bank call: the bank treats the first claim as final even if the response is lost.
        Journal(InitiatorType.Officer, officer.Name, "card.number_revealed", card.Id, card.Nickname);
        await db.SaveChangesAsync(ct);
        var number = await Bank(() => bank.RevealCardNumberAsync(card.ProviderCardId, ct));
        return new CardNumberDto(number.Number, number.Cvc, number.ExpMonth, number.ExpYear);
    }

    async Task<CorporateCard> LiveCardAsync(string id, CancellationToken ct)
    {
        var card = await db.CorporateCards.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw PaymentsException.NotFound("card");
        if (card.State is CardState.Closed or CardState.Failed)
            throw PaymentsException.Conflict("invalid_state", "This card is closed.");
        return card;
    }

    static void CheckLimits(int? perTransaction, int? daily, int? monthly, bool required, List<ApiErrorDetail> problems)
    {
        if (required && perTransaction is not (> 0 and <= 200_000))
            problems.Add(new("per_transaction_limit", "invalid", "Whole dollars, 1 to 200,000 (the bank may set a lower ceiling)."));
        if (daily is < 0 || monthly is < 0)
            problems.Add(new("daily_limit", "invalid", "Limits are whole dollars, 0 or more."));
        if (daily is > 0 && perTransaction > daily || monthly is > 0 && daily > monthly)
            problems.Add(new("daily_limit", "inconsistent", "Each limit must be at least the one for a shorter period."));
    }

    static void CheckCategories(IReadOnlyList<string>? categories, List<ApiErrorDetail> problems)
    {
        if (categories?.FirstOrDefault(c => !BankStatuses.CardCategories.Contains(c)) is { } unknown)
            problems.Add(new("allowed_categories", "invalid", $"\"{unknown}\" is not one of the bank's merchant categories."));
    }

    // ------------------------------------------------------------------ bank events and upkeep

    /// <summary>
    /// Applies one stored bank webhook: payment and card status, or a deposit that makes the cached
    /// balances stale. Events Ndeipi has no use for are kept and marked ignored.
    /// </summary>
    public async Task ApplyEventAsync(BankWebhookEvent e, CancellationToken ct)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(e.RawPayload)!;
        var data = root["data"];
        var resource = e.EventType.Split('.')[0];
        string? Field(string name) => data?[name] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        var handled = true;

        switch (resource)
        {
            case "ach_transfer" or "wire_transfer" or "book_transfer":
            case "crypto_transfer" when Field("direction") == "outbound":
                var paymentId = data?["metadata"]?["ndeipi_payment"]?.GetValue<string>();
                var transferId = Field("id");
                var payment = paymentId is not null
                    ? await db.TreasuryPayments.FirstOrDefaultAsync(p => p.Id == paymentId, ct)
                    : transferId is not null ? await db.TreasuryPayments.FirstOrDefaultAsync(p => p.ProviderTransferId == transferId, ct) : null;
                if (payment is null || Field("status") is not { } status)
                {
                    handled = false;
                    break;
                }
                payment.ProviderTransferId ??= transferId;
                payment.ProviderStatus = status;
                if (BankStatuses.ToPayment(status) is { } next && !BankStatuses.IsFinal(payment.Status) && next != payment.Status)
                {
                    payment.Status = next;
                    if (BankStatuses.IsFinal(next))
                        payment.CompletedAt = Now;
                    if (next is PaymentStatus.Failed or PaymentStatus.Returned && Field("error") is { } error)
                        payment.FailureReason = error;
                    payment.Version++;
                    Journal(InitiatorType.Officer, "bank", "payment." + WireEnum.Name(next), payment.Id, $"{e.EventType} {status}");
                }
                break;

            case "inbound_ach_transfer" or "inbound_wire_transfer" or "check_deposit" or "global_account_transfer" or "crypto_transfer" or "account":
                // Balances moved; the next read asks the bank.
                await db.TreasuryAccounts.ExecuteUpdateAsync(s => s.SetProperty(a => a.RefreshedAt, DateTimeOffset.MinValue), ct);
                Journal(InitiatorType.Officer, "bank", resource == "account" ? "account.updated" : "deposit.received", Field("account_id"),
                    $"{e.EventType} {Field("amount")} {Field("currency")} {Field("status") ?? Field("activity_type")}".Trim());
                break;

            case "card":
                var card = Field("id") is { } cardId ? await db.CorporateCards.FirstOrDefaultAsync(c => c.ProviderCardId == cardId, ct) : null;
                if (card is null || Field("status") is not { } cardStatus)
                {
                    handled = false;
                    break;
                }
                card.State = BankStatuses.ToCard(cardStatus);
                if (Field("last_four") is { Length: 4 } last4 && last4.All(char.IsAsciiDigit))
                    card.Last4 = last4;
                card.Version++;
                Journal(InitiatorType.Officer, "bank", "card." + WireEnum.Name(card.State), card.Id, e.EventType);
                break;

            default:
                handled = false;
                break;
        }

        e.Status = handled ? BankEventStatus.Processed : BankEventStatus.Ignored;
        e.ProcessedAt = Now;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Expires approvals past their window. Returns how many expired.</summary>
    public async Task<int> ExpireApprovalsAsync(CancellationToken ct)
    {
        var expired = await db.TreasuryPayments.Where(p => p.Status == PaymentStatus.PendingApproval && p.ApprovalExpiresAt <= Now).ToListAsync(ct);
        foreach (var p in expired)
        {
            p.Status = PaymentStatus.Expired;
            p.CompletedAt = Now;
            p.Version++;
            Journal(InitiatorType.Officer, "ndeipi", "payment.expired", p.Id, "Not approved in time.");
        }
        await db.SaveChangesAsync(ct);
        return expired.Count;
    }

    public async Task<IReadOnlyList<JournalEntryDto>> JournalAsync(string? subject, long? before, int limit, CancellationToken ct)
    {
        var query = db.BankingJournal.AsNoTracking();
        if (subject is not null)
            query = query.Where(j => j.Subject == subject);
        if (before is not null)
            query = query.Where(j => j.Id < before);
        return [.. (await query.OrderByDescending(j => j.Id).Take(Math.Clamp(limit, 1, 200)).ToListAsync(ct))
            .Select(j => new JournalEntryDto(j.Id, j.At, j.ActorType, j.Actor, j.Action, j.Subject, j.Detail, j.AgentSessionId, j.AgentModel))];
    }

    /// <summary>The latest journal entry: it moves whenever anything in the treasury changes, so clients poll it cheaply.</summary>
    public async Task<long> CursorAsync(CancellationToken ct) => await db.BankingJournal.MaxAsync(j => (long?)j.Id, ct) ?? 0;

    public void Journal(InitiatorType type, string actor, string action, string? subject, string? detail, string? sessionId = null, string? model = null) =>
        db.BankingJournal.Add(new BankingJournalEntry
        {
            At = Now,
            ActorType = type,
            Actor = actor,
            Action = action,
            Subject = subject,
            Detail = detail is { Length: > 1000 } ? detail[..1000] : detail,
            AgentSessionId = sessionId,
            AgentModel = model
        });

    async Task JournalAsync(InitiatorType type, string actor, string action, string? subject, string? detail, string? sessionId = null, string? model = null, CancellationToken ct = default)
    {
        Journal(type, actor, action, subject, detail, sessionId, model);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>A bank call from an officer's request: a refusal is the officer's to fix, an outage is a 503.</summary>
    async Task<T> Bank<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (BankRefusedException e)
        {
            throw PaymentsException.Unprocessable("bank_refused", e.Message);
        }
        catch (BankUnavailableException e)
        {
            log.LogWarning(e, "Bank unavailable");
            throw new PaymentsException(503, new ApiError("bank_unavailable", e.Message));
        }
    }

    // ------------------------------------------------------------------ validation and mapping

    static (PayeeKind Kind, BankPayeeRequest Request) ValidatePayee(PayeeCreateRequest r)
    {
        var problems = new List<ApiErrorDetail>();
        if (r.Kind is null)
            problems.Add(new("kind", "required", "Choose ach, wire, international or crypto."));
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Trim().Length > 140)
            problems.Add(new("name", "required", "The payee's name as their bank knows it."));
        if (problems.Count > 0)
            throw PaymentsException.Validation(problems);
        var kind = r.Kind!.Value;
        var name = r.Name!.Trim();

        BankAddress? Address(AddressRequest? a, string field)
        {
            if (a is null)
            {
                problems.Add(new(field, "required", "Street, city, postal code and country."));
                return null;
            }
            if (string.IsNullOrWhiteSpace(a.Line1) || string.IsNullOrWhiteSpace(a.City) || string.IsNullOrWhiteSpace(a.PostalCode) || a.Country is not { Length: 2 })
            {
                problems.Add(new(field, "invalid", "Street, city, postal code and a two-letter country code."));
                return null;
            }
            if (a.Country.Equals("US", StringComparison.OrdinalIgnoreCase) && a.State is not { Length: 2 })
                problems.Add(new(field + ".state", "required", "US addresses need the two-letter state."));
            return new BankAddress(a.Line1.Trim(), a.Line2?.Trim(), a.City.Trim(), a.State?.Trim().ToUpperInvariant(), a.PostalCode.Trim(), a.Country.ToUpperInvariant());
        }

        BankPayeeRequest request;
        switch (kind)
        {
            case PayeeKind.Ach or PayeeKind.Wire:
                if (kind == PayeeKind.Ach ? r.RoutingNumber is not { Length: 9 } || !r.RoutingNumber.All(char.IsAsciiDigit) : r.RoutingNumber is not { Length: >= 8 and <= 11 })
                    problems.Add(new("routing_number", "invalid", kind == PayeeKind.Ach ? "A 9-digit ABA routing number." : "A 9-digit ABA routing number, or an 8 or 11 character SWIFT/BIC."));
                if (r.AccountNumber is not { Length: >= 1 } || r.AccountNumber.Length > (kind == PayeeKind.Ach ? 18 : 34))
                    problems.Add(new("account_number", "invalid", "The payee's account number."));
                if (kind == PayeeKind.Ach && r.AccountType is not ("checking" or "savings"))
                    problems.Add(new("account_type", "invalid", "checking or savings."));
                if (kind == PayeeKind.Wire && string.IsNullOrWhiteSpace(r.BankName))
                    problems.Add(new("bank_name", "required", "The receiving bank's name."));
                var address = Address(r.Address, "address");
                var bankAddress = kind == PayeeKind.Wire ? Address(r.BankAddress, "bank_address") : null;
                request = new BankPayeeRequest(kind, name, r.Email, problems.Count > 0 ? null :
                    new BankAccountDetails(r.RoutingNumber!.Trim(), r.AccountNumber!.Trim(), r.AccountType ?? "checking", address!, r.BankName?.Trim(), bankAddress), null, null);
                break;

            case PayeeKind.International:
                if (r.BeneficiaryType is not ("individual" or "company"))
                    problems.Add(new("beneficiary_type", "invalid", "individual or company."));
                if (r.Currency is not { Length: 3 })
                    problems.Add(new("currency", "invalid", "The ISO 4217 currency they are paid in, such as ZAR or ZMW."));
                if (r.BankCountry is not { Length: 2 })
                    problems.Add(new("bank_country", "invalid", "The two-letter country of their bank."));
                if (string.IsNullOrWhiteSpace(r.Iban) && string.IsNullOrWhiteSpace(r.AccountNumber))
                    problems.Add(new("account_number", "required", "Their IBAN or account number."));
                if (r.SwiftBic is { } swift && !System.Text.RegularExpressions.Regex.IsMatch(swift.Trim().ToUpperInvariant(), "^[A-Z]{6}[A-Z0-9]{2}([A-Z0-9]{3})?$"))
                    problems.Add(new("swift_bic", "invalid", "An 8 or 11 character SWIFT/BIC."));
                var beneficiaryAddress = r.Address is null ? null : Address(r.Address, "address");
                request = new BankPayeeRequest(kind, name, r.Email, null, new InternationalDetails(
                    r.BeneficiaryType ?? "", r.Currency?.ToUpperInvariant() ?? "", r.BankCountry?.ToUpperInvariant() ?? "", r.Iban?.Replace(" ", "").ToUpperInvariant(),
                    r.AccountNumber?.Trim(), r.SwiftBic?.Trim().ToUpperInvariant(), r.BankName?.Trim(), beneficiaryAddress), null);
                break;

            default:
                string[] networks = ["arbitrum", "avalanche_c_chain", "base", "ethereum", "optimism", "polygon", "solana", "stellar", "sui", "tron"];
                string[] coins = ["usdc", "usdt", "pyusd", "usdg"];
                if (r.WalletAddress is not { Length: >= 10 and <= 66 })
                    problems.Add(new("wallet_address", "invalid", "The wallet address, 10 to 66 characters."));
                if (!networks.Contains(r.Network))
                    problems.Add(new("network", "invalid", "One of " + string.Join(", ", networks) + "."));
                if (!coins.Contains(r.Coin))
                    problems.Add(new("coin", "invalid", "One of " + string.Join(", ", coins) + "."));
                request = new BankPayeeRequest(kind, name, r.Email, null, null, new CryptoDetails(r.WalletAddress?.Trim() ?? "", r.Network ?? "", r.Coin ?? ""));
                break;
        }
        if (problems.Count > 0)
            throw PaymentsException.Validation(problems);
        return (kind, request);
    }

    static string Display(PayeeKind kind, BankPayeeRequest r) => kind switch
    {
        PayeeKind.Ach => $"ACH {r.Bank!.AccountType} ••{Last4(r.Bank.AccountNumber)}",
        PayeeKind.Wire => $"{r.Bank!.BankName} ••{Last4(r.Bank.AccountNumber)}",
        PayeeKind.International => $"{r.International!.Currency} {r.International.BankName ?? r.International.BankCountry} ••{Last4(r.International.Iban ?? r.International.AccountNumber ?? "")}",
        _ => $"{r.Crypto!.Currency.ToUpperInvariant()} on {r.Crypto.Network} {r.Crypto.Address[..6]}…{r.Crypto.Address[^4..]}"
    };

    static string Last4(string number) => number.Length <= 4 ? number : number[^4..];

    static BankAccountDto ToDto(TreasuryAccount a, bool stale) =>
        new(a.Id, a.Name, a.Type, a.Last4, a.Currency, a.Status, a.Available, a.Current, a.Bank, a.RefreshedAt, stale);

    static BankTransactionDto ToDto(BankTransaction t) =>
        new(t.Id, t.At, t.PostedAt, t.Description, t.Counterparty, t.Amount, t.Currency, t.Credit ? "credit" : "debit", t.Status, t.Type);

    static PayeeDto ToDto(Payee p) => new(p.Id, p.Kind, p.Name, p.Display, p.Country, p.Currency, p.CreatedBy, p.CreatedAt);

    static CardDto ToDto(CorporateCard c) => new(
        c.Id, c.Nickname, c.Last4, c.Cardholder, c.DebitAccountId, c.PerTransactionLimit, c.DailyLimit, c.MonthlyLimit,
        c.AllowedCategories.Length == 0 ? [] : c.AllowedCategories.Split(','), c.State, c.Purpose, c.NumberRevealed, c.CreatedBy, c.CreatedAt);

    async Task<IReadOnlyList<PaymentDto>> ToDtosAsync(IReadOnlyList<TreasuryPayment> payments, CancellationToken ct)
    {
        var ids = payments.Select(p => p.Id).ToList();
        var payeeIds = payments.Select(p => p.PayeeId).Distinct().ToList();
        var payees = await db.Payees.AsNoTracking().Where(p => payeeIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var approvals = (await db.PaymentApprovals.AsNoTracking().Where(a => ids.Contains(a.PaymentId)).OrderBy(a => a.Id).ToListAsync(ct))
            .ToLookup(a => a.PaymentId);
        return [.. payments.Select(p => new PaymentDto(
            p.Id, p.Rail, p.AccountId, p.PayeeId, payees.GetValueOrDefault(p.PayeeId)?.Name ?? "", payees.GetValueOrDefault(p.PayeeId)?.Display ?? "",
            p.Amount, p.RecipientCurrency, p.Purpose, p.Memo, p.Status, p.InitiatorType, p.InitiatedBy, p.InitiatedById, p.AgentSessionId, p.AgentModel,
            p.RequiredApprovals, [.. approvals[p.Id].Select(a => new ApprovalDto(a.Officer, a.Decision, a.Note, a.At))], p.ApprovalExpiresAt,
            Digest(p), p.ProviderStatus, p.FailureReason, p.CreatedAt, p.DispatchedAt, p.CompletedAt))];
    }
}
