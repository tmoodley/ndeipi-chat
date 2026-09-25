using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Data;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Assets;

public sealed class TokenOptions
{
    public const string Section = "Tokens";

    /// <summary>Chains transfers may use. Empty means any chain Ndeipi Enterprise Server accepts.</summary>
    public string[] Chains { get; set; } = [];

    /// <summary>Tokens offered in the app's picker. Any other token can still be sent by address.</summary>
    public List<KnownToken> Known { get; set; } = [];

    /// <summary>How often to look for status changes written by Ndeipi Enterprise Server.</summary>
    public TimeSpan QueuePollInterval { get; set; } = TimeSpan.FromSeconds(2);
}

public sealed class KnownToken
{
    public string Chain { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string Standard { get; set; } = TokenStandards.Erc20;
    public string? ContractAddress { get; set; }
    public int? Decimals { get; set; }

    public TokenRef ToTokenRef() => new(Chain.ToLowerInvariant(), Symbol, Standard.ToLowerInvariant(), ContractAddress, Decimals, null);
}

public static class TokenTransferModule
{
    public static IServiceCollection AddTokenTransfers(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<TokenOptions>(config.GetSection(TokenOptions.Section));
        services.AddMessageKind<AssetTransferHandler>();
        services.AddHostedService<TokenTransferQueueWatcher>();
        return services;
    }

    public static void MapTokenTransfers(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/tokens", (IOptions<TokenOptions> options) => options.Value.Known.Select(k => k.ToTokenRef()))
            .RequireAuthorization();
    }
}

/// <summary>
/// "asset.transfer" messages: validates the transfer and queues it for Ndeipi Enterprise Server in
/// the same transaction as the message, so a transfer can't exist without its message or the
/// message without its transfer.
/// </summary>
public sealed class AssetTransferHandler(ChatDbContext db, IOptions<TokenOptions> options, TimeProvider clock) : IMessageKindHandler
{
    /// <summary>The queue stores amounts as decimal(38,18).</summary>
    public const int MaxDecimals = 18;

    static readonly decimal MaxAmount = 100_000_000_000_000_000_000m; // 10^20: the whole digits decimal(38,18) holds

    public string Kind => MessageKinds.AssetTransfer;

