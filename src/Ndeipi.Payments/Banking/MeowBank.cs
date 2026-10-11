using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Ndeipi.Payments.Api;
using Ndeipi.Payments.Data;
using Polly.CircuitBreaker;

namespace Ndeipi.Payments.Banking;

/// <summary>
/// Meow's REST API (developer.meow.com) behind <see cref="IBusinessBank"/>. The HttpClient it is
/// given already retries transient failures and trips a circuit breaker (<see cref="BankingModule"/>),
/// and every create carries an <c>Idempotency-Key</c>, so a retried POST never moves money twice.
///
/// Meow's field names, statuses and error bodies stay in this file.
/// </summary>
public sealed class MeowBank(HttpClient http, IOptions<PaymentsOptions> options, ILogger<MeowBank> log) : IBusinessBank
{
    public const string ClientName = "meow";

    BankingOptions.MeowOptions Meow => options.Value.Banking.Meow;

    // ------------------------------------------------------------------ accounts

    public async Task<IReadOnlyList<BankAccountInfo>> ListAccountsAsync(CancellationToken ct)
    {
        var list = await GetAsync("accounts", ct);
        var accounts = new List<BankAccountInfo>();
        foreach (var wrapper in list["accounts"]!.AsArray())
        {
            var (kind, summary) = Unwrap(wrapper!);
            var id = summary["accountId"]!.GetValue<string>();
            // Balances come only with the account's details.
            var detail = Unwrap(await GetAsync($"accounts/{Uri.EscapeDataString(id)}", ct)).Account;
            var (available, current) = kind == "investmentAccount"
                ? (Number(detail["availableCashBalance"]), Number(detail["currentValue"]))
                : (Number(detail["availableBalance"]), Number(detail["currentBalance"]));
            accounts.Add(new BankAccountInfo(
                id,
                Text(summary["nickname"]) ?? summary["productName"]!.GetValue<string>(),
                summary["accountType"]!.GetValue<string>(),
                Text(summary["accountNumberDisplay"]),
                summary["currency"]?["currencyCode"]?.GetValue<string>() ?? "USD",
                summary["status"]!.GetValue<string>(),
                available,
                current,
                Text(summary["bankProductType"])));
        }
        return accounts;

        static (string Kind, JsonObject Account) Unwrap(JsonNode wrapper)
        {
            var property = wrapper.AsObject().First(p => p.Value is JsonObject);
            return (property.Key, property.Value!.AsObject());
        }
    }

    public async Task<BankTransactionPage> ListTransactionsAsync(
        string accountId, DateTimeOffset? from, DateTimeOffset? to, string? cursor, int limit, CancellationToken ct)
    {
        var query = new List<string> { $"limit={Math.Clamp(limit, 1, 100)}" };
        if (from is { } f)
            query.Add("start_time=" + Uri.EscapeDataString(f.ToString("O", CultureInfo.InvariantCulture)));
        if (to is { } t)
            query.Add("end_time=" + Uri.EscapeDataString(t.ToString("O", CultureInfo.InvariantCulture)));
        if (cursor is not null)
            query.Add("offset=" + Uri.EscapeDataString(cursor));

        var page = await GetAsync($"accounts/{Uri.EscapeDataString(accountId)}/transactions?{string.Join('&', query)}", ct);
        var transactions = new List<BankTransaction>();
        foreach (var wrapper in page["transactions"]!.AsArray())
        {
            var tx = wrapper!.AsObject().First(p => p.Value is JsonObject).Value!.AsObject();
            transactions.Add(new BankTransaction(
                tx["transactionId"]!.GetValue<string>(),
                DateTimeOffset.Parse(tx["transactionTimestamp"]!.GetValue<string>(), CultureInfo.InvariantCulture),
                Text(tx["postedTimestamp"]) is { } posted ? DateTimeOffset.Parse(posted, CultureInfo.InvariantCulture) : null,
                Text(tx["description"]) ?? "",
                Text(tx["payee"]),
                Math.Abs(Number(tx["amount"])),
                tx["currency"]?["currencyCode"]?.GetValue<string>() ?? "USD",
                Text(tx["debitCreditMemo"]) == "CREDIT",
                Text(tx["status"]) ?? "POSTED",
                Text(tx["transactionType"])));
        }
        return new BankTransactionPage(transactions, Text(page["page"]?["nextOffset"]));
    }

    // ------------------------------------------------------------------ payees

