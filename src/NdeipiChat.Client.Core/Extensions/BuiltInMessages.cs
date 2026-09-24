using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using NdeipiChat.Contracts;

namespace NdeipiChat.Client.Extensions;

public sealed class TextMessageViewModel : MessageViewModel
{
    public TextMessageViewModel(MessageDto message, MessageRenderContext context) : base(message, context) =>
        Text = ContractJson.Read<TextPayload>(message.Payload)?.Text ?? "";

    public string Text { get; }
    public override string Preview => Text;
}

public sealed class TextMessageRenderer : IMessageRenderer
{
    public string Kind => MessageKinds.Text;
    public MessageViewModel Create(MessageDto message, MessageRenderContext context) => new TextMessageViewModel(message, context);
}

/// <summary>Shared by token and money transfers: who sent what, and how far it's got.</summary>
public abstract partial class TransferMessageViewModel : MessageViewModel
{
    protected TransferMessageViewModel(MessageDto message, MessageRenderContext context, Guid recipientId) : base(message, context)
    {
        var recipient = context.FindUser(recipientId);
        IsToMe = recipientId == context.MyUserId;
        Headline = IsMine
            ? $"To {recipient?.DisplayName ?? "someone"}"
            : IsToMe ? $"From {SenderName}" : $"{SenderName} to {recipient?.DisplayName ?? "someone"}";
        Status = TransferStatuses.Pending;
    }

    public bool IsToMe { get; }
    public string Headline { get; }
    public abstract string AmountText { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsConfirmed), nameof(IsFailedTransfer), nameof(IsInProgress))]
    public partial string Status { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string? Error { get; set; }

    public bool IsConfirmed => Status == TransferStatuses.Confirmed;
    public bool IsFailedTransfer => Status == TransferStatuses.Failed;
    public bool IsInProgress => !TransferStatuses.IsFinal(Status);

    public virtual string StatusText => Status switch
    {
        TransferStatuses.Pending => "Queued",
        TransferStatuses.Processing => "Processing…",
        TransferStatuses.Confirmed => IsMine ? "Sent" : "Received",
        TransferStatuses.Failed => Error ?? "Failed",
        _ => Status
    };
}

public sealed partial class AssetTransferMessageViewModel : TransferMessageViewModel
{
    public AssetTransferMessageViewModel(MessageDto message, MessageRenderContext context, AssetTransferPayload payload)
        : base(message, context, payload.RecipientId) => Payload = payload;

    public AssetTransferPayload Payload { get; }

    public override string AmountText => TokenStandards.IsNonFungible(Payload.Token.Standard)
        ? $"{Payload.Token.Symbol} #{Payload.Token.TokenId}" + (Payload.Amount == "1" ? "" : $" ×{Payload.Amount}")
        : $"{Payload.Amount} {Payload.Token.Symbol}";

    public string ChainText => Payload.Token.Chain;
    public string? Memo => Payload.Memo;
    public bool HasMemo => Memo is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTxHash), nameof(ShortTxHash))]
    public partial string? TxHash { get; set; }

    public bool HasTxHash => TxHash is not null;
    public string ShortTxHash => Display.ShortAddress(TxHash);

    public override string StatusText => Status == TransferStatuses.Processing ? "Minting on-chain…" : base.StatusText;

    public override string Preview => $"[Transfer] {AmountText}";

    public override void ApplyState(JsonElement state)
    {
        if (ContractJson.Read<AssetTransferState>(state) is not { } s)
            return;
        Error = s.Error;
        TxHash = s.TxHash;
        Status = s.Status;
    }
}

public sealed class AssetTransferRenderer : IMessageRenderer
{
    public string Kind => MessageKinds.AssetTransfer;

    public MessageViewModel Create(MessageDto message, MessageRenderContext context) =>
        new AssetTransferMessageViewModel(message, context, ContractJson.Read<AssetTransferPayload>(message.Payload)
            ?? throw new JsonException("Empty transfer payload."));
}

public sealed class BankTransferMessageViewModel : TransferMessageViewModel
{
    public BankTransferMessageViewModel(MessageDto message, MessageRenderContext context, BankTransferPayload payload)
        : base(message, context, payload.RecipientId) => Payload = payload;

    public BankTransferPayload Payload { get; }

    public override string AmountText =>
        (decimal.TryParse(Payload.Amount, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var amount)
            ? amount.ToString("N2")
            : Payload.Amount) + " " + Payload.Currency.ToUpperInvariant();

    public string? Memo => Payload.Memo;
    public bool HasMemo => Memo is not null;

    public override string Preview => $"[Money] {AmountText}";

    public override void ApplyState(JsonElement state)
    {
        if (ContractJson.Read<BankTransferState>(state) is not { } s)
            return;
        Error = s.Error;
        Status = s.Status;
    }
}

public sealed class BankTransferRenderer : IMessageRenderer
{
    public string Kind => MessageKinds.BankTransfer;

    public MessageViewModel Create(MessageDto message, MessageRenderContext context) =>
        new BankTransferMessageViewModel(message, context, ContractJson.Read<BankTransferPayload>(message.Payload)
            ?? throw new JsonException("Empty transfer payload."));
}

public sealed class SendTokenAction(INavigator navigator) : IComposerAction
{
    public string Title => "Transfer";
    public string Glyph => "⇄";
    public int Order => 10;

    public Task ExecuteAsync(ComposerContext context) =>
        navigator.GoToAsync(Routes.AssetTransfer, new Dictionary<string, object> { [Routes.ConversationIdParameter] = context.Conversation.Id });
}

public sealed class SendMoneyAction(INavigator navigator) : IComposerAction
{
    public string Title => "Send money";
    public string Glyph => "$";
    public int Order => 20;

    public Task ExecuteAsync(ComposerContext context) =>
        navigator.GoToAsync(Routes.BankTransfer, new Dictionary<string, object> { [Routes.ConversationIdParameter] = context.Conversation.Id });
}
