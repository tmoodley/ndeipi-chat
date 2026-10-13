using System.Net.Http.Headers;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Ndeipi.Payments.Api;
using Ndeipi.Payments.Banking.Assistant;
using Ndeipi.Payments.Data;
using Ndeipi.Payments.Ops;
using Polly;

namespace Ndeipi.Payments.Banking;

/// <summary>
/// Business banking (SRS-NDE-MEOW-001) on the operator API at <c>/ops/v1/banking</c>, for Ndeipi's
/// treasury officers, and Meow's webhooks at <c>/webhooks/meow</c>. Officers reach it through the
/// Ndeipi app's server with a gateway key, which names the officer on every request; clients never
/// call Meow (SRS §2.2).
/// </summary>
public static class BankingModule
{
    public static IServiceCollection AddBanking(this IServiceCollection services)
    {
        services.AddSingleton<SanctionsScreen>();
        services.AddScoped<BankingService>();
        services.AddScoped<BankingAssistant>();
        services.AddSingleton<IAssistantModel, ClaudeAssistantModel>();
        services.AddSingleton<MeowMcpTools>();
        services.AddSingleton<BankingWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<BankingWorker>());

        services.AddHttpClient<MeowBank>((sp, http) =>
            {
                var meow = sp.GetRequiredService<IOptions<PaymentsOptions>>().Value.Banking.Meow;
                http.BaseAddress = new Uri(meow.BaseUrl.TrimEnd('/') + "/");
                http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                // The resilience pipeline times each attempt; this bounds the whole call.
                http.Timeout = TimeSpan.FromSeconds(90);
            })
            .AddResilienceHandler("meow", (pipeline, context) =>
            {
                var log = context.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Ndeipi.Payments.Banking.MeowCircuit");
                // NFR-REL-02: exponential backoff with jitter from 200 ms, at most four retries, on
                // 408, 429 and 5xx (honouring Retry-After) and on connection failures. Only calls that
                // are safe to repeat are retried: reads, updates, and creates that carry an
                // Idempotency-Key. A card number claim is never retried: Meow allows one.
                pipeline.AddRetry(new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = 4,
                    Delay = TimeSpan.FromMilliseconds(200),
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    ShouldHandle = args => ValueTask.FromResult(
                        HttpClientResiliencePredicates.IsTransient(args.Outcome) && SafeToRepeat(args.Context.GetRequestMessage()))
                });
                // NFR-REL-03: five failures in a row open the circuit, isolating Meow calls for 30
                // seconds instead of tying up threads; DevOps is alerted through the critical log.
                pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
                {
                    FailureRatio = 1.0,
                    MinimumThroughput = 5,
                    SamplingDuration = TimeSpan.FromSeconds(60),
                    BreakDuration = TimeSpan.FromSeconds(30),
                    OnOpened = args =>
                    {
                        log.LogCritical("Meow circuit opened after repeated failures; bank calls are refused for {Seconds}s", args.BreakDuration.TotalSeconds);
                        return ValueTask.CompletedTask;
                    },
                    OnClosed = _ =>
                    {
                        log.LogWarning("Meow circuit closed; bank calls resumed");
                        return ValueTask.CompletedTask;
                    }
                });
                pipeline.AddTimeout(TimeSpan.FromSeconds(20));
            });
        services.AddSingleton<SimulatedBank>();
        services.AddSingleton<NoBank>();
        // Chosen when first needed, from the options, so test hosts and environments can set the provider.
        services.AddTransient<IBusinessBank>(sp => sp.GetRequiredService<IOptions<PaymentsOptions>>().Value.Banking.Provider switch
        {
            "meow" => sp.GetRequiredService<MeowBank>(),
            "simulated" => sp.GetRequiredService<SimulatedBank>(),
            _ => sp.GetRequiredService<NoBank>()
        });
        return services;
    }

    static bool SafeToRepeat(HttpRequestMessage? request) =>
        request is not null
        && !(request.RequestUri?.AbsolutePath.EndsWith("/pan", StringComparison.Ordinal) ?? false)
        && !(request.RequestUri?.AbsolutePath.EndsWith("/pan/reveal", StringComparison.Ordinal) ?? false)
        && (request.Method != HttpMethod.Post || request.Headers.Contains("Idempotency-Key"));

    /// <summary>Refuses to start where banking would run unsafely: production on the simulator, without the keys, or without the sanctions list.</summary>
    public static void ValidateBanking(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<PaymentsOptions>>().Value;
        var banking = options.Banking;
        if (banking.Provider is not ("none" or "simulated" or "meow"))
            throw new InvalidOperationException($"Payments:Banking:Provider is \"{banking.Provider}\"; use none, simulated or meow.");
        if (banking.Provider == "meow" && (banking.Meow.ApiKey.Length == 0 || banking.Meow.WebhookSecret.Length == 0))
            throw new InvalidOperationException("Banking on Meow needs Payments:Banking:Meow:ApiKey and Payments:Banking:Meow:WebhookSecret (user-secrets or environment).");
        if (options.Environment == PaymentsEnvironment.Production && banking.Provider == "meow" && !app.Services.GetRequiredService<SanctionsScreen>().IsConfigured)
            throw new InvalidOperationException("Production banking needs the OFAC SDN list (Payments:Banking:Sanctions:SdnCsvPath) to screen payees (NFR-CMP-01).");
    }

    public static void MapBanking(this IEndpointRouteBuilder app)
    {
        app.MapBankWebhooks();

        var api = app.MapGroup("/ops/v1/banking").RequireAuthorization(OperatorAuthenticationHandler.Policy)
            .AddEndpointFilter(async (context, next) =>
                context.HttpContext.RequestServices.GetRequiredService<IOptions<PaymentsOptions>>().Value.Banking.Enabled
                    ? await next(context)
                    : ApiErrors.Result(context.HttpContext, 404, new("banking_disabled", "Business banking is not set up on this server.")));

        api.MapGet("/settings", (BankingService banking, BankingAssistant assistant) =>
            Json(banking.SettingsDto(assistant.IsEnabled)));
        api.MapGet("/updates", async (BankingService banking, CancellationToken ct) =>
            Json(new { cursor = await banking.CursorAsync(ct) }));
        api.MapGet("/journal", async (string? subject, long? before, int? limit, BankingService banking, CancellationToken ct) =>
            Json(new { data = await banking.JournalAsync(subject, before, limit ?? 50, ct) }));

        // ---- Accounts (FR-2)
        api.MapGet("/accounts", async (bool? refresh, BankingService banking, CancellationToken ct) =>
            Json(new { data = await banking.AccountsAsync(refresh == true, ct) }));
        api.MapGet("/accounts/{account_id}/transactions", async (string account_id, DateTimeOffset? from, DateTimeOffset? to, string? cursor, int? limit,
            BankingService banking, CancellationToken ct) =>
            Json(await banking.TransactionsAsync(account_id, from, to, cursor, limit ?? 25, ct)));
        api.MapGet("/accounts/{account_id}/statement.csv", async (string account_id, DateTimeOffset? from, DateTimeOffset? to,
            BankingService banking, TimeProvider clock, CancellationToken ct) =>
        {
            var end = to ?? clock.GetUtcNow();
            var csv = await banking.StatementCsvAsync(account_id, from ?? end.AddDays(-30), end, ct);
            return Results.File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv", $"statement-{end:yyyy-MM-dd}.csv");
        });

        // ---- Payees (FR-1.3)
        api.MapGet("/payees", async (BankingService banking, CancellationToken ct) =>
            Json(new { data = await banking.PayeesAsync(ct) }));
        api.MapPost("/payees", async (HttpContext http, PayeeCreateRequest request, BankingService banking, CancellationToken ct) =>
            Json(await banking.CreatePayeeAsync(OfficerOrThrow(http), request, ct), 201));
        api.MapDelete("/payees/{payee_id}", async (HttpContext http, string payee_id, BankingService banking, CancellationToken ct) =>
        {
            await banking.ArchivePayeeAsync(OfficerOrThrow(http), payee_id, ct);
            return Results.NoContent();
        });

        // ---- Payments (FR-3)
        api.MapGet("/payments", async (string? status, int? limit, BankingService banking, CancellationToken ct) =>
            Json(new { data = await banking.PaymentsAsync(WireEnum.Parse<PaymentStatus>(status, "status"), limit ?? 50, ct) }));
        api.MapGet("/payments/{payment_id}", async (string payment_id, BankingService banking, CancellationToken ct) =>
            Json(await banking.PaymentAsync(payment_id, ct)));
        api.MapPost("/payments", async (HttpContext http, PaymentCreateRequest request, BankingService banking, CancellationToken ct) =>
            Json(await banking.CreatePaymentAsync(OfficerOrThrow(http), http.Request.Headers[IdempotencyMiddleware.Header].ToString(), request, ct), 201));
        api.MapPost("/payments/{payment_id}/approve", async (HttpContext http, string payment_id, ApproveRequest request, BankingService banking, CancellationToken ct) =>
            Json(await banking.ApproveAsync(OfficerOrThrow(http), payment_id, request, ct)));
        api.MapPost("/payments/{payment_id}/reject", async (HttpContext http, string payment_id, ReasonRequest request, BankingService banking, CancellationToken ct) =>
            Json(await banking.RejectAsync(OfficerOrThrow(http), payment_id, request.Reason, ct)));
        api.MapPost("/payments/{payment_id}/cancel", async (HttpContext http, string payment_id, BankingService banking, CancellationToken ct) =>
            Json(await banking.CancelAsync(OfficerOrThrow(http), payment_id, ct)));
        api.MapPost("/payments/{payment_id}/confirm", async (HttpContext http, string payment_id, BankingService banking, CancellationToken ct) =>
            Json(await banking.ConfirmDraftAsync(OfficerOrThrow(http), payment_id, ct)));

        // ---- Cards (FR-4)
        api.MapGet("/cards", async (BankingService banking, CancellationToken ct) =>
            Json(new { data = await banking.CardsAsync(ct) }));
        api.MapPost("/cards", async (HttpContext http, CardCreateRequest request, BankingService banking, CancellationToken ct) =>
            Json(await banking.IssueCardAsync(OfficerOrThrow(http), http.Request.Headers[IdempotencyMiddleware.Header].ToString(), request, ct), 201));
        api.MapPatch("/cards/{card_id}", async (HttpContext http, string card_id, CardUpdateRequest request, BankingService banking, CancellationToken ct) =>
            Json(await banking.UpdateCardAsync(OfficerOrThrow(http), card_id, request, ct)));
        api.MapPost("/cards/{card_id}/revoke", async (HttpContext http, string card_id, BankingService banking, CancellationToken ct) =>
            Json(await banking.RevokeCardAsync(OfficerOrThrow(http), card_id, ct)));
        api.MapPost("/cards/{card_id}/number", async (HttpContext http, string card_id, BankingService banking, CancellationToken ct) =>
        {
            var number = await banking.RevealCardNumberAsync(OfficerOrThrow(http), card_id, ct);
            // Never cached by a browser or proxy on its way to the officer.
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers.Pragma = "no-cache";
            return Json(number);
        });

        // ---- Assistant (FR-5)
        api.MapPost("/assistant/sessions", async (HttpContext http, BankingAssistant assistant, CancellationToken ct) =>
            Json(await assistant.StartAsync(OfficerOrThrow(http), ct), 201));
        api.MapGet("/assistant/sessions/{session_id}", async (HttpContext http, string session_id, BankingAssistant assistant, CancellationToken ct) =>
            Json(await assistant.GetAsync(OfficerOrThrow(http), session_id, ct)));
        api.MapPost("/assistant/sessions/{session_id}/messages", async (HttpContext http, string session_id, AssistantMessageRequest request,
            BankingAssistant assistant, CancellationToken ct) =>
            Json(await assistant.SendAsync(OfficerOrThrow(http), session_id, request.Text, ct)));
    }

    static Officer OfficerOrThrow(HttpContext http) =>
        OperatorAuthenticationHandler.OfficerOf(http)
        ?? throw PaymentsException.Forbidden("officer_required", "Name the officer acting (Acting-Officer header); the gateway alone may only read.");

    static IResult Json<T>(T value, int status = 200) => Results.Json(value, PaymentsJson.Options, statusCode: status);

    /// <summary>Stands in when banking is off; the endpoints answer 404 before anything reaches it.</summary>
    sealed class NoBank : IBusinessBank
    {
        static BankUnavailableException Off => new("Business banking is not set up on this server.");
        public Task<IReadOnlyList<BankAccountInfo>> ListAccountsAsync(CancellationToken ct) => throw Off;
        public Task<BankTransactionPage> ListTransactionsAsync(string a, DateTimeOffset? f, DateTimeOffset? t, string? c, int l, CancellationToken ct) => throw Off;
        public Task<BankPayeeRef> CreatePayeeAsync(BankPayeeRequest r, CancellationToken ct) => throw Off;
        public Task<BankPaymentResult> SendAsync(BankPaymentRequest r, CancellationToken ct) => throw Off;
        public Task<BankCard> IssueCardAsync(BankCardRequest r, CancellationToken ct) => throw Off;
        public Task<BankCard> UpdateCardAsync(string id, BankCardUpdate u, CancellationToken ct) => throw Off;
        public Task RevokeCardAsync(string id, CancellationToken ct) => throw Off;
        public Task<IReadOnlyList<BankCard>> ListCardsAsync(CancellationToken ct) => throw Off;
        public Task<BankCardNumber> RevealCardNumberAsync(string id, CancellationToken ct) => throw Off;
    }
}