    public async Task<BankPayeeRef> CreatePayeeAsync(BankPayeeRequest r, CancellationToken ct)
    {
        var body = new JsonObject { ["nickname"] = r.Name, ["email"] = r.Email };
        switch (r.Kind)
        {
            case PayeeKind.Ach or PayeeKind.Wire:
                var bank = r.Bank ?? throw new ArgumentException("Bank details are required.");
                var counterparty = new JsonObject { ["name_on_account"] = r.Name, ["nickname"] = r.Name };
                if (r.Kind == PayeeKind.Ach)
                    counterparty["ach"] = new JsonObject
                    {
                        ["account_number"] = bank.AccountNumber,
                        ["routing_number"] = bank.RoutingNumber,
                        ["account_type"] = bank.AccountType,
                        ["address_on_account"] = Address(bank.Address)
                    };
                else
                    counterparty["wire"] = new JsonObject
                    {
                        ["account_number"] = bank.AccountNumber,
                        ["routing_number"] = bank.RoutingNumber,
                        ["bank_name"] = bank.BankName,
                        ["address_on_account"] = Address(bank.Address),
                        ["bank_address"] = Address(bank.BankAddress ?? throw new ArgumentException("A wire payee needs the bank's address."))
                    };
                body["counterparty"] = counterparty;
                break;
            case PayeeKind.International:
                var i = r.International ?? throw new ArgumentException("International details are required.");
                var international = new JsonObject
                {
                    ["payment_method"] = i.SwiftBic is null ? "LOCAL" : "SWIFT",
                    ["beneficiary_type"] = i.BeneficiaryType,
                    ["currency"] = i.Currency,
                    ["bank_country"] = i.BankCountry,
                    ["bank_name"] = i.BankName,
                    ["iban"] = i.Iban,
                    ["account_number"] = i.AccountNumber,
                    ["bic_swift"] = i.SwiftBic,
                    ["address"] = i.Address is null ? null : Address(i.Address)
                };
                if (i.BeneficiaryType == "company")
                    international["company_name"] = r.Name;
                else
                {
                    var space = r.Name.LastIndexOf(' ');
                    international["first_name"] = space > 0 ? r.Name[..space] : r.Name;
                    international["last_name"] = space > 0 ? r.Name[(space + 1)..] : r.Name;
                }
                body["international"] = international;
                break;
            case PayeeKind.Crypto:
                var c = r.Crypto ?? throw new ArgumentException("Wallet details are required.");
                body["crypto_addresses"] = new JsonArray(new JsonObject
                {
                    ["address"] = c.Address,
                    ["payment_rail"] = c.Network,
                    ["currency"] = c.Currency
                });
                break;
        }

        var contact = await SendAsync(HttpMethod.Post, "contacts", body, idempotencyKey: null, ct);
        var contactId = contact["id"]!.GetValue<string>();
        var methods = contact["payment_methods"];
        var payeeId = r.Kind switch
        {
            PayeeKind.Ach => Text(methods?["ach"]?["counterparty_id"]),
            PayeeKind.Wire => Text(methods?["wire"]?["counterparty_id"]),
            // International payments name the contact itself.
            PayeeKind.International => contactId,
            _ => Text(methods?["crypto"]?.AsArray().FirstOrDefault()?["id"])
        };
        return new BankPayeeRef(contactId, payeeId ?? throw new BankRefusedException("Meow saved the contact without the payment details it needs."));

        static JsonObject Address(BankAddress a) => new()
        {
            ["street_line_1"] = a.Line1,
            ["street_line_2"] = a.Line2,
            ["city"] = a.City,
            ["state"] = a.State,
            ["postal_code"] = a.PostalCode,
            ["country"] = a.Country
        };
    }

    // ------------------------------------------------------------------ payments

