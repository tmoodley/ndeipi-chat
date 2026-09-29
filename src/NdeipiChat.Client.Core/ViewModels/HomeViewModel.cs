using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NdeipiChat.Client.Realtime;
using NdeipiChat.Contracts;

namespace NdeipiChat.Client.ViewModels;

/// <summary>Each app's colour, the same in every shell ("blue" and so on; the web adds "tone-").</summary>
public static class AppTones
{
    public static readonly IReadOnlyList<string> All = ["blue", "green", "red", "yellow", "purple", "brown", "teal", "orange", "pink"];

    public static string For(string appId) => appId switch
    {
        BuiltInApps.Chats => "blue",
        BuiltInApps.Feed => "pink",
        BuiltInApps.Shamwaris => "green",
        BuiltInApps.Wallet => "brown",
        BuiltInApps.Herd => "yellow",
        "events" => "purple",
        "gigs" => "orange",
        "inventory" => "teal",
        _ => ForName(appId)
    };

    /// <summary>
    /// A colour for any name (an app, a person's initials) that stays the same on every launch and
    /// device; string.GetHashCode doesn't, it changes each run.
    /// </summary>
    public static string ForName(string? name)
    {
        var hash = 17u;
        foreach (var c in name ?? "")
            hash = hash * 31 + c;
        return All[(int)(hash % (uint)All.Count)];
    }
}

/// <summary>Someone on the home screen: in your frequent contacts, or to pick for a transfer.</summary>
public sealed class HomePersonViewModel(UserDto user)
{
    public UserDto User => user;
    public string Name => user.DisplayName;
    public string FirstName => HomeViewModel.FirstNameOf(user.DisplayName);
    public string Initials => Display.Initials(user.DisplayName);
    public string? AvatarUrl => user.AvatarUrl;
}

/// <summary>What to do once someone's picked from the home screen.</summary>
public enum HomeAction
{
    Chat,
    SendMoney,
    SendTokens
}

/// <summary>
/// Home: who you are, your wallet, quick ways to move money, the people you talk to most, your
/// pinned apps and recent chats. The same screen as the web app's home.
/// </summary>
public sealed partial class HomeViewModel : ObservableObject
{
    public const int MaxFrequent = 12;
    public const int MaxRecent = 5;

    readonly ChatApi _api;
    readonly ChatSession _session;
    readonly INavigator _navigator;
    readonly TimeProvider _clock;

    public HomeViewModel(ChatApi api, ChatSession session, LauncherViewModel launcher, ChatsViewModel chats, ContactsViewModel contacts,
        INavigator navigator, IUiDispatcher ui, TimeProvider clock)
    {
        (_api, _session, _navigator, _clock) = (api, session, navigator, clock);
        (Launcher, Chats, Contacts) = (launcher, chats, contacts);

        // Chats and Shamwaris load in the background after sign-in, often after Home first shows.
        NotifyCollectionChangedEventHandler changed = (_, _) => ui.Post(Rebuild);
        chats.Conversations.CollectionChanged += changed;
        contacts.Shamwaris.CollectionChanged += changed;
        contacts.Incoming.CollectionChanged += changed;
        launcher.Pinned.CollectionChanged += changed;
        _session.MeChanged += _ => ui.Post(() => OnPropertyChanged(string.Empty));
        _session.Connection.BankingStatusChanged += status => ui.Post(() => Status = status);
        Rebuild();
    }

    public LauncherViewModel Launcher { get; }
    public ChatsViewModel Chats { get; }
    public ContactsViewModel Contacts { get; }

    public ObservableCollection<HomePersonViewModel> Frequent { get; } = [];
    public ObservableCollection<ConversationItemViewModel> Recent { get; } = [];

    public string DateText => _clock.GetLocalNow().ToString("dddd d MMMM");
    public string Greeting => _session.Me?.DisplayName is { Length: > 0 } name ? $"Mhoro, {FirstNameOf(name)}" : "Mhoro";
    public string Initials => Display.Initials(_session.Me?.DisplayName ?? "N");
    public string? AvatarUrl => _session.Me?.AvatarUrl;

    public int RequestCount => Contacts.Incoming.Count;
    public bool HasRequests => RequestCount > 0;
    public bool HasFrequent => Frequent.Count > 0;
    public bool HasRecent => Recent.Count > 0;

