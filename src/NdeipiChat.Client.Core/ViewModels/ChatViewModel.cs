using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NdeipiChat.Client.Extensions;
using NdeipiChat.Client.Realtime;
using NdeipiChat.Contracts;

namespace NdeipiChat.Client.ViewModels;

/// <summary>
/// One conversation: history, live messages, optimistic sends with retry, typing and read
/// receipts, and the "+" panel of extension actions.
/// </summary>
public sealed partial class ChatViewModel : ObservableObject, INavigationAware, IActivatable, IDisposable
{
    const int PageSize = 50;
    static readonly TimeSpan TimestampGap = TimeSpan.FromMinutes(5);
    static readonly TimeSpan TypingThrottle = TimeSpan.FromSeconds(3);
    static readonly TimeSpan TypingTimeout = TimeSpan.FromSeconds(4);

    readonly ChatApi _api;
    readonly ChatSession _session;
    readonly ChatExtensions _extensions;
    readonly IUiDispatcher _ui;
    readonly IDialogs _dialogs;
    readonly TimeProvider _clock;

    ConversationDto? _conversation;
    bool _active;
    DateTimeOffset _lastTypingSent;
    int _typingVersion;

    public ChatViewModel(ChatApi api, ChatSession session, ChatExtensions extensions, IUiDispatcher ui, IDialogs dialogs, TimeProvider clock)
    {
        (_api, _session, _extensions, _ui, _dialogs, _clock) = (api, session, extensions, ui, dialogs, clock);
        Title = "";
        Draft = "";
        _session.MessageArrived += OnMessageArrived;
        _session.Connection.MessageStateChanged += OnMessageStateChanged;
        _session.Connection.Typing += OnTyping;
        _session.Connection.Reconnected += OnReconnected;
    }

    public ObservableCollection<MessageViewModel> Messages { get; } = [];

    public IReadOnlyList<IComposerAction> Actions { get; private set; } = [];

    public Guid? ConversationId => _conversation?.Id;

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    public partial string Draft { get; set; }