    public async Task<BankPaymentResult> SendAsync(BankPaymentRequest r, CancellationToken ct)
    {
        var account = Uri.EscapeDataString(r.AccountId);
        var amount = r.Amount.ToString("0.00", CultureInfo.InvariantCulture);
        // Webhooks echo metadata, which is how a bank event finds its payment.
        var metadata = new JsonObject { ["ndeipi_payment"] = r.IdempotencyKey };
        var note = r.Purpose.Length >= 5 ? Truncate(r.Purpose, 255) : null;

        var (path, body) = r.Rail switch
        {
            PaymentRail.Ach => ($"accounts/{account}/ach", new JsonObject
            {
                ["amount"] = amount,
                ["counterparty_id"] = r.PayeeId,
                // Shown on the payee's statement; Meow allows ten characters.
                ["description"] = r.Memo is { Length: > 0 } memo ? Truncate(memo, 10) : null,
                ["internal_note"] = note,
                ["metadata"] = metadata
            }),
            PaymentRail.Wire => ($"accounts/{account}/wire", new JsonObject
            {
                ["amount"] = amount,
                ["counterparty_id"] = r.PayeeId,
                ["purpose"] = Truncate(r.Purpose, 16),
                ["instructions"] = r.Memo is { Length: > 0 } memo ? Truncate(memo, 140) : null,
                ["internal_note"] = note,
                ["metadata"] = metadata
            }),
            PaymentRail.International => ($"accounts/{account}/international", new JsonObject
            {
                ["contact_id"] = r.PayeeId,
                ["recipient_currency"] = r.RecipientCurrency,
                ["source_amount"] = amount,
                ["purpose"] = Truncate(r.Purpose, 50),
                ["reference"] = r.Memo is { Length: > 0 } memo ? Truncate(memo, 140) : null,
                ["metadata"] = metadata
            }),
            _ => ($"accounts/{account}/crypto", new JsonObject
            {
                ["amount"] = amount,
                ["crypto_id"] = r.PayeeId,
                ["internal_note"] = note,
                ["metadata"] = metadata
            })
        };

        var result = await SendAsync(HttpMethod.Post, path, body, r.IdempotencyKey, ct);
        return new BankPaymentResult(
            result["approval_id"]!.GetValue<string>(),
            Text(result["id"]),
            result["status"]!.GetValue<string>(),
            Text(result["message"]));
    }

    // ------------------------------------------------------------------ cards

