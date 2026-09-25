using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NdeipiChat.Client.Realtime;
using NdeipiChat.Contracts;

namespace NdeipiChat.Client.ViewModels;

/// <summary>
/// The Contacts tab: your Shamwaris and requests, adding someone by email or phone number, other
/// people you chat with, search, and starting direct or group chats. Tapping a Shamwari opens the
/// chat with them, where the "+" panel sends money or tokens.
/// </summary>
public sealed partial class ContactsViewModel : ObservableObject
{
    readonly ChatApi _api;
    readonly ChatSession _session;
    readonly INavigator _navigator;
    readonly IDialogs _dialogs;
    readonly IUiDispatcher _ui;
    readonly HashSet<Guid> _selected = [];
    CancellationTokenSource? _search;

    public ContactsViewModel(ChatApi api, ChatSession session, INavigator navigator, IDialogs dialogs, IUiDispatcher ui)
    {
        (_api, _session, _navigator, _dialogs, _ui) = (api, session, navigator, dialogs, ui);
        Query = "";
        GroupTitle = "";
        NewContact = "";
        _session.Connection.ShamwarisChanged += () => _ = ReloadShamwarisAsync();
        _session.Connection.Reconnected += () => _ = ReloadShamwarisAsync();
    }

    public ObservableCollection<ContactItemViewModel> Shamwaris { get; } = [];
    public ObservableCollection<ShamwariRequestItemViewModel> Incoming { get; } = [];
    public ObservableCollection<ShamwariRequestItemViewModel> Outgoing { get; } = [];

    /// <summary>People you chat with who aren't Shamwaris.</summary>
    public ObservableCollection<ContactItemViewModel> Recent { get; } = [];

    public ObservableCollection<ContactItemViewModel> Results { get; } = [];

    [ObservableProperty]
    public partial string Query { get; set; }

    [ObservableProperty]
    public partial bool IsGroupMode { get; set; }