    [ObservableProperty]
    public partial bool IsExtensionPanelOpen { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool HasMore { get; set; }

    [ObservableProperty]
    public partial string? TypingText { get; set; }

    Guid MyId => _session.MyUserId;

    public async Task OnNavigatedToAsync(IReadOnlyDictionary<string, object> parameters)
    {
        var id = NavigationParameters.GetGuid(parameters, Routes.ConversationIdParameter);
        if (_conversation?.Id == id)
            return;

        IsLoading = true;
        try
        {
            _conversation = await _api.GetConversationAsync(id);
            Title = _conversation.Title;
            Actions = _extensions.Actions.Where(a => a.IsAvailable(_conversation)).ToList();
            OnPropertyChanged(nameof(Actions));
            OnPropertyChanged(nameof(ConversationId));
            if (_active)
                _session.ActiveConversationId = id;
            await LoadLatestAsync();
        }
        catch (ApiException ex)
        {
            await _dialogs.AlertAsync("Couldn't open the chat", ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void Activate()
    {
        _active = true;
        if (_conversation is not null)
            _session.ActiveConversationId = _conversation.Id;
        MarkLatestRead();
    }

    public void Deactivate()
    {
        _active = false;
        if (_session.ActiveConversationId == _conversation?.Id)
            _session.ActiveConversationId = null;
    }

    [RelayCommand]
    async Task SendTextAsync()
    {
        var text = Draft?.Trim();
        if (string.IsNullOrEmpty(text) || _conversation is null)
            return;
        Draft = "";
        await SendAsync(MessageKinds.Text, new TextPayload(text), Guid.NewGuid());
    }

    /// <summary>Resends a failed message with its original client id, so it can never land twice.</summary>
    [RelayCommand]
    async Task RetryAsync(MessageViewModel message)
    {
        if (!message.IsFailed || _conversation is null)
            return;
        Messages.Remove(message);
        await SendAsync(message.Kind, message.Message.Payload, message.ClientMessageId);
    }

    [RelayCommand]
    void ToggleExtensionPanel() => IsExtensionPanelOpen = !IsExtensionPanelOpen;

    [RelayCommand]
    async Task RunActionAsync(IComposerAction action)
    {
        if (_conversation is null)
            return;
        IsExtensionPanelOpen = false;
        await action.ExecuteAsync(new ComposerContext(_conversation, MyId));
    }

    [RelayCommand]
    async Task LoadOlderAsync()
    {
        if (!HasMore || IsLoading || _conversation is null)
            return;
        var oldest = Messages.FirstOrDefault(m => m.Delivery == DeliveryStatus.Sent);
        if (oldest is null)
            return;

        IsLoading = true;
        try
        {
            var page = await _api.GetMessagesAsync(_conversation.Id, oldest.Id, PageSize);
            for (var i = page.Count - 1; i >= 0; i--)
            {
                if (Messages.All(m => m.Id != page[i].Id))
                    Messages.Insert(0, Create(page[i]));
            }
            HasMore = page.Count == PageSize;
            Restamp();
        }
        catch (ApiException ex)
        {
            await _dialogs.AlertAsync("Couldn't load older messages", ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnDraftChanged(string value)
    {
        if (_conversation is null || string.IsNullOrEmpty(value))
            return;
        var now = _clock.GetUtcNow();
        if (now - _lastTypingSent < TypingThrottle)
            return;
        _lastTypingSent = now;
        Forget(_session.Connection.TypingAsync(_conversation.Id));
    }

    async Task SendAsync(string kind, object payload, Guid clientMessageId)
    {
        var conversationId = _conversation!.Id;
        var pending = Create(new MessageDto(clientMessageId, conversationId, MyId, kind, ContractJson.ToElement(payload), null, _clock.GetUtcNow(), clientMessageId));
        pending.Delivery = DeliveryStatus.Sending;
        Append(pending);

        try
        {
            pending.Confirm(await _session.SendAsync(conversationId, kind, payload, clientMessageId));
        }
        catch (ChatSendException ex)
        {
            pending.Delivery = DeliveryStatus.Failed;
            pending.DeliveryError = ex.Message;
        }
    }

    async Task LoadLatestAsync()
    {
        var page = await _api.GetMessagesAsync(_conversation!.Id, null, PageSize);
        var unsent = Messages.Where(m => m.Delivery != DeliveryStatus.Sent).ToList();

        Messages.Clear();
        foreach (var message in page)
            Messages.Add(Create(message));
        foreach (var message in unsent.Where(u => page.All(p => p.ClientMessageId != u.ClientMessageId || p.SenderId != MyId)))
            Messages.Add(message);

        HasMore = page.Count == PageSize;
        Restamp();
        MarkLatestRead();
    }

    void OnMessageArrived(MessageDto message) => _ui.Post(() =>
    {
        if (message.ConversationId != _conversation?.Id)
            return;

        var existing = Messages.FirstOrDefault(m => m.Id == message.Id
            || (message.SenderId == MyId && m.IsMine && m.ClientMessageId == message.ClientMessageId));
        if (existing is not null)
        {
            existing.Confirm(message);
            return;
        }

        Append(Create(message));
        if (message.SenderId != MyId)
        {
            TypingText = null;
            if (_active)
                Forget(_session.Connection.MarkReadAsync(message.ConversationId, message.Id));
        }
    });

    void OnMessageStateChanged(MessageStateDto state) => _ui.Post(() =>
    {
        if (state.ConversationId == _conversation?.Id)
            Messages.FirstOrDefault(m => m.Id == state.MessageId)?.ApplyState(state.State);
    });

    void OnTyping(TypingDto typing) => _ui.Post(() =>
    {
        if (typing.ConversationId != _conversation?.Id || typing.UserId == MyId)
            return;
        TypingText = $"{FindUser(typing.UserId)?.DisplayName ?? "Someone"} is typing…";
        var version = ++_typingVersion;
        _ = HideTypingLaterAsync(version);
    });

    async Task HideTypingLaterAsync(int version)
    {
        await Task.Delay(TypingTimeout);
        _ui.Post(() =>
        {
            if (version == _typingVersion)
                TypingText = null;
        });
    }

    void OnReconnected() => _ui.Post(async () =>
    {
        // Nothing sent while the connection was down gets replayed, so reload.
        if (_conversation is null)
            return;
        try
        {
            await LoadLatestAsync();
        }
        catch (ApiException)
        {
        }
    });

    void MarkLatestRead()
    {
        if (!_active || _conversation is null)
            return;
        var last = Messages.LastOrDefault(m => m.Delivery == DeliveryStatus.Sent && !m.IsMine);
        if (last is not null)
            Forget(_session.Connection.MarkReadAsync(_conversation.Id, last.Id));
    }

    MessageViewModel Create(MessageDto message) =>
        _extensions.Create(message, new MessageRenderContext(MyId, FindUser));

    UserDto? FindUser(Guid id) => _conversation?.Members.FirstOrDefault(m => m.Id == id);

    void Append(MessageViewModel message)
    {
        Messages.Add(message);
        Stamp(Messages.Count - 1);
    }

    void Restamp()
    {
        for (var i = 0; i < Messages.Count; i++)
            Stamp(i);
    }

    void Stamp(int index) =>
        Messages[index].ShowTimestamp = index == 0 || Messages[index].SentAt - Messages[index - 1].SentAt > TimestampGap;

    /// <summary>Typing notices and read receipts are best-effort; a lost one isn't worth an error.</summary>
    static async void Forget(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception)
        {
        }
    }

    public void Dispose()
    {
        Deactivate();
        _session.MessageArrived -= OnMessageArrived;
        _session.Connection.MessageStateChanged -= OnMessageStateChanged;
        _session.Connection.Typing -= OnTyping;
        _session.Connection.Reconnected -= OnReconnected;
    }
}

public static class NavigationParameters
{
    public static Guid GetGuid(IReadOnlyDictionary<string, object> parameters, string key) =>
        parameters.TryGetValue(key, out var value) switch
        {
            true when value is Guid guid => guid,
            true when Guid.TryParse(value?.ToString(), out var parsed) => parsed,
            _ => throw new ArgumentException($"Missing navigation parameter '{key}'.")
        };
}
