using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ndeipi.Api.Auth;
using Ndeipi.Api.Chat;
using Ndeipi.Api.Data;
using NdeipiChat.Contracts;

namespace Ndeipi.Api.Banking;

public static class BankingModule
{
    public const string WebhookPath = "/webhooks/bridge";
    public const string SignatureHeader = "X-Webhook-Signature";

    public static IServiceCollection AddBridgeBanking(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<BridgeOptions>(config.GetSection(BridgeOptions.Section));
        services.AddHttpClient<BridgeClient>((sp, http) =>
        {
            var bridge = sp.GetRequiredService<IOptions<BridgeOptions>>().Value;
            http.BaseAddress = new Uri(bridge.BaseUrl.TrimEnd('/') + "/");
            http.Timeout = TimeSpan.FromSeconds(30);
            if (bridge.ApiKey.Length > 0)
                http.DefaultRequestHeaders.Add("Api-Key", bridge.ApiKey);
        });
        services.AddScoped<BankingService>();
        services.AddSingleton<BridgeWebhookVerifier>();
        services.AddMessageKind<BankTransferHandler>();
        services.AddHostedService<BridgeTransferPoller>();
        return services;
    }

    public static void MapBanking(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/banking").RequireAuthorization();

        api.MapGet("/status", async (HttpContext http, CurrentUserService users, BankingService banking) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return Results.Ok(await banking.GetStatusAsync(me.Id, http.RequestAborted));
        });

        api.MapPost("/kyc", async (HttpContext http, CurrentUserService users, BankingService banking) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return Results.Ok(await banking.StartKycAsync(me, http.RequestAborted));
        });

        api.MapPost("/refresh", async (HttpContext http, CurrentUserService users, BankingService banking) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return Results.Ok(await banking.RefreshAsync(me.Id, http.RequestAborted));
        });

        api.MapGet("/balances", async (HttpContext http, CurrentUserService users, BankingService banking) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return Results.Ok(await banking.GetBalancesAsync(me.Id, http.RequestAborted));
        });

        app.MapPost(WebhookPath, async (HttpRequest request, BridgeWebhookVerifier verifier, BankingService banking) =>
        {
            // Verify against the exact bytes Bridge signed, before any parsing.
            using var reader = new StreamReader(request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(request.HttpContext.RequestAborted);
            if (!verifier.IsValid(request.Headers[SignatureHeader].ToString(), body))
                return Results.Unauthorized();

            BridgeWebhookEvent? evt;
            try
            {
                evt = JsonSerializer.Deserialize<BridgeWebhookEvent>(body, BridgeClient.Json);
            }
            catch (JsonException)
            {
                return Results.BadRequest();
            }
            if (evt?.EventId is null)
                return Results.BadRequest();

            await banking.HandleWebhookAsync(evt, request.HttpContext.RequestAborted);
            return Results.Ok();
        }).AllowAnonymous();
    }
}

/// <summary>
/// Checks Bridge's webhook signature: header <c>t=&lt;timestamp&gt;,v0=&lt;base64 signature&gt;</c>,
/// an RSA-SHA256 signature over <c>"{t}.{raw body}"</c>, verified with the endpoint's public key.
/// Old timestamps are refused so a captured delivery can't be replayed later.
/// </summary>
public sealed class BridgeWebhookVerifier(IOptions<BridgeOptions> options, TimeProvider clock)
{
    public bool IsValid(string? header, string body)
    {
        var pem = options.Value.WebhookPublicKey?.Replace("\\n", "\n");
        if (string.IsNullOrWhiteSpace(pem) || string.IsNullOrEmpty(header))
            return false;

        string? timestamp = null, signature = null;
        foreach (var part in header.Split(','))
        {
            var pair = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2)
                continue;
            if (pair[0] == "t")
                timestamp = pair[1];
            else if (pair[0] == "v0")
                signature = pair[1];
        }
        if (!long.TryParse(timestamp, out var t) || signature is null)
            return false;

        // Bridge sends milliseconds; accept seconds too.
        var sentAt = t > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(t) : DateTimeOffset.FromUnixTimeSeconds(t);
        if ((clock.GetUtcNow() - sentAt).Duration() > options.Value.WebhookTolerance)
            return false;

        byte[] signatureBytes;
        try
        {
            signatureBytes = Convert.FromBase64String(signature);
        }
        catch (FormatException)
        {
            return false;
        }

        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        return rsa.VerifyData(Encoding.UTF8.GetBytes($"{timestamp}.{body}"), signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }
}

