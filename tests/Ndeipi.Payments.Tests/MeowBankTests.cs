using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic.Models.Beta.Messages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ndeipi.Payments.Api;
using Ndeipi.Payments.Banking;
using Ndeipi.Payments.Data;

namespace Ndeipi.Payments.Tests;

/// <summary>
/// The Meow adapter against a recorded-shape stub: the requests it sends (paths, headers, bodies as
/// developer.meow.com documents them), how it reads Meow's answers, and the resilience pipeline
/// around it (NFR-REL-02, NFR-REL-03).
/// </summary>
public sealed class MeowBankTests
{
    sealed class Stub : HttpMessageHandler
    {
        readonly ConcurrentQueue<Func<HttpRequestMessage, HttpResponseMessage>> _answers = new();
        public Func<HttpRequestMessage, HttpResponseMessage>? Always;
        public List<(HttpRequestMessage Request, string? Body)> Seen { get; } = [];

        public void Then(HttpStatusCode status, string json = "{}") =>
            _answers.Enqueue(_ => new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            lock (Seen)
                Seen.Add((request, body));
            if (_answers.TryDequeue(out var answer))
                return answer(request);
            return Always?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    static (MeowBank Bank, Stub Stub) Create()
    {
        var stub = new Stub();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Payments:Banking:Provider"] = "meow",
            ["Payments:Banking:Meow:BaseUrl"] = "https://api.sandbox.meow.com/v1",
            ["Payments:Banking:Meow:ApiKey"] = "test-key",
            ["Payments:Banking:Meow:EntityId"] = "6f3b1d7c-aa90-4f0c-a5d2-9c8b1d3e2c10"
        }).Build();
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(TimeProvider.System)
            .Configure<PaymentsOptions>(configuration.GetSection(PaymentsOptions.Section));
        services.AddBanking();
        services.AddHttpClient<MeowBank>().ConfigurePrimaryHttpMessageHandler(() => stub);
        return (services.BuildServiceProvider().GetRequiredService<MeowBank>(), stub);
    }

    static BankPaymentRequest Ach(string key = "tp_01JA8Z2F5G6H7J8K9M0N1P2Q3R") =>
        new(key, PaymentRail.Ach, "cash_account_12345678", "5dada3c2-7f3a-4f2e-8a91-3d6f1c9b9e22", 1250m, null, "Feed for the Chirundu herd", "INV-2041-MARCH");

    [Fact]
    public async Task An_ach_payment_is_posted_to_the_account_with_its_idempotency_key_and_metadata()
    {
        var (bank, stub) = Create();
        stub.Then(HttpStatusCode.OK, """{"id":null,"status":"processing","amount":"1250.00","approval_id":"b2d4e6f8-1a3c-4e5a-8b7c-9d0e1f2a3b4c","message":null}""");

        var result = await bank.SendAsync(Ach(), default);

        var (request, body) = Assert.Single(stub.Seen);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.sandbox.meow.com/v1/accounts/cash_account_12345678/ach", request.RequestUri!.ToString());
        Assert.Equal("test-key", request.Headers.GetValues("x-api-key").Single());
        Assert.Equal("6f3b1d7c-aa90-4f0c-a5d2-9c8b1d3e2c10", request.Headers.GetValues("x-entity-id").Single());
        Assert.Equal("tp_01JA8Z2F5G6H7J8K9M0N1P2Q3R", request.Headers.GetValues("Idempotency-Key").Single());
        var json = JsonNode.Parse(body!)!;
        Assert.Equal("1250.00", json["amount"]!.GetValue<string>());
        Assert.Equal("5dada3c2-7f3a-4f2e-8a91-3d6f1c9b9e22", json["counterparty_id"]!.GetValue<string>());
        Assert.Equal("INV-2041-M", json["description"]!.GetValue<string>()); // Meow allows ten characters
        Assert.Equal("tp_01JA8Z2F5G6H7J8K9M0N1P2Q3R", json["metadata"]!["ndeipi_payment"]!.GetValue<string>());
        Assert.Equal("processing", result.Status);
        Assert.Equal("b2d4e6f8-1a3c-4e5a-8b7c-9d0e1f2a3b4c", result.ApprovalId);
    }

