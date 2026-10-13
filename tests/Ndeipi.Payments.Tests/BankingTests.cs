using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ndeipi.Payments.Banking;
using Ndeipi.Payments.Banking.Assistant;
using Ndeipi.Payments.Data;
using Ndeipi.Payments.Ops;
using Ndeipi.Payments.Tests.Infrastructure;

namespace Ndeipi.Payments.Tests;

/// <summary>
/// The payments server with business banking on the simulated bank, a $10,000 approval threshold,
/// a sanctions list of two entries, and a scripted assistant model. The worker is slowed to an hour
/// so tests run it themselves.
/// </summary>
public sealed class BankingApp : PaymentsApp
{
    public const string WebhookSecret = "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw";
    public const string SanctionedName = "Ivan Sanctioned Petrov";
    public const string SanctionedWallet = "0x1da5821544e25c636c1417ba96ade4cf6d2f9b5a";

    public ScriptedModel Model { get; } = new();

    protected override IDictionary<string, string?> Overrides => new Dictionary<string, string?>
    {
        ["Payments:Banking:Provider"] = "simulated",
        ["Payments:Banking:Meow:WebhookSecret"] = WebhookSecret,
        ["Payments:Banking:ApprovalThreshold"] = "10000",
        ["Payments:Banking:RequiredApprovals"] = "1",
        ["Payments:Banking:WorkerInterval"] = "01:00:00",
        ["Payments:Banking:Sanctions:Names:0"] = "PETROV, Ivan Sanctioned",
        ["Payments:Banking:Sanctions:Addresses:0"] = SanctionedWallet
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services => services.AddSingleton<IAssistantModel>(Model));
    }

    public SimulatedBank Bank => Services.GetRequiredService<SimulatedBank>();

    public Task RunWorkerAsync() => Services.GetRequiredService<BankingWorker>().RunOnceAsync(default);

    /// <summary>A client acting as one officer through the Ndeipi app's gateway key.</summary>
    public async Task<HttpClient> OfficerAsync(string id, string name)
    {
        var key = await AsHouseAsync(async sp => (await sp.GetRequiredService<OperatorKeyService>().IssueAsync("Ndeipi app", default, actsForOfficers: true)).Key);
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(OperatorAuthenticationHandler.Header, key);
        if (id.Length > 0)
            client.DefaultRequestHeaders.Add(OperatorAuthenticationHandler.ActingOfficerHeader, $"{id};{Uri.EscapeDataString(name)}");
        return client;
    }
}

/// <summary>The assistant's model, played from a script: each call takes the next step, which sees the conversation so far.</summary>
public sealed class ScriptedModel : IAssistantModel
{
    public const string Name = "claude-opus-5-5";
    readonly ConcurrentQueue<Func<IReadOnlyList<JsonObject>, JsonArray>> _steps = new();

    public bool IsConfigured => true;

    public List<AssistantCall> Calls { get; } = [];

    public void Then(Func<IReadOnlyList<JsonObject>, JsonArray> step) => _steps.Enqueue(step);

    public Task<AssistantResult> CreateAsync(AssistantCall call, CancellationToken ct)
    {
        lock (Calls)
            Calls.Add(call);
        var content = _steps.TryDequeue(out var step) ? step(call.Messages) : [new JsonObject { ["type"] = "text", ["text"] = "(no more script)" }];
        var stop = content.Any(b => b?["type"]?.GetValue<string>() == "tool_use") ? "tool_use" : "end_turn";
        return Task.FromResult(new AssistantResult(content, stop, Name));
    }

    public static JsonArray ToolUse(string id, string name, JsonObject input) =>
    [
        new JsonObject { ["type"] = "thinking", ["thinking"] = "", ["signature"] = "sig-" + id },
        new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = input }
    ];

    public static JsonArray Text(string text) => [new JsonObject { ["type"] = "text", ["text"] = text }];
}