    [ObservableProperty]
    public partial string GroupTitle { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>The email address or phone number being added as a Shamwari.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddShamwariCommand))]
    public partial string NewContact { get; set; }

    /// <summary>What happened to the last add, shown under the field.</summary>
    [ObservableProperty]
    public partial string? AddStatus { get; set; }

    public int SelectedCount => _selected.Count;
    public bool HasResults => Results.Count > 0;
    public bool HasShamwaris => Shamwaris.Count > 0;
    public bool HasIncoming => Incoming.Count > 0;
    public bool HasOutgoing => Outgoing.Count > 0;
    public bool HasRecent => Recent.Count > 0;

    [RelayCommand]
    async Task LoadAsync()
    {
        try
        {
            var shamwaris = await _api.GetShamwarisAsync();
            var conversations = await _api.GetConversationsAsync();
            Apply(shamwaris);

            var friends = shamwaris.Shamwaris.Select(s => s.User.Id).ToHashSet();
            var people = conversations
                .Where(c => c.Type == ConversationType.Direct)
                .SelectMany(c => c.Members)
                .Where(m => m.Id != _session.MyUserId && !friends.Contains(m.Id))
                .DistinctBy(m => m.Id)
                .OrderBy(m => m.DisplayName);
            Recent.Clear();
            foreach (var person in people)
                Recent.Add(new ContactItemViewModel(person) { IsSelected = _selected.Contains(person.Id) });
            OnPropertyChanged(nameof(HasRecent));
        }
        catch (ApiException ex)
        {
            await _dialogs.AlertAsync("Couldn't load contacts", ex.Message);
        }
    }

    /// <summary>After a live change: someone sent, answered or withdrew a request.</summary>
    async Task ReloadShamwarisAsync()
    {
        try
        {
            var shamwaris = await _api.GetShamwarisAsync();
            _ui.Post(() => Apply(shamwaris));
        }
        catch (ApiException)
        {
            // Offline for now; the list reloads when the tab next appears or the hub reconnects.
        }
    }

    void Apply(ShamwariListDto list)
    {
        Shamwaris.Clear();
        foreach (var shamwari in list.Shamwaris)
            Shamwaris.Add(new ContactItemViewModel(shamwari.User) { IsSelected = _selected.Contains(shamwari.User.Id) });
        Incoming.Clear();
        foreach (var request in list.Incoming)
            Incoming.Add(new ShamwariRequestItemViewModel(request));
        Outgoing.Clear();
        foreach (var request in list.Outgoing)
            Outgoing.Add(new ShamwariRequestItemViewModel(request));

        var friends = Shamwaris.Select(s => s.User.Id).ToHashSet();
        foreach (var stale in Recent.Where(r => friends.Contains(r.User.Id)).ToList())
            Recent.Remove(stale);

        OnPropertyChanged(nameof(HasShamwaris));
        OnPropertyChanged(nameof(HasIncoming));
        OnPropertyChanged(nameof(HasOutgoing));
        OnPropertyChanged(nameof(HasRecent));
    }

    [RelayCommand(CanExecute = nameof(CanAddShamwari))]
    async Task AddShamwariAsync()
    {
        var contact = NewContact.Trim();
        IsBusy = true;
        try
        {
            var response = await _api.AddShamwariAsync(contact);
            Apply(response.List);
            AddStatus = response.Outcome switch
            {
                ShamwariAddOutcome.RequestSent => $"Request sent to {NameFor(response.List, contact)}.",
                ShamwariAddOutcome.NowShamwaris => $"You and {NameFor(response.List, contact)} are now Shamwaris.",
                ShamwariAddOutcome.Invited => $"No one on Ndeipi uses {contact} yet. They'll get your request when they join.",
                ShamwariAddOutcome.AlreadyShamwaris => "You're already Shamwaris.",
                _ => "You've already asked them. Waiting for them to accept."
            };
            NewContact = "";
        }
        catch (ApiException ex)
        {
            AddStatus = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    bool CanAddShamwari() => NewContact.Trim().Length > 0;

    /// <summary>The person just added: the newest outgoing request, or the newest Shamwari.</summary>
    static string NameFor(ShamwariListDto list, string contact) =>
        list.Outgoing.FirstOrDefault(r => r.User is not null)?.User?.DisplayName
        ?? list.Shamwaris.MaxBy(s => s.Since)?.User.DisplayName
        ?? contact;

    [RelayCommand]
    Task AcceptAsync(ShamwariRequestItemViewModel request) =>
        UpdateAsync("Couldn't accept", () => _api.AcceptShamwariAsync(request.Id));

    /// <summary>Declines a request to you, or cancels one you sent.</summary>
    [RelayCommand]
    Task DismissAsync(ShamwariRequestItemViewModel request) =>
        UpdateAsync(Outgoing.Contains(request) ? "Couldn't cancel" : "Couldn't decline", () => _api.DeleteShamwariRequestAsync(request.Id));

    [RelayCommand]
    Task RemoveAsync(ContactItemViewModel shamwari) =>
        UpdateAsync("Couldn't remove", () => _api.RemoveShamwariAsync(shamwari.User.Id));

    async Task UpdateAsync(string failure, Func<Task<ShamwariListDto>> change)
    {
        try
        {
            Apply(await change());
        }
        catch (ApiException ex)
        {
            await _dialogs.AlertAsync(failure, ex.Message);
        }
    }

    partial void OnQueryChanged(string value) => _ = SearchAsync(value);

    async Task SearchAsync(string query)
    {
        _search?.Cancel();
        var cts = _search = new CancellationTokenSource();
        try
        {
            await Task.Delay(300, cts.Token);
            var users = query.Trim().Length < 2 ? [] : await _api.SearchUsersAsync(query.Trim(), cts.Token);
            if (cts.IsCancellationRequested)
                return;
            Results.Clear();
            foreach (var user in users)
                Results.Add(new ContactItemViewModel(user) { IsSelected = _selected.Contains(user.Id) });
            OnPropertyChanged(nameof(HasResults));
        }
        catch (OperationCanceledException)
        {
        }
        catch (ApiException ex)
        {
            await _dialogs.AlertAsync("Search failed", ex.Message);
        }
    }

    /// <summary>Opens a direct chat -- or, when picking a group, toggles the person.</summary>
    [RelayCommand]
    async Task SelectAsync(ContactItemViewModel contact)
    {
        if (IsGroupMode)
        {
            if (!_selected.Remove(contact.User.Id))
                _selected.Add(contact.User.Id);
            foreach (var item in Shamwaris.Concat(Recent).Concat(Results).Where(i => i.User.Id == contact.User.Id))
                item.IsSelected = _selected.Contains(contact.User.Id);
            OnPropertyChanged(nameof(SelectedCount));
            CreateGroupCommand.NotifyCanExecuteChanged();
            return;
        }

        await OpenAsync(new CreateConversationRequest(ConversationType.Direct, [contact.User.Id], null));
    }

    [RelayCommand]
    void ToggleGroupMode()
    {
        IsGroupMode = !IsGroupMode;
        _selected.Clear();
        foreach (var item in Shamwaris.Concat(Recent).Concat(Results))
            item.IsSelected = false;
        GroupTitle = "";
        OnPropertyChanged(nameof(SelectedCount));
        CreateGroupCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanCreateGroup))]
    async Task CreateGroupAsync()
    {
        await OpenAsync(new CreateConversationRequest(ConversationType.Group, _selected.ToList(), GroupTitle));
        ToggleGroupMode();
    }

    bool CanCreateGroup() => _selected.Count >= 2;

    async Task OpenAsync(CreateConversationRequest request)
    {
        IsBusy = true;
        try
        {
            var conversation = await _api.CreateConversationAsync(request);
            await _navigator.GoToAsync(Routes.Chat, new Dictionary<string, object> { [Routes.ConversationIdParameter] = conversation.Id });
        }
        catch (ApiException ex)
        {
            await _dialogs.AlertAsync("Couldn't start the chat", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

public sealed partial class ContactItemViewModel(UserDto user) : ObservableObject
{
    public UserDto User => user;
    public string Name => user.DisplayName;
    public string? Username => user.Username is null ? null : "@" + user.Username;
    public string? AvatarUrl => user.AvatarUrl;
    public string Initials => Display.Initials(user.DisplayName);

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>A request to or from someone, or an invite to someone not on Ndeipi yet.</summary>
public sealed class ShamwariRequestItemViewModel(ShamwariRequestDto request)
{
    public Guid Id => request.Id;
    public UserDto? User => request.User;
    public bool IsInvite => request.User is null;
    public string Name => request.User?.DisplayName ?? request.Contact ?? "";
    public string Detail => IsInvite ? "Invited · not on Ndeipi yet" : request.User?.Username is { } u ? "@" + u : "";
    public string? AvatarUrl => request.User?.AvatarUrl;
    public string Initials => IsInvite ? "?" : Display.Initials(Name);
}