    [Theory]
    [InlineData(PaymentRail.Wire, "accounts/cash_account_12345678/wire", "counterparty_id")]
    [InlineData(PaymentRail.International, "accounts/cash_account_12345678/international", "contact_id")]
    [InlineData(PaymentRail.Crypto, "accounts/cash_account_12345678/crypto", "crypto_id")]
    public async Task Each_rail_goes_to_its_own_endpoint_naming_the_payee_as_meow_expects(PaymentRail rail, string path, string payeeField)
    {
        var (bank, stub) = Create();
        stub.Then(HttpStatusCode.OK, """{"status":"pending_approval","approval_id":"7b1e4d2a-5c6f-4a8b-9d3e-1f2a3b4c5d6e","message":"Approve in the dashboard."}""");

        var result = await bank.SendAsync(Ach() with { Rail = rail, RecipientCurrency = rail == PaymentRail.International ? "ZAR" : null }, default);

        var (request, body) = Assert.Single(stub.Seen);
        Assert.EndsWith(path, request.RequestUri!.AbsolutePath);
        Assert.NotNull(JsonNode.Parse(body!)![payeeField]);
        if (rail == PaymentRail.International)
        {
            Assert.Equal("ZAR", JsonNode.Parse(body!)!["recipient_currency"]!.GetValue<string>());
            Assert.Equal("1250.00", JsonNode.Parse(body!)!["source_amount"]!.GetValue<string>());
        }
        Assert.Equal(PaymentStatus.BankApproval, BankStatuses.ToPayment(result.Status));
    }

    [Fact]
    public async Task A_payment_is_retried_through_outages_with_the_same_idempotency_key()
    {
        var (bank, stub) = Create();
        stub.Then(HttpStatusCode.ServiceUnavailable);
        stub.Then(HttpStatusCode.BadGateway);
        stub.Then(HttpStatusCode.OK, """{"status":"processing","approval_id":"a"}""");

        await bank.SendAsync(Ach(), default);

        Assert.Equal(3, stub.Seen.Count);
        Assert.All(stub.Seen, s => Assert.Equal("tp_01JA8Z2F5G6H7J8K9M0N1P2Q3R", s.Request.Headers.GetValues("Idempotency-Key").Single()));
    }

    [Fact]
    public async Task Creates_without_an_idempotency_key_and_card_number_claims_are_never_retried()
    {
        var (bank, stub) = Create();
        stub.Always = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

        await Assert.ThrowsAsync<BankUnavailableException>(() => bank.CreatePayeeAsync(new BankPayeeRequest(
            PayeeKind.Crypto, "Lusaka Grain Co", null, null, null, new CryptoDetails("0x71C7656EC7ab88b098defB751B7401B5f6d8976F", "base", "usdc")), default));
        await Assert.ThrowsAsync<BankUnavailableException>(() => bank.RevealCardNumberAsync("7c9e6679-7425-40de-944b-e07fc1f90ae7", default));

        Assert.Equal(2, stub.Seen.Count);
    }

    [Fact]
    public async Task Five_failures_open_the_circuit_and_further_calls_fail_fast_without_reaching_meow()
    {
        var (bank, stub) = Create();
        stub.Always = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        await Assert.ThrowsAsync<BankUnavailableException>(() => bank.ListAccountsAsync(default));
        var afterFirst = stub.Seen.Count;
        await Assert.ThrowsAsync<BankUnavailableException>(() => bank.ListAccountsAsync(default));

        Assert.Equal(5, afterFirst); // one try and four retries
        Assert.Equal(afterFirst, stub.Seen.Count);
    }

    [Fact]
    public async Task Meows_refusals_come_back_as_their_message_and_key_problems_as_a_scopes_hint()
    {
        var (bank, stub) = Create();
        stub.Then(HttpStatusCode.BadRequest, """{"code":703,"message":"Invalid input: routing number","debug_message":"x"}""");
        stub.Then(HttpStatusCode.Forbidden, """{"error_code":"forbidden","message":"missing scope transfers:wire:write"}""");

        var invalid = await Assert.ThrowsAsync<BankRefusedException>(() => bank.SendAsync(Ach(), default));
        var forbidden = await Assert.ThrowsAsync<BankRefusedException>(() => bank.SendAsync(Ach() with { Rail = PaymentRail.Wire }, default));

        Assert.Equal("Invalid input: routing number", invalid.Message);
        Assert.Contains("missing scope transfers:wire:write", forbidden.Message);
        Assert.Contains("scopes", forbidden.Message);
    }