/// <summary>
/// Ndeipi's treasury at its business bank (SRS-NDE-MEOW-001), through the operator API, against the
/// simulated bank: the SRS's acceptance tests TC-FR-3.2, TC-FR-3.3, TC-FR-5.2 and TC-NFR-SEC-01,
/// and the rest of what officers do.
/// </summary>
public sealed class BankingTests(BankingApp app) : IClassFixture<BankingApp>
{
    static async Task<JsonElement> Json(Task<HttpResponseMessage> request)
    {
        var response = await request;
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    static Task<HttpResponseMessage> Post(HttpClient client, string path, object? body = null, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body ?? new { }) };
        if (key is not null)
            request.Headers.Add("Idempotency-Key", key);
        return client.SendAsync(request);
    }

    async Task<string> AchPayeeAsync(HttpClient officer, string name = "Acme Feed Supplies")
    {
        var payee = await Json(Post(officer, "/ops/v1/banking/payees", new
        {
            kind = "ach",
            name,
            routing_number = "021000021",
            account_number = "000123456789",
            account_type = "checking",
            address = new { line1 = "1 Main St", city = "Austin", state = "TX", postal_code = "78701", country = "US" }
        }));
        return payee.GetProperty("id").GetString()!;
    }

    async Task<string> WirePayeeAsync(HttpClient officer) => (await Json(Post(officer, "/ops/v1/banking/payees", new
    {
        kind = "wire",
        name = "Harare Tractors Ltd",
        routing_number = "026009593",
        account_number = "4410012345",
        bank_name = "Bank of America",
        address = new { line1 = "4 Samora Machel Ave", city = "Harare", postal_code = "00263", country = "ZW" },
        bank_address = new { line1 = "222 Broadway", city = "New York", state = "NY", postal_code = "10038", country = "US" }
    }))).GetProperty("id").GetString()!;

    static object Payment(string rail, string payee, string amount, string purpose = "Feed for the Chirundu herd") => new
    {
        rail,
        account_id = SimulatedBank.CheckingId,
        payee_id = payee,
        amount,
        purpose,
        memo = "INV-2041"
    };

    [Fact]
    public async Task Accounts_show_balances_and_a_gateway_with_no_officer_may_read_but_not_act()
    {
        var gateway = await app.OfficerAsync("", "");
        var accounts = await Json(gateway.GetAsync("/ops/v1/banking/accounts?refresh=true"));
        var denied = await Post(gateway, "/ops/v1/banking/payees", new { kind = "ach", name = "X" });
        var integrator = await app.CreateIntegratorAsync();
        var outsider = await integrator.Client.GetAsync("/ops/v1/banking/accounts");

        var checking = accounts.GetProperty("data").EnumerateArray().Single(a => a.GetProperty("id").GetString() == SimulatedBank.CheckingId);
        Assert.Equal("5820", checking.GetProperty("last4").GetString());
        Assert.False(checking.GetProperty("stale").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("officer_required", await denied.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, outsider.StatusCode);
    }

    [Fact]
    public async Task TC_FR_3_2_the_same_payment_sent_concurrently_reaches_the_bank_once()
    {
        var thandi = await app.OfficerAsync("u_thandi", "Thandi Moyo");
        var payee = await AchPayeeAsync(thandi);
        var key = Guid.NewGuid().ToString();
        var before = app.Bank.SendCalls;

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Post(thandi, "/ops/v1/banking/payments", Payment("ach", payee, "1250.00"), key)));
        var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()));

        Assert.All(responses, r => Assert.True(r.IsSuccessStatusCode, r.StatusCode.ToString()));
        var ids = bodies.Select(b => JsonDocument.Parse(b).RootElement.GetProperty("id").GetString()).Distinct().ToList();
        var id = Assert.Single(ids)!;
        Assert.Equal(1, app.Bank.SendCalls - before);
        Assert.True(app.Bank.Sent.ContainsKey(id));
        var payment = await Json(thandi.GetAsync($"/ops/v1/banking/payments/{id}"));
        Assert.Equal("processing", payment.GetProperty("status").GetString());
        Assert.Equal("1250.00", payment.GetProperty("amount").GetString());

        // The same key for a different payment is refused rather than returning the first one.
        var reused = await Post(thandi, "/ops/v1/banking/payments", Payment("ach", payee, "999.00"), key);
        Assert.Equal(HttpStatusCode.Conflict, reused.StatusCode);
        Assert.Equal("idempotency_key_reused", await reused.ErrorCodeAsync());
        var missingKey = await Post(thandi, "/ops/v1/banking/payments", Payment("ach", payee, "10.00"));
        Assert.Equal(HttpStatusCode.BadRequest, missingKey.StatusCode);
    }

    [Fact]
    public async Task TC_FR_3_3_a_25000_wire_waits_for_a_second_officer_and_reaches_the_bank_only_after_their_approval()
    {
        var thandi = await app.OfficerAsync("u_thandi", "Thandi Moyo");
        var farai = await app.OfficerAsync("u_farai", "Farai Chikore");
        var payee = await WirePayeeAsync(thandi);
        var before = app.Bank.SendCalls;

        var held = await Json(Post(thandi, "/ops/v1/banking/payments", Payment("wire", payee, "25000.00", "Tractor deposit"), Guid.NewGuid().ToString()));
        var id = held.GetProperty("id").GetString()!;
        var digest = held.GetProperty("terms_digest").GetString()!;
        var self = await Post(thandi, $"/ops/v1/banking/payments/{id}/approve", new { terms_digest = digest });
        var wrongTerms = await Post(farai, $"/ops/v1/banking/payments/{id}/approve", new { terms_digest = new string('0', 64) });
        var sentBeforeApproval = app.Bank.SendCalls - before;
        var approved = await Json(Post(farai, $"/ops/v1/banking/payments/{id}/approve", new { terms_digest = digest, note = "Matches the quote." }));
        var again = await Post(farai, $"/ops/v1/banking/payments/{id}/approve", new { terms_digest = digest });

        Assert.Equal("pending_approval", held.GetProperty("status").GetString());
        Assert.Equal(1, held.GetProperty("required_approvals").GetInt32());
        Assert.Equal(HttpStatusCode.Forbidden, self.StatusCode);
        Assert.Equal("four_eyes", await self.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.Conflict, wrongTerms.StatusCode);
        Assert.Equal(0, sentBeforeApproval);
        Assert.Equal("processing", approved.GetProperty("status").GetString());
        Assert.Equal("Farai Chikore via Ndeipi app", approved.GetProperty("approvals")[0].GetProperty("officer").GetString());
        Assert.Equal(1, app.Bank.SendCalls - before);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        var journal = await Json(thandi.GetAsync($"/ops/v1/banking/journal?subject={id}"));
        var actions = journal.GetProperty("data").EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToList();
        Assert.Contains("payment.requested", actions);
        Assert.Contains("payment.approved", actions);
        Assert.Contains("payment.dispatched", actions);
    }

    [Fact]
    public async Task A_rejected_payment_is_never_sent_and_says_why()
    {
        var thandi = await app.OfficerAsync("u_thandi", "Thandi Moyo");
        var farai = await app.OfficerAsync("u_farai", "Farai Chikore");
        var payee = await WirePayeeAsync(thandi);
        var before = app.Bank.SendCalls;
        var id = (await Json(Post(thandi, "/ops/v1/banking/payments", Payment("wire", payee, "40000.00"), Guid.NewGuid().ToString()))).GetProperty("id").GetString();

        var noReason = await Post(farai, $"/ops/v1/banking/payments/{id}/reject", new { });
        var rejected = await Json(Post(farai, $"/ops/v1/banking/payments/{id}/reject", new { reason = "No signed quote on file." }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, noReason.StatusCode);
        Assert.Equal("rejected", rejected.GetProperty("status").GetString());
        Assert.Contains("No signed quote on file.", rejected.GetProperty("failure_reason").GetString());
        Assert.Equal(0, app.Bank.SendCalls - before);
    }

    [Fact]
    public async Task TC_NFR_SEC_01_a_webhook_with_a_bad_signature_or_an_old_timestamp_is_refused_and_not_kept()
    {
        var client = app.CreateClient();
        var body = """{"type":"ach_transfer.updated","timestamp":"2026-10-09T08:00:00Z","data":{"id":"withdrawal_x","status":"sent"}}""";
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var forged = await SendWebhookAsync(client, "msg_forged", now, body, "v1," + Convert.ToBase64String(new byte[32]));
        var old = (DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10)).ToUnixTimeSeconds().ToString();
        var replayed = await SendWebhookAsync(client, "msg_old", old, body, BankWebhooks.Sign(BankingApp.WebhookSecret, "msg_old", old, body));

        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, replayed.StatusCode);
        Assert.False(await app.DbAsync(db => db.BankWebhookEvents.AnyAsync(e => e.EventId == "msg_forged" || e.EventId == "msg_old")));
    }

    [Fact]
    public async Task A_signed_bank_event_moves_the_payment_on_once_however_often_it_is_delivered()
    {
        var thandi = await app.OfficerAsync("u_thandi", "Thandi Moyo");
        var payee = await AchPayeeAsync(thandi);
        var id = (await Json(Post(thandi, "/ops/v1/banking/payments", Payment("ach", payee, "300.00"), Guid.NewGuid().ToString()))).GetProperty("id").GetString()!;
        var eventId = "msg_" + Guid.NewGuid().ToString("N");
        var body = new JsonObject
        {
            ["type"] = "ach_transfer.updated",
            ["timestamp"] = "2026-10-09T08:00:00Z",
            ["data"] = new JsonObject
            {
                ["id"] = "withdrawal_txc_15wp3bd309xenf6p",
                ["object"] = "ach_transfer",
                ["status"] = "returned",
                ["amount"] = "300.00",
                ["error"] = "R03: No account / unable to locate account",
                ["metadata"] = new JsonObject { ["ndeipi_payment"] = id }
            },
            ["sequence"] = 3
        }.ToJsonString();
        var client = app.CreateClient();

        var first = await SendSignedAsync(client, eventId, body);
        var second = await SendSignedAsync(client, eventId, body);
        await app.RunWorkerAsync();
        var payment = await Json(thandi.GetAsync($"/ops/v1/banking/payments/{id}"));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(1, await app.DbAsync(db => db.BankWebhookEvents.CountAsync(e => e.EventId == eventId)));
        Assert.Equal("returned", payment.GetProperty("status").GetString());
        Assert.Equal("R03: No account / unable to locate account", payment.GetProperty("failure_reason").GetString());
        Assert.Equal("returned", payment.GetProperty("provider_status").GetString());
    }

    [Fact]
    public async Task While_the_bank_is_down_an_approved_payment_waits_and_is_sent_once_when_it_returns()
    {
        var thandi = await app.OfficerAsync("u_thandi", "Thandi Moyo");
        var payee = await AchPayeeAsync(thandi);
        app.Bank.FailNextSends = 1;
        var waiting = await Json(Post(thandi, "/ops/v1/banking/payments", Payment("ach", payee, "75.00"), Guid.NewGuid().ToString()));
        var id = waiting.GetProperty("id").GetString()!;

        // The retry is due later; bring it forward rather than wait.
        await app.DbAsync(db => db.TreasuryPayments.Where(p => p.Id == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.NextDispatchAt, DateTimeOffset.UtcNow.AddSeconds(-1))));
        await app.RunWorkerAsync();
        var sent = await Json(thandi.GetAsync($"/ops/v1/banking/payments/{id}"));

        Assert.Equal("approved", waiting.GetProperty("status").GetString());
        Assert.Equal("The simulated bank is down.", waiting.GetProperty("failure_reason").GetString());
        Assert.False(sent.TryGetProperty("failure_reason", out _));
        Assert.Equal("processing", sent.GetProperty("status").GetString());
        Assert.Single(app.Bank.Sent, s => s.Key == id);
    }

    [Fact]
    public async Task Sanctioned_names_and_wallets_are_refused_before_anyone_can_pay_them()
    {
        var thandi = await app.OfficerAsync("u_thandi", "Thandi Moyo");
        var person = await Post(thandi, "/ops/v1/banking/payees", new
        {
            kind = "ach",
            name = "Ivan  sanctioned PETROV",
            routing_number = "021000021",
            account_number = "1234",
            account_type = "checking",
            address = new { line1 = "1 Main St", city = "Austin", state = "TX", postal_code = "78701", country = "US" }
        });
        var wallet = await Post(thandi, "/ops/v1/banking/payees", new
        {
            kind = "crypto",
            name = "Some Exchange",
            wallet_address = BankingApp.SanctionedWallet.ToUpperInvariant().Replace("0X", "0x"),
            network = "ethereum",
            coin = "usdc"
        });
        var clean = await Post(thandi, "/ops/v1/banking/payees", new
        {
            kind = "crypto",
            name = "Lusaka Grain Co",
            wallet_address = "0x71C7656EC7ab88b098defB751B7401B5f6d8976F",
            network = "base",
            coin = "usdc"
        });

        Assert.Equal(HttpStatusCode.Forbidden, person.StatusCode);
        Assert.Equal("sanctions_match", await person.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.Forbidden, wallet.StatusCode);
        Assert.Equal(HttpStatusCode.Created, clean.StatusCode);
        Assert.Equal("USDC on base 0x71C7…976F", (await clean.JsonAsync()).GetProperty("display").GetString());
    }

    [Fact]
    public async Task TC_FR_5_2_the_assistant_drafts_a_payment_but_no_money_moves_until_the_officer_confirms_it()
    {
        var thandi = await app.OfficerAsync("u_thandi", "Thandi Moyo");
        var farai = await app.OfficerAsync("u_farai", "Farai Chikore");
        var payee = await AchPayeeAsync(thandi, "Mazvikadei Vets");
        var before = app.Bank.SendCalls;

        app.Model.Then(_ => ScriptedModel.ToolUse("toolu_1", "list_payees", []));
        app.Model.Then(messages =>
        {
            // The model sees the directory, finds the vets' id and drafts the payment.
            var directory = messages[^1]["content"]![0]!["content"]!.GetValue<string>();
            var id = JsonNode.Parse(directory)!.AsArray().Single(p => p!["name"]!.GetValue<string>() == "Mazvikadei Vets")!["id"]!.GetValue<string>();
            return ScriptedModel.ToolUse("toolu_2", "propose_payment", new JsonObject
            {
                ["payee_id"] = id,
                ["account_id"] = SimulatedBank.CheckingId,
                ["rail"] = "ach",
                ["amount"] = "500.00",
                ["purpose"] = "Vaccination visit"
            });
        });
        app.Model.Then(_ => ScriptedModel.Text("I've drafted a $500.00 ACH payment to Mazvikadei Vets. Confirm it in the app to send it."));

        var session = (await Json(Post(thandi, "/ops/v1/banking/assistant/sessions"))).GetProperty("id").GetString()!;
        var reply = await Json(Post(thandi, $"/ops/v1/banking/assistant/sessions/{session}/messages", new { text = "Send $500 to the vets for the vaccination visit" }));
        var draft = reply.GetProperty("drafts")[0];
        var draftId = draft.GetProperty("id").GetString()!;
        var sentAfterDraft = app.Bank.SendCalls - before;
        var notTheirs = await Post(farai, $"/ops/v1/banking/payments/{draftId}/confirm");
        var otherSession = await farai.GetAsync($"/ops/v1/banking/assistant/sessions/{session}");
        var confirmed = await Json(Post(thandi, $"/ops/v1/banking/payments/{draftId}/confirm"));

        Assert.Contains("drafted", reply.GetProperty("text").GetString());
        Assert.Equal("awaiting_confirmation", draft.GetProperty("status").GetString());
        Assert.Equal("agent", draft.GetProperty("initiator_type").GetString());
        Assert.Equal(session, draft.GetProperty("agent_session_id").GetString());
        Assert.Equal(ScriptedModel.Name, draft.GetProperty("agent_model").GetString());
        Assert.Equal(payee, draft.GetProperty("payee_id").GetString());
        Assert.Equal(0, sentAfterDraft);
        Assert.Equal(HttpStatusCode.Forbidden, notTheirs.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, otherSession.StatusCode);
        Assert.Equal("processing", confirmed.GetProperty("status").GetString());
        Assert.Equal(1, app.Bank.SendCalls - before);

        // FR-5.3: each tool call is journaled as the agent's, with the conversation and the model.
        var journal = await app.DbAsync(db => db.BankingJournal.Where(j => j.AgentSessionId == session && j.Action == "assistant.tool").ToListAsync());
        Assert.Equal(2, journal.Count);
        Assert.All(journal, j => Assert.Equal(InitiatorType.Agent, j.ActorType));
        Assert.All(journal, j => Assert.Equal(ScriptedModel.Name, j.AgentModel));
        Assert.All(journal, j => Assert.Equal("Thandi Moyo via Ndeipi app", j.Actor));

        // The model was offered no tool that moves money directly.
        var offered = app.Model.Calls[^1].Tools.Select(t => t.Name).ToList();
        Assert.DoesNotContain(offered, n => n.StartsWith("create_", StringComparison.Ordinal));
        // History is replayed as stored, thinking blocks included.
        var replayed = app.Model.Calls[^1].Messages;
        Assert.Equal("sig-toolu_1", replayed[1]["content"]![0]!["signature"]!.GetValue<string>());

        var transcript = await Json(thandi.GetAsync($"/ops/v1/banking/assistant/sessions/{session}"));
        Assert.Contains(transcript.GetProperty("messages").EnumerateArray(), m => m.GetProperty("drafted_payment_ids").EnumerateArray().Any(d => d.GetString() == draftId));
    }

    [Fact]
    public async Task Cards_are_issued_with_controls_frozen_and_revoked_and_their_number_is_not_shown_unless_enabled()
    {
        var thandi = await app.OfficerAsync("u_thandi", "Thandi Moyo");
        var badCategory = await Post(thandi, "/ops/v1/banking/cards", new
        {
            nickname = "Fuel card",
            debit_account_id = SimulatedBank.CheckingId,
            per_transaction_limit = 500,
            allowed_categories = new[] { "Fuel", "Diesel" }
        });
        var card = await Json(Post(thandi, "/ops/v1/banking/cards", new
        {
            nickname = "Fuel card",
            debit_account_id = SimulatedBank.CheckingId,
            per_transaction_limit = 500,
            daily_limit = 1000,
            monthly_limit = 8000,
            allowed_categories = new[] { "Fuel", "Vehicle Expenses" },
            purpose = "Delivery truck diesel"
        }));
        var id = card.GetProperty("id").GetString();
        var frozen = await Json(thandi.PatchAsJsonAsync($"/ops/v1/banking/cards/{id}", new { frozen = true }));
        var number = await Post(thandi, $"/ops/v1/banking/cards/{id}/number");
        var revoked = await Json(Post(thandi, $"/ops/v1/banking/cards/{id}/revoke"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, badCategory.StatusCode);
        Assert.Equal("active", card.GetProperty("state").GetString());
        Assert.Equal(4, card.GetProperty("last4").GetString()!.Length);
        Assert.Equal(["Fuel", "Vehicle Expenses"], card.GetProperty("allowed_categories").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal("frozen", frozen.GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, number.StatusCode);
        Assert.Equal("card_reveal_disabled", await number.ErrorCodeAsync());
        Assert.Equal("closed", revoked.GetProperty("state").GetString());
    }

    [Fact]
    public async Task The_journal_and_approvals_cannot_be_changed_even_directly_in_the_database()
    {
        var thandi = await app.OfficerAsync("u_thandi", "Thandi Moyo");
        await AchPayeeAsync(thandi);

        var update = await Assert.ThrowsAsync<SqlException>(() => app.DbAsync(db =>
            db.Database.ExecuteSqlRawAsync("UPDATE [payments].[BankingJournal] SET [Actor] = 'someone else'")));
        var delete = await Assert.ThrowsAsync<SqlException>(() => app.DbAsync(db =>
            db.Database.ExecuteSqlRawAsync("DELETE FROM [payments].[BankingJournal]")));

        Assert.Contains("write-once", update.Message);
        Assert.Contains("write-once", delete.Message);
    }

    [Fact]
    public async Task A_statement_downloads_as_csv_with_formulas_neutralised()
    {
        var thandi = await app.OfficerAsync("u_thandi", "Thandi Moyo");
        app.Bank.Deposit(SimulatedBank.CheckingId, 1500m, "=HYPERLINK(\"http://evil\")");

        var response = await thandi.GetAsync($"/ops/v1/banking/accounts/{SimulatedBank.CheckingId}/statement.csv");
        var csv = await response.Content.ReadAsStringAsync();

        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("date,posted,description", csv);
        Assert.Contains("\"'=HYPERLINK(\"\"http://evil\"\")\"", csv);
        Assert.Contains(",credit,1500.00,USD,", csv);
    }

    static Task<HttpResponseMessage> SendSignedAsync(HttpClient client, string id, string body)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        return SendWebhookAsync(client, id, timestamp, body, BankWebhooks.Sign(BankingApp.WebhookSecret, id, timestamp, body));
    }

    static Task<HttpResponseMessage> SendWebhookAsync(HttpClient client, string id, string timestamp, string body, string signature)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, BankWebhooks.Path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("webhook-id", id);
        request.Headers.Add("webhook-timestamp", timestamp);
        request.Headers.Add("webhook-signature", signature);
        return client.SendAsync(request);
    }
}
