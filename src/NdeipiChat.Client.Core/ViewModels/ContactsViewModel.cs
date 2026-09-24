using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NdeipiChat.Client.Realtime;
using NdeipiChat.Contracts;

namespace NdeipiChat.Client.ViewModels;

/// <summary>The Contacts tab: people you've chatted with, search, and starting direct or group chats.</summary>
public sealed partial class ContactsViewModel : ObservableObject
{
    readonly ChatApi _api;
    readonly ChatSession _session;
    readonly INavigator _navigator;
    readonly IDialogs _dialogs;
    readonly HashSet<Guid> _selected = [];
    CancellationTokenSource? _search;

    public ContactsViewModel(ChatApi api, ChatSession session, INavigator navigator, IDialogs dialogs)
    {
        (_api, _session, _navigator, _dialogs) = (api, session, navigator, dialogs);
        Query = "";
        GroupTitle = "";
    }

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

    public int SelectedCount => _selected.Count;
    public bool HasResults => Results.Count > 0;

    [RelayCommand]
    async Task LoadAsync()
    {
        try
        {
            var conversations = await _api.GetConversationsAsync();
            var people = conversations
                .Where(c => c.Type == ConversationType.Direct)
                .SelectMany(c => c.Members)
                .Where(m => m.Id != _session.MyUserId)
                .DistinctBy(m => m.Id)
                .OrderBy(m => m.DisplayName);
            Recent.Clear();
            foreach (var person in people)
                Recent.Add(new ContactItemViewModel(person) { IsSelected = _selected.Contains(person.Id) });
        }
        catch (ApiException ex)
        {
            await _dialogs.AlertAsync("Couldn't load contacts", ex.Message);
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
            foreach (var item in Recent.Concat(Results).Where(i => i.User.Id == contact.User.Id))
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
        foreach (var item in Recent.Concat(Results))
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