    public async Task<PreparedMessage> PrepareAsync(MessageContext context, JsonElement payload, CancellationToken ct)
    {
        var request = MessagePayload.Read<AssetTransferPayload>(payload);
        var recipient = MessagePayload.Recipient(context, request.RecipientId);
        var token = NormaliseToken(request.Token ?? throw new ChatRejectedException("Choose a token to send."));
        var amount = ParseAmount(request.Amount, token);
        var memo = MessagePayload.Memo(request.Memo);

        var wallets = await db.UserWallets.AsNoTracking()
            .Where(w => w.Chain == token.Chain && (w.UserId == context.Sender.Id || w.UserId == recipient.Id))
            .ToListAsync(ct);

        var now = clock.GetUtcNow().UtcDateTime;
        var transfer = new TokenTransfer
        {
            Id = Guid.NewGuid(),
            Chain = token.Chain,
            TokenStandard = token.Standard,
            TokenSymbol = token.Symbol,
            ContractAddress = token.ContractAddress,
            TokenId = token.TokenId,
            Decimals = token.Decimals,
            Amount = amount,
            SenderUserId = context.Sender.Id,
            SenderClerkId = context.Sender.ClerkUserId,
            SenderWalletAddress = wallets.FirstOrDefault(w => w.UserId == context.Sender.Id)?.Address,
            RecipientUserId = recipient.Id,
            RecipientClerkId = recipient.ClerkUserId,
            RecipientWalletAddress = wallets.FirstOrDefault(w => w.UserId == recipient.Id)?.Address,
            ConversationId = context.Conversation.Id,
            MessageId = context.MessageId,
            Memo = memo,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.TokenTransfers.Add(transfer);

        var stored = new AssetTransferPayload(recipient.Id, token, Amounts.Format(amount), memo);
        return new PreparedMessage(ContractJson.ToElement(stored), new AssetTransferState(transfer.Id, TransferStatuses.Pending, null, null));
    }

    TokenRef NormaliseToken(TokenRef token)
    {
        var chain = token.Chain?.Trim().ToLowerInvariant() ?? "";
        if (chain.Length is 0 or > 50 || !chain.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            throw new ChatRejectedException("Choose which chain the token is on.");
        var chains = options.Value.Chains;
        if (chains.Length > 0 && !chains.Contains(chain, StringComparer.OrdinalIgnoreCase))
            throw new ChatRejectedException($"Transfers on '{chain}' aren't supported.");

        var standard = token.Standard?.Trim().ToLowerInvariant() ?? "";
        if (!TokenStandards.All.Contains(standard))
            throw new ChatRejectedException($"Unknown token standard '{token.Standard}'.");

        var contract = standard == TokenStandards.Native ? null : token.ContractAddress?.Trim();
        if (standard != TokenStandards.Native && (string.IsNullOrEmpty(contract) || contract.Length > 128))
            throw new ChatRejectedException("Enter the token's contract address.");

        // A token the server lists is described by the server, whatever the app sent -- so nobody
        // can relabel a worthless contract with a known symbol.
        var known = options.Value.Known.FirstOrDefault(k =>
            string.Equals(k.Chain, chain, StringComparison.OrdinalIgnoreCase)
            && string.Equals(k.ContractAddress ?? "", contract ?? "", StringComparison.OrdinalIgnoreCase)
            && (contract is not null || string.Equals(k.Standard, standard, StringComparison.OrdinalIgnoreCase)));

        var symbol = (known?.Symbol ?? token.Symbol)?.Trim() ?? "";
        if (symbol.Length is 0 or > 32)
            throw new ChatRejectedException("Enter the token's symbol.");

        var decimals = known?.Decimals ?? token.Decimals;
        string? tokenId = null;
        if (TokenStandards.IsNonFungible(standard))
        {
            tokenId = token.TokenId?.Trim();
            if (string.IsNullOrEmpty(tokenId) || tokenId.Length > 78 || !tokenId.All(char.IsAsciiDigit))
                throw new ChatRejectedException("Enter the id of the NFT to send.");
            decimals = null;
        }
        else if (!string.IsNullOrWhiteSpace(token.TokenId))
        {
            throw new ChatRejectedException("Only NFTs have a token id.");
        }

        if (decimals is < 0 or > 36)
            throw new ChatRejectedException("Token decimals must be between 0 and 36.");

        return new TokenRef(chain, symbol, standard, contract, decimals, tokenId);
    }

    static decimal ParseAmount(string? text, TokenRef token)
    {
        var maxDecimals = TokenStandards.IsNonFungible(token.Standard) ? 0 : Math.Min(token.Decimals ?? MaxDecimals, MaxDecimals);
        if (!Amounts.TryParse(text, maxDecimals, out var amount) || amount >= MaxAmount)
            throw new ChatRejectedException(maxDecimals == 0
                ? "Enter a whole number greater than zero."
                : $"Enter an amount greater than zero, with at most {maxDecimals} decimal places.");
        if (token.Standard == TokenStandards.Erc721 && amount != 1)
            throw new ChatRejectedException("ERC-721 tokens are sent one at a time.");
        return amount;
    }
}

/// <summary>
/// Relays status changes Ndeipi Enterprise Server writes to the queue (Processing, Confirmed,
/// Failed) into the chat. A trigger flags each change, so this reads only what moved.
/// </summary>
public sealed class TokenTransferQueueWatcher(
    IServiceScopeFactory scopes,
    IOptions<TokenOptions> options,
    ILogger<TokenTransferQueueWatcher> log) : BackgroundService
{
    const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.QueuePollInterval);
        try
        {
            do
            {
                try
                {
                    while (await PublishChangesAsync(stoppingToken) == BatchSize)
                    {
                    }
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    log.LogError(ex, "Couldn't relay token transfer status changes");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    async Task<int> PublishChangesAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var messageState = scope.ServiceProvider.GetRequiredService<MessageStateService>();

        var changed = await db.TokenTransfers.AsNoTracking()
            .Where(t => t.NotifyPending)
            .OrderBy(t => t.UpdatedAt)
            .Take(BatchSize)
            .Select(t => new { t.Id, t.MessageId, t.Status, t.TxHash, t.Error })
            .ToListAsync(ct);

        foreach (var transfer in changed)
        {
            await messageState.SetAsync(transfer.MessageId, new AssetTransferState(transfer.Id, transfer.Status, transfer.TxHash, transfer.Error), ct);

            // Clear the flag only if the status is still what was relayed; if Ndeipi moved it on in
            // the meantime, the flag stays up and the next pass relays the newer status.
            await db.TokenTransfers
                .Where(t => t.Id == transfer.Id && t.Status == transfer.Status)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.NotifyPending, false), ct);
        }

        return changed.Count;
    }
}