    [Fact]
    public async Task Accounts_are_read_with_their_balances_from_each_accounts_details()
    {
        var (bank, stub) = Create();
        stub.Then(HttpStatusCode.OK, """
            {"page":{"nextOffset":null},"accounts":[
              {"depositAccount":{"accountId":"cash_account_1","accountNumberDisplay":"5820","accountType":"CHECKING","bankProductType":"Grasshopper Bank N.A.; Member FDIC","currency":{"currencyCode":"USD"},"nickname":"Main Checking","productName":"Business Checking","status":"OPEN"}}]}
            """);
        stub.Then(HttpStatusCode.OK, """
            {"depositAccount":{"accountId":"cash_account_1","accountType":"CHECKING","accountNumberDisplay":"5820","productName":"Business Checking","nickname":"Main Checking","status":"OPEN","currency":{"currencyCode":"USD"},"currentBalance":1500.00,"availableBalance":1250.50}}
            """);

        var account = Assert.Single(await bank.ListAccountsAsync(default));

        Assert.Equal(("cash_account_1", "Main Checking", "5820", 1250.50m, 1500.00m), (account.Id, account.Name, account.Last4, account.Available, account.Current));
        Assert.EndsWith("/accounts/cash_account_1", stub.Seen[1].Request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task A_card_number_is_read_only_from_meows_vault()
    {
        var (bank, stub) = Create();
        stub.Then(HttpStatusCode.OK, """{"reveal_url":"https://attacker.example/v1/cards/c/pan/reveal","reveal_token":"t","token_expires_in_seconds":300,"exp_month":12,"exp_year":2028}""");

        await Assert.ThrowsAsync<BankRefusedException>(() => bank.RevealCardNumberAsync("c", default));

        Assert.Single(stub.Seen); // the API key never went to the other host
    }

    [Fact]
    public void Standard_webhooks_signatures_verify_against_the_specifications_test_vector()
    {
        const string secret = "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw";
        const string id = "msg_p5jXN8AQM9LWM0D4loKWxJek";
        const string timestamp = "1614265330";
        const string body = """{"test": 2432232314}""";
        var at = DateTimeOffset.FromUnixTimeSeconds(1614265330);

        Assert.Equal("v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=", BankWebhooks.Sign(secret, id, timestamp, body));
        Assert.True(BankWebhooks.Verify(secret, id, timestamp, "v1,bm9wZQ== v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=", body, at));
        Assert.False(BankWebhooks.Verify(secret, id, timestamp, "v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=", body + " ", at));
        Assert.False(BankWebhooks.Verify(secret, id, timestamp, "v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=", body, at.AddMinutes(6)));
        Assert.False(BankWebhooks.Verify("", id, timestamp, "v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=", body, at));
    }

    [Fact]
    public void Claudes_content_blocks_round_trip_through_storage_unchanged()
    {
        var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""
            {"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5-5","stop_reason":"tool_use","stop_sequence":null,
             "usage":{"input_tokens":10,"output_tokens":5},
             "content":[
               {"type":"thinking","thinking":"","signature":"EqQBCkgIBxABGAIiQL"},
               {"type":"text","text":"Checking the directory."},
               {"type":"tool_use","id":"toolu_01","name":"list_payees","input":{"a":1}}]}
            """)!;
        var message = BetaMessage.FromRawUnchecked(raw);

        var stored = JsonSerializer.SerializeToNode(message.Content)!.AsArray();
        var param = BetaMessageParam.FromRawUnchecked(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            new JsonObject { ["role"] = "assistant", ["content"] = stored.DeepClone() }.ToJsonString())!);
        var sent = JsonNode.Parse(JsonSerializer.Serialize(param))!;

        Assert.Equal(["thinking", "text", "tool_use"], stored.Select(b => b!["type"]!.GetValue<string>()));
        Assert.Equal("EqQBCkgIBxABGAIiQL", stored[0]!["signature"]!.GetValue<string>());
        Assert.Equal(1, stored[2]!["input"]!["a"]!.GetValue<int>());
        Assert.True(JsonNode.DeepEquals(stored, sent["content"]));
        Assert.Equal("\"default\"", JsonSerializer.Serialize(new Default()));
    }
}