    public bool ShowWallet => Launcher.Allows(BuiltInApps.Wallet);
    public bool ShowFeed => Launcher.Allows(BuiltInApps.Feed);
    public bool ShowShamwaris => Launcher.Allows(BuiltInApps.Shamwaris);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BalanceShown), nameof(BalanceHidden), nameof(NeedsVerification), nameof(BalanceText), nameof(OtherBalancesText), nameof(WalletFoot))]
    public partial BankingStatusDto? Status { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BalanceShown), nameof(BalanceHidden), nameof(NeedsVerification), nameof(BalanceText), nameof(OtherBalancesText))]
    public partial IReadOnlyList<BalanceDto>? Balances { get; set; }

    [ObservableProperty]
    public partial bool IsLoadingBalance { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    /// <summary>The balance stays hidden until asked for, as on the web.</summary>
    public bool BalanceHidden => Balances is null;
    public bool NeedsVerification => Balances is not null && Status is { CanTransfer: false };
    public bool BalanceShown => Balances is not null && !NeedsVerification;

    public string BalanceText => Balances?.FirstOrDefault() is { } b
        ? $"{b.Amount} {b.Currency.ToUpperInvariant()}"
        : $"0 {Status?.Currency.ToUpperInvariant()}";

    public string OtherBalancesText => string.Join("\n", (Balances ?? []).Skip(1).Select(b => $"{b.Amount} {b.Currency.ToUpperInvariant()} · {b.Chain}"));

    public string WalletFoot => Status?.WalletAddress is { } address ? Display.ShortAddress(address) : "Ndeipi · chat.ndeipi.com";

    /// <summary>Everyone you could pick: people from your recent one-to-one chats, then your other Shamwaris.</summary>
    public IReadOnlyList<HomePersonViewModel> Everyone => Chats.Conversations
        .Where(c => !c.IsGroup)
        .SelectMany(c => c.Conversation.Members.Where(m => m.Id != _session.MyUserId))
        .Concat(Contacts.Shamwaris.Select(s => s.User))
        .DistinctBy(u => u.Id)
        .Select(u => new HomePersonViewModel(u))
        .ToList();

    public static string FirstNameOf(string name) => name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? name;

    void Rebuild()
    {
        Frequent.Clear();
        foreach (var person in Everyone.Take(MaxFrequent))
            Frequent.Add(person);
        Recent.Clear();
        foreach (var chat in Chats.Conversations.Take(MaxRecent))
            Recent.Add(chat);
        OnPropertyChanged(nameof(HasFrequent));
        OnPropertyChanged(nameof(HasRecent));
        OnPropertyChanged(nameof(RequestCount));
        OnPropertyChanged(nameof(HasRequests));
        OnPropertyChanged(nameof(ShowWallet));
        OnPropertyChanged(nameof(ShowFeed));
        OnPropertyChanged(nameof(ShowShamwaris));
    }

    /// <summary>
    /// Home's own pull-to-refresh. It has to be Home's: while a RefreshView is refreshing its content
    /// takes no taps, so a flag nothing cleared (it used to share the launcher's) froze the screen.
    /// </summary>
    [ObservableProperty]
    public partial bool IsRefreshing { get; set; }

    [RelayCommand]
    async Task LoadAsync()
    {
        if (!Launcher.IsLoaded)
            await Launcher.LoadCommand.ExecuteAsync(null);
        Rebuild();
    }

    /// <summary>Pull to refresh: apps, chats and Shamwaris again, and the balance if it's showing.</summary>
    [RelayCommand]
    async Task RefreshAsync()
    {
        try
        {
            await Task.WhenAll(
                Launcher.LoadCommand.ExecuteAsync(null),
                Chats.RefreshCommand.ExecuteAsync(null),
                Contacts.LoadCommand.ExecuteAsync(null));
            if (Balances is not null)
                await ShowBalanceAsync();
            Rebuild();
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    [RelayCommand]
    async Task ShowBalanceAsync()
    {
        (IsLoadingBalance, ErrorMessage) = (true, null);
        try
        {
            Status = await _api.GetBankingStatusAsync();
            Balances = Status.CanTransfer ? await _api.GetBalancesAsync() : [];
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoadingBalance = false;
        }
    }

    [RelayCommand]
    Task OpenWalletAsync() => _navigator.GoToAsync(Routes.Wallet);

    [RelayCommand]
    Task OpenChatsAsync() => _navigator.GoToAsync(Routes.SubApp(BuiltInApps.Chats));

    [RelayCommand]
    Task OpenShamwarisAsync() => _navigator.GoToAsync(Routes.SubApp(BuiltInApps.Shamwaris));

    [RelayCommand]
    Task OpenAppsAsync() => _navigator.GoToAsync(Routes.Launcher);

    [RelayCommand]
    Task ComposePostAsync() => _navigator.GoToAsync(Routes.ComposePost);

    [RelayCommand]
    Task OpenPersonAsync(HomePersonViewModel person) => OpenAsync(person, HomeAction.Chat);

    /// <summary>Opens (or starts) the one-to-one chat with them, or its money or token form.</summary>
    public async Task OpenAsync(HomePersonViewModel person, HomeAction action)
    {
        ErrorMessage = null;
        try
        {
            var chat = await _api.CreateConversationAsync(new CreateConversationRequest(ConversationType.Direct, [person.User.Id], null));
            var route = action switch
            {
                HomeAction.SendMoney => Routes.BankTransfer,
                HomeAction.SendTokens => Routes.AssetTransfer,
                _ => Routes.Chat
            };
            await _navigator.GoToAsync(route, new Dictionary<string, object> { [Routes.ConversationIdParameter] = chat.Id });
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
        }
    }
}
