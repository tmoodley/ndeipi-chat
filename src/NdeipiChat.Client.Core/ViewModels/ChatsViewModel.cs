using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NdeipiChat.Client.Extensions;
using NdeipiChat.Client.Realtime;
using NdeipiChat.Contracts;

namespace NdeipiChat.Client.ViewModels;

/// <summary>The Chats tab: every conversation, newest activity first, with unread badges.</summary>
public sealed partial class ChatsViewModel : ObservableObject
{
    readonly ChatApi _api;
    readonly ChatSession _session;
    readonly ChatExtensions _extensions;
    readonly INavigator _navigator;
    readonly IUiDispatcher _ui;
    readonly IDialogs _dialogs;
    readonly TimeProvider _clock;

    public ChatsViewModel(ChatApi api, ChatSession session, ChatExtensions extensions, INavigator navigator, IUiDispatcher ui, IDialogs dialogs, TimeProvider clock)
    {
        (_api, _session, _extensions, _navigator, _ui, _dialogs, _clock) = (api, session, extensions, navigator, ui, dialogs, clock);
        _session.MessageArrived += m => _ui.Post(() => OnMessage(m));
        _session.Connection.ConversationUpdated += c => _ui.Post(() => Upsert(c));
        _session.Connection.ReadReceipt += r => _ui.Post(() => OnReadReceipt(r));
        _session.Connection.Reconnected += () => _ui.Post(() => _ = RefreshAsync());
    }

    public ObservableCollection<ConversationItemViewModel> Conversations { get; } = [];

    [ObservableProperty]
    public partial bool IsRefreshing { get; set; }

    [ObservableProperty]
    public partial int TotalUnread { get; set; }

    public bool IsEmpty => Conversations.Count == 0;

    [RelayCommand]
    async Task RefreshAsync()
    {
        try
        {
            var conversations = await _api.GetConversationsAsync();
            Conversations.Clear();
            foreach (var conversation in conversations)
                Conversations.Add(new ConversationItemViewModel(conversation, PreviewOf(conversation), _clock));
        }
        catch (ApiException ex)
        {
            await _dialogs.AlertAsync("Couldn't load chats", ex.Message);
        }
        finally
        {
            IsRefreshing = false;
            Changed();
        }
    }

    [RelayCommand]
    Task OpenAsync(ConversationItemViewModel item) =>
        _navigator.GoToAsync(Routes.Chat, new Dictionary<string, object> { [Routes.ConversationIdParameter] = item.Id });

    void OnMessage(MessageDto message)
    {
        var item = Conversations.FirstOrDefault(c => c.Id == message.ConversationId);
        if (item is null)
        {
            // A chat this device hasn't seen yet -- someone just started it.
            _ = AddConversationAsync(message.ConversationId);
            return;
        }
        if (item.Conversation.LastMessage?.Id == message.Id)
            return;

        var unread = item.UnreadCount;
        if (message.SenderId != _session.MyUserId && _session.ActiveConversationId != message.ConversationId)
            unread++;

        var updated = item.Conversation with { LastMessage = message, LastActivityAt = message.SentAt, UnreadCount = unread };
        item.Update(updated, PreviewOf(updated));
        Conversations.Move(Conversations.IndexOf(item), 0);
        Changed();
    }

    void OnReadReceipt(ReadReceiptDto receipt)
    {
        if (receipt.UserId != _session.MyUserId)
            return;
        var item = Conversations.FirstOrDefault(c => c.Id == receipt.ConversationId);
        if (item is null)
            return;
        item.Update(item.Conversation with { UnreadCount = 0 }, item.Preview);
        Changed();
    }

    void Upsert(ConversationDto conversation)
    {
        var existing = Conversations.FirstOrDefault(c => c.Id == conversation.Id);
        if (existing is not null)
        {
            existing.Update(conversation, PreviewOf(conversation));
        }
        else
        {
            var index = Conversations.TakeWhile(c => c.Conversation.LastActivityAt > conversation.LastActivityAt).Count();
            Conversations.Insert(index, new ConversationItemViewModel(conversation, PreviewOf(conversation), _clock));
        }
        Changed();
    }

    async Task AddConversationAsync(Guid id)
    {
        try
        {
            var conversation = await _api.GetConversationAsync(id);
            _ui.Post(() => Upsert(conversation));
        }
        catch (ApiException)
        {
            // Shows up on the next refresh.
        }
    }

    string PreviewOf(ConversationDto conversation)
    {
        if (conversation.LastMessage is not { } last)
            return "";
        var vm = _extensions.Create(last, new MessageRenderContext(_session.MyUserId, id => conversation.Members.FirstOrDefault(m => m.Id == id)));
        return conversation.Type == ConversationType.Group && !vm.IsMine ? $"{vm.SenderName}: {vm.Preview}" : vm.Preview;
    }

    void Changed()
    {
        TotalUnread = Conversations.Sum(c => c.UnreadCount);
        OnPropertyChanged(nameof(IsEmpty));
    }
}

public sealed partial class ConversationItemViewModel : ObservableObject
{
    readonly TimeProvider _clock;

    public ConversationItemViewModel(ConversationDto conversation, string preview, TimeProvider clock)
    {
        _clock = clock;
        Conversation = conversation;
        Preview = preview;
        TimeText = "";
        Update(conversation, preview);
    }

    public ConversationDto Conversation { get; private set; }

    public Guid Id => Conversation.Id;
    public string Title => Conversation.Title;
    public string? AvatarUrl => Conversation.AvatarUrl;
    public string Initials => Display.Initials(Conversation.Title);
    public bool IsGroup => Conversation.Type == ConversationType.Group;

    [ObservableProperty]
    public partial string Preview { get; set; }

    [ObservableProperty]
    public partial string TimeText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnread), nameof(UnreadText))]
    public partial int UnreadCount { get; set; }

    public bool HasUnread => UnreadCount > 0;
    public string UnreadText => UnreadCount > 99 ? "99+" : UnreadCount.ToString();

    public void Update(ConversationDto conversation, string preview)
    {
        Conversation = conversation;
        Preview = preview;
        UnreadCount = conversation.UnreadCount;
        TimeText = Display.ListTime(conversation.LastMessage?.SentAt ?? conversation.LastActivityAt, _clock.GetUtcNow());
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(AvatarUrl));
        OnPropertyChanged(nameof(Initials));
    }
}