/// <summary>
/// "bank.transfer" messages: money between two verified users' Bridge wallets. The transfer is
/// recorded with the message, then submitted to Bridge once the message is out.
/// </summary>
public sealed class BankTransferHandler(ChatDbContext db, BankingService banking, IOptions<BridgeOptions> options, TimeProvider clock) : IMessageKindHandler
{
    public const int MaxDecimals = 2;

    public string Kind => MessageKinds.BankTransfer;

    public async Task<PreparedMessage> PrepareAsync(MessageContext context, JsonElement payload, CancellationToken ct)
    {
        var bridge = options.Value;
        if (!bridge.IsConfigured)
            throw new ChatRejectedException("Sending money isn't set up on this server.");

        var request = MessagePayload.Read<BankTransferPayload>(payload);
        var recipient = MessagePayload.Recipient(context, request.RecipientId);
        var currency = bridge.Currency.ToLowerInvariant();
        if (!string.Equals(request.Currency?.Trim(), currency, StringComparison.OrdinalIgnoreCase))
            throw new ChatRejectedException($"Money is sent in {currency.ToUpperInvariant()}.");
        if (!Amounts.TryParse(request.Amount, MaxDecimals, out var amount))
            throw new ChatRejectedException("Enter an amount greater than zero, to the cent.");
        if (amount > bridge.MaxTransferAmount)
            throw new ChatRejectedException($"The most you can send at once is {Amounts.Format(bridge.MaxTransferAmount)} {currency.ToUpperInvariant()}.");
        var memo = MessagePayload.Memo(request.Memo);

        var profiles = await db.BankingProfiles.AsNoTracking()
            .Where(p => p.UserId == context.Sender.Id || p.UserId == recipient.Id)
            .ToListAsync(ct);
        if (profiles.FirstOrDefault(p => p.UserId == context.Sender.Id) is not { CanTransfer: true })
            throw new ChatRejectedException("Verify your identity under Me > Wallet before sending money.");
        if (profiles.FirstOrDefault(p => p.UserId == recipient.Id) is not { CanTransfer: true })
            throw new ChatRejectedException($"{recipient.DisplayName} hasn't verified their account yet, so they can't receive money.");

        var now = clock.GetUtcNow();
        var transfer = new BankTransfer
        {
            Id = Guid.NewGuid(),
            MessageId = context.MessageId,
            ConversationId = context.Conversation.Id,
            SenderId = context.Sender.Id,
            RecipientId = recipient.Id,
            Amount = amount,
            Currency = currency,
            Memo = memo,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.BankTransfers.Add(transfer);

        return new PreparedMessage(
            ContractJson.ToElement(new BankTransferPayload(recipient.Id, Amounts.Format(amount), currency, memo)),
            new BankTransferState(transfer.Id, TransferStatuses.Pending, null, null));
    }

    public async Task AfterSendAsync(MessageContext context, ChatMessage message, CancellationToken ct)
    {
        var transferId = await db.BankTransfers.Where(t => t.MessageId == message.Id).Select(t => t.Id).FirstAsync(ct);
        await banking.SubmitTransferAsync(transferId, ct);
    }
}

/// <summary>
/// The safety net under webhooks: resubmits transfers whose submission never got an answer and
/// re-reads ones Bridge hasn't reported on.
/// </summary>
public sealed class BridgeTransferPoller(
    IServiceScopeFactory scopes,
    IOptions<BridgeOptions> options,
    TimeProvider clock,
    ILogger<BridgeTransferPoller> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var bridge = options.Value;
        if (!bridge.IsConfigured || bridge.PollInterval <= TimeSpan.Zero)
            return;

        using var timer = new PeriodicTimer(bridge.PollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await PollAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    log.LogError(ex, "Bridge transfer poll failed");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    async Task PollAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var banking = scope.ServiceProvider.GetRequiredService<BankingService>();

        var stale = clock.GetUtcNow() - TimeSpan.FromSeconds(30);
        var unsent = await db.BankTransfers
            .Where(t => t.BridgeTransferId == null && t.Status == TransferStatuses.Pending && t.CreatedAt < stale)
            .Select(t => t.Id).Take(20).ToListAsync(ct);
        foreach (var id in unsent)
            await banking.SubmitTransferAsync(id, ct);

        var inFlight = await db.BankTransfers
            .Where(t => t.BridgeTransferId != null && (t.Status == TransferStatuses.Pending || t.Status == TransferStatuses.Processing))
            .Select(t => t.Id).Take(50).ToListAsync(ct);
        foreach (var id in inFlight)
            await banking.SyncTransferAsync(id, ct);
    }
}
