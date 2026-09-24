using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NdeipiChat.Client.Realtime;
using NdeipiChat.Contracts;

namespace NdeipiChat.Client.ViewModels;

public sealed record TokenChoice(string Label, TokenRef? Token)
{
    public static TokenChoice Custom { get; } = new("Other token…", null);

    public bool IsCustom => Token is null;
}

/// <summary>Shared by both transfer forms: who in the chat the transfer goes to.</summary>
public abstract partial class TransferFormViewModel : ObservableObject, INavigationAware
{
    protected readonly ChatApi Api;
    protected readonly ChatSession Session;
    protected readonly INavigator Navigator;

    /// <summary>
    /// Fixed for the life of the form: a resend after a timeout reaches the server as the same
    /// message, so the transfer can't happen twice.
    /// </summary>
    protected readonly Guid ClientMessageId = Guid.NewGuid();

    protected Guid ConversationId;

    protected TransferFormViewModel(ChatApi api, ChatSession session, INavigator navigator)
    {
        (Api, Session, Navigator) = (api, session, navigator);
        Amount = "";
        Memo = "";
    }

    public ObservableCollection<UserDto> Recipients { get; } = [];

    public bool ShowRecipientPicker => Recipients.Count > 1;

    [ObservableProperty]
    public partial UserDto? Recipient { get; set; }

    [ObservableProperty]
    public partial string Amount { get; set; }

    [ObservableProperty]
    public partial string Memo { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial bool IsBusy { get; set; }

    public async Task OnNavigatedToAsync(IReadOnlyDictionary<string, object> parameters)
    {
        ConversationId = NavigationParameters.GetGuid(parameters, Routes.ConversationIdParameter);
        try
        {
            var conversation = await Api.GetConversationAsync(ConversationId);
            Recipients.Clear();
            foreach (var member in conversation.Members.Where(m => m.Id != Session.MyUserId))
                Recipients.Add(member);
            Recipient = Recipients.Count == 1 ? Recipients[0] : null;
            OnPropertyChanged(nameof(ShowRecipientPicker));
            await LoadAsync();
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    protected virtual Task LoadAsync() => Task.CompletedTask;

    protected abstract (string Kind, object Payload)? BuildMessage();

    [RelayCommand(CanExecute = nameof(CanSend))]
    async Task SendAsync()
    {
        ErrorMessage = null;
        if (Recipient is null)
        {
            ErrorMessage = "Choose who to send to.";
            return;
        }
        if (BuildMessage() is not { } message)
            return;

        IsBusy = true;
        try
        {
            await Session.SendAsync(ConversationId, message.Kind, message.Payload, ClientMessageId);
            await Navigator.GoBackAsync();
        }
        catch (ChatSendException ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    bool CanSend() => !IsBusy;

    /// <summary>The server reads amounts in invariant culture; accept a comma as the decimal mark too.</summary>
    protected static string NormaliseAmount(string? amount)
    {
        var text = (amount ?? "").Trim().Replace(" ", "");
        return text.Contains('.') ? text.Replace(",", "") : text.Replace(',', '.');
    }

    protected static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Send any token to someone in the chat; queued for Ndeipi Enterprise Server to put on-chain.</summary>
public sealed partial class AssetTransferViewModel : TransferFormViewModel
{
    public AssetTransferViewModel(ChatApi api, ChatSession session, INavigator navigator) : base(api, session, navigator)
    {
        Chain = "";
        Standard = TokenStandards.Erc20;
        Symbol = "";
        ContractAddress = "";
        Decimals = "";
        TokenId = "";
    }

    public ObservableCollection<TokenChoice> Tokens { get; } = [];

    public IReadOnlyList<string> Standards => TokenStandards.All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustom), nameof(IsNonFungible), nameof(AmountPlaceholder))]
    public partial TokenChoice? SelectedToken { get; set; }

    [ObservableProperty]
    public partial string Chain { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNonFungible), nameof(AmountPlaceholder), nameof(NeedsContract))]
    public partial string Standard { get; set; }

    [ObservableProperty]
    public partial string Symbol { get; set; }

    [ObservableProperty]
    public partial string ContractAddress { get; set; }

    [ObservableProperty]
    public partial string Decimals { get; set; }

    [ObservableProperty]
    public partial string TokenId { get; set; }

    public bool IsCustom => SelectedToken is { IsCustom: true };
    public bool NeedsContract => Standard != TokenStandards.Native;
    public bool IsNonFungible => TokenStandards.IsNonFungible(IsCustom ? Standard : SelectedToken?.Token?.Standard ?? "");
    public string AmountPlaceholder => IsNonFungible ? "Quantity (1)" : "Amount";

    protected override async Task LoadAsync()
    {
        Tokens.Clear();
        foreach (var token in await Api.GetTokensAsync())
            Tokens.Add(new TokenChoice($"{token.Symbol} · {token.Chain}", token));
        Tokens.Add(TokenChoice.Custom);
        SelectedToken = Tokens[0];
    }

    protected override (string, object)? BuildMessage()
    {
        TokenRef token;
        if (SelectedToken is null or { IsCustom: true })
        {
            if (string.IsNullOrWhiteSpace(Chain) || string.IsNullOrWhiteSpace(Symbol))
            {
                ErrorMessage = "Enter the token's chain and symbol.";
                return null;
            }
            int? decimals = int.TryParse(Decimals, out var d) ? d : null;
            token = new TokenRef(Chain.Trim(), Symbol.Trim(), Standard, NeedsContract ? NullIfEmpty(ContractAddress) : null, decimals, null);
        }
        else
        {
            token = SelectedToken.Token!;
        }

        if (IsNonFungible)
            token = token with { TokenId = NullIfEmpty(TokenId), Decimals = null };

        var amount = IsNonFungible && string.IsNullOrWhiteSpace(Amount) ? "1" : NormaliseAmount(Amount);
        return (MessageKinds.AssetTransfer, new AssetTransferPayload(Recipient!.Id, token, amount, NullIfEmpty(Memo)));
    }
}

/// <summary>Send money through Bridge to someone in the chat. Both people need a verified account.</summary>
public sealed partial class BankTransferViewModel : TransferFormViewModel
{
    public BankTransferViewModel(ChatApi api, ChatSession session, INavigator navigator) : base(api, session, navigator) =>
        Currency = "";

    [ObservableProperty]
    public partial string Currency { get; set; }

    [ObservableProperty]
    public partial bool NeedsVerification { get; set; }

    protected override async Task LoadAsync()
    {
        var status = await Api.GetBankingStatusAsync();
        Currency = status.Currency.ToUpperInvariant();
        NeedsVerification = !status.CanTransfer;
    }

    [RelayCommand]
    Task OpenWalletAsync() => Navigator.GoToAsync(Routes.Wallet);

    protected override (string, object)? BuildMessage()
    {
        if (NeedsVerification)
        {
            ErrorMessage = "Verify your identity before sending money.";
            return null;
        }
        return (MessageKinds.BankTransfer, new BankTransferPayload(Recipient!.Id, NormaliseAmount(Amount), Currency.ToLowerInvariant(), NullIfEmpty(Memo)));
    }
}