    public async Task<BankCard> IssueCardAsync(BankCardRequest r, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["nickname"] = r.Nickname,
            ["spending_controls"] = Controls(r.PerTransactionLimit, r.DailyLimit, r.MonthlyLimit),
            ["allowed_categories"] = r.AllowedCategories.Count == 0 ? null : new JsonArray([.. r.AllowedCategories.Select(c => JsonValue.Create(c))]),
            ["debit_account"] = r.DebitAccountId,
            ["purpose"] = r.Purpose,
            ["single_use"] = false
        };
        var created = await SendAsync(HttpMethod.Post, "cards", body, r.IdempotencyKey, ct);
        var id = created["metadata"]!["card_id"]!.GetValue<string>();
        try
        {
            return Card(await GetAsync($"cards/{Uri.EscapeDataString(id)}", ct), r.DebitAccountId);
        }
        catch (BankRefusedException)
        {
            // Issued but not readable yet; the card webhook fills in the rest.
            return new BankCard(id, r.Nickname, null, "pending", null, r.DebitAccountId);
        }
    }

    public async Task<BankCard> UpdateCardAsync(string providerCardId, BankCardUpdate u, CancellationToken ct)
    {
        var body = new JsonObject();
        if (u.Frozen is { } frozen)
            body["status"] = frozen ? "inactive" : "active";
        if (u.Nickname is not null)
            body["nickname"] = u.Nickname;
        if (u.PerTransactionLimit is { } perTransaction)
            body["spending_controls"] = Controls(perTransaction, u.DailyLimit, u.MonthlyLimit);
        if (u.AllowedCategories is { } categories)
            // An explicit null clears the restriction; leaving the field out keeps it.
            body["allowed_categories"] = categories.Count == 0 ? null : new JsonArray([.. categories.Select(c => JsonValue.Create(c))]);
        return Card(await SendAsync(HttpMethod.Patch, $"cards/{Uri.EscapeDataString(providerCardId)}", body, idempotencyKey: null, ct), null);
    }

    public Task RevokeCardAsync(string providerCardId, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"cards/{Uri.EscapeDataString(providerCardId)}/revoke", null, idempotencyKey: null, ct);

    public async Task<IReadOnlyList<BankCard>> ListCardsAsync(CancellationToken ct)
    {
        var cards = new List<BankCard>();
        string? offset = null;
        do
        {
            var page = await GetAsync("cards?limit=100" + (offset is null ? "" : "&offset=" + Uri.EscapeDataString(offset)), ct);
            cards.AddRange(page["data"]!.AsArray().Select(c => Card(c!.AsObject(), null)));
            offset = Text(page["page"]?["nextOffset"]);
        }
        while (offset is not null);
        return cards;
    }

    public async Task<BankCardNumber> RevealCardNumberAsync(string providerCardId, CancellationToken ct)
    {
        // Two steps: claim a single-use grant, then read the number from Meow's card vault.
        var grant = await SendAsync(HttpMethod.Post, $"cards/{Uri.EscapeDataString(providerCardId)}/pan", null, idempotencyKey: null, ct);
        var revealUrl = new Uri(grant["reveal_url"]!.GetValue<string>());
        // The API key goes with the reveal call, so it must only ever go to Meow.
        if (revealUrl.Scheme != Uri.UriSchemeHttps || !revealUrl.Host.EndsWith(".meow.com", StringComparison.OrdinalIgnoreCase))
            throw new BankRefusedException("Meow returned a card vault address outside meow.com; the number was not requested.");

        using var request = new HttpRequestMessage(HttpMethod.Get, revealUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", grant["reveal_token"]!.GetValue<string>());
        var number = await ReadAsync(request, ct);
        return new BankCardNumber(
            number["card_number"]!.GetValue<string>(),
            number["cvc"]!.GetValue<string>(),
            number["exp_month"]!.GetValue<int>(),
            number["exp_year"]!.GetValue<int>());
    }

    /// <summary>
    /// Subscribes <paramref name="url"/> to every Meow event, delivered as full snapshots. Returns the
    /// subscription and its signing secret, which Meow shows only now.
    /// </summary>
    public async Task<(string Id, string Secret)> SubscribeAsync(string url, CancellationToken ct)
    {
        var created = await SendAsync(HttpMethod.Post, "webhooks/subscriptions", new JsonObject
        {
            ["name"] = "Ndeipi treasury",
            ["url"] = url,
            ["event_types"] = null,
            ["payload_mode"] = "snapshot"
        }, idempotencyKey: "ndeipi-webhooks-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..24], ct);
        return (created["id"]!.GetValue<string>(), created["secret"]!.GetValue<string>());
    }

    static JsonObject Controls(int perTransaction, int? daily, int? monthly) => new()
    {
        ["per_transaction_limit"] = perTransaction,
        ["daily_limit"] = daily,
        ["monthly_limit"] = monthly
    };

    static BankCard Card(JsonObject c, string? debitAccount) => new(
        c["id"]!.GetValue<string>(),
        c["display_name"]!.GetValue<string>(),
        Text(c["last_four"]) is { } last4 && last4.All(char.IsAsciiDigit) ? last4 : null,
        c["status"]!.GetValue<string>(),
        Text(c["cardholder"]?["name"]),
        debitAccount);

    // ------------------------------------------------------------------ HTTP

    Task<JsonObject> GetAsync(string path, CancellationToken ct) => ReadAsync(new HttpRequestMessage(HttpMethod.Get, path), ct);

    Task<JsonObject> SendAsync(HttpMethod method, string path, JsonObject? body, string? idempotencyKey, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        if (idempotencyKey is not null)
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        return ReadAsync(request, ct);
    }

    async Task<JsonObject> ReadAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using (request)
        {
            if (Meow.ApiKey.Length == 0)
                throw new BankUnavailableException("No Meow API key is configured (Payments:Banking:Meow:ApiKey).");
            request.Headers.Add("x-api-key", Meow.ApiKey);
            if (Meow.EntityId is { Length: > 0 } entity)
                request.Headers.Add("x-entity-id", entity);

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, ct);
            }
            catch (Exception e) when (e is HttpRequestException or BrokenCircuitException or Polly.Timeout.TimeoutRejectedException
                                      || e is TaskCanceledException && !ct.IsCancellationRequested)
            {
                log.LogWarning(e, "Meow unreachable for {Method} {Path}", request.Method, request.RequestUri?.AbsolutePath);
                throw new BankUnavailableException("The bank can't be reached right now. Nothing was sent; try again shortly.", e);
            }

            using (response)
            {
                var text = await response.Content.ReadAsStringAsync(ct);
                if (response.IsSuccessStatusCode)
                    return text.Length == 0 ? [] : JsonNode.Parse(text)!.AsObject();

                var message = ErrorMessage(text);
                log.LogWarning("Meow answered {Status} for {Method} {Path}: {Message}", (int)response.StatusCode, request.Method, request.RequestUri?.AbsolutePath, message);
                throw response.StatusCode switch
                {
                    HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError =>
                        new BankUnavailableException("The bank is busy or down right now. Nothing was sent; try again shortly."),
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                        new BankRefusedException($"Meow refused Ndeipi's API key for this ({message ?? "not permitted"}). Check the key's scopes and the account's feature access."),
                    _ => new BankRefusedException(message ?? "The bank refused the request.")
                };
            }
        }
    }

    /// <summary>Meow's two error shapes: <c>{code, message, debug_message}</c> and <c>{error_code, message}</c>.</summary>
    static string? ErrorMessage(string body)
    {
        try
        {
            return JsonNode.Parse(body)?["message"]?.GetValue<string>();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    static decimal Number(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<decimal>(out var d) => d,
        JsonValue v when v.TryGetValue<string>(out var s) && decimal.TryParse(s, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => 0m
    };

    static string Truncate(string value, int length) => value.Length > length ? value[..length] : value;
}
