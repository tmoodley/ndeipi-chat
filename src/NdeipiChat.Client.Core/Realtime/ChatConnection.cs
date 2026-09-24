using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using NdeipiChat.Client.Auth;
using NdeipiChat.Contracts;

namespace NdeipiChat.Client.Realtime;

/// <summary>
/// The SignalR connection to the chat hub. Reconnects forever with backoff; after a reconnect,
/// view models reload what they show, since events sent while offline are not replayed.
/// </summary>
public sealed class ChatConnection : IAsyncDisposable
{
    readonly HubConnection _hub;
    readonly SemaphoreSlim _startLock = new(1, 1);

    public ChatConnection(ClientOptions options, AuthService auth, Action<HttpConnectionOptions>? configureHttp = null)
    {
        _hub = new HubConnectionBuilder()
            .WithUrl(new Uri(options.ApiBaseUrl, ChatHubContract.Path.TrimStart('/')), o =>
            {
                o.AccessTokenProvider = () => auth.GetAccessTokenAsync();
                configureHttp?.Invoke(o);
            })
            .WithAutomaticReconnect(new ForeverRetryPolicy())
            .AddJsonProtocol(o => ContractJson.Configure(o.PayloadSerializerOptions))
            .Build();

        _hub.On<MessageDto>(nameof(IChatClient.MessageReceived), m => MessageReceived?.Invoke(m));
        _hub.On<MessageStateDto>(nameof(IChatClient.MessageStateChanged), s => MessageStateChanged?.Invoke(s));
        _hub.On<ConversationDto>(nameof(IChatClient.ConversationUpdated), c => ConversationUpdated?.Invoke(c));
        _hub.On<TypingDto>(nameof(IChatClient.Typing), t => Typing?.Invoke(t));
        _hub.On<ReadReceiptDto>(nameof(IChatClient.ReadReceipt), r => ReadReceipt?.Invoke(r));
        _hub.On<BankingStatusDto>(nameof(IChatClient.BankingStatusChanged), b => BankingStatusChanged?.Invoke(b));

        _hub.Reconnecting += _ => Raise(StateChanged);
        _hub.Reconnected += _ =>
        {
            Reconnected?.Invoke();
            return Raise(StateChanged);
        };
        _hub.Closed += _ => Raise(StateChanged);
    }

    public event Action<MessageDto>? MessageReceived;
    public event Action<MessageStateDto>? MessageStateChanged;
    public event Action<ConversationDto>? ConversationUpdated;
    public event Action<TypingDto>? Typing;
    public event Action<ReadReceiptDto>? ReadReceipt;
    public event Action<BankingStatusDto>? BankingStatusChanged;
    public event Action? Reconnected;
    public event Action? StateChanged;

    public HubConnectionState State => _hub.State;

    /// <summary>Connects, retrying with backoff until it succeeds or <paramref name="ct"/> is cancelled.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        await _startLock.WaitAsync(ct);
        try
        {
            var delay = TimeSpan.FromSeconds(1);
            while (_hub.State == HubConnectionState.Disconnected)
            {
                try
                {
                    await _hub.StartAsync(ct);
                    StateChanged?.Invoke();
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    await Task.Delay(delay, ct);
                    delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
                }
            }
        }
        finally
        {
            _startLock.Release();
        }
    }

    public Task StopAsync() => _hub.StopAsync();

    public Task<MessageDto> SendMessageAsync(SendMessageRequest request, CancellationToken ct = default) =>
        _hub.InvokeAsync<MessageDto>(ChatHubContract.SendMessage, request, ct);

    public Task MarkReadAsync(Guid conversationId, Guid messageId) =>
        _hub.State == HubConnectionState.Connected ? _hub.SendAsync(ChatHubContract.MarkRead, conversationId, messageId) : Task.CompletedTask;

    public Task TypingAsync(Guid conversationId) =>
        _hub.State == HubConnectionState.Connected ? _hub.SendAsync(ChatHubContract.Typing, conversationId) : Task.CompletedTask;

    public ValueTask DisposeAsync() => _hub.DisposeAsync();

    static Task Raise(Action? handler)
    {
        handler?.Invoke();
        return Task.CompletedTask;
    }

    sealed class ForeverRetryPolicy : IRetryPolicy
    {
        static readonly TimeSpan[] Delays = [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

        public TimeSpan? NextRetryDelay(RetryContext context) =>
            context.PreviousRetryCount < Delays.Length ? Delays[context.PreviousRetryCount] : TimeSpan.FromSeconds(30);
    }
}

/// <summary>
/// The signed-in session: who "me" is, the live connection, and sending. Sends go over SignalR
/// and fall back to HTTP when the connection is down; the client message id makes the fallback
/// safe even if the first attempt did reach the server.
/// </summary>
public sealed class ChatSession
{
    readonly ChatConnection _connection;
    readonly ChatApi _api;
    readonly AuthService _auth;
    CancellationTokenSource _lifetime = new();

    public ChatSession(ChatConnection connection, ChatApi api, AuthService auth)
    {
        (_connection, _api, _auth) = (connection, api, auth);
        _connection.MessageReceived += message => MessageArrived?.Invoke(message);
    }

    public ChatConnection Connection => _connection;

    /// <summary>Known from the stored sign-in, so it's right even when the app starts offline.</summary>
    public Guid MyUserId { get; private set; }

    public MeDto? Me { get; private set; }

    /// <summary>The conversation on screen, so it doesn't count as unread.</summary>
    public Guid? ActiveConversationId { get; set; }

    /// <summary>Every message that arrives or that this device sends -- handle both the same way, deduplicating by id.</summary>
    public event Action<MessageDto>? MessageArrived;

    public event Action<MeDto>? MeChanged;

    public async Task StartAsync()
    {
        MyUserId = await _auth.GetUserIdAsync() ?? Guid.Empty;
        _lifetime = new CancellationTokenSource();
        _ = _connection.StartAsync(_lifetime.Token);
        await RefreshMeAsync(_lifetime.Token);
    }

    public async Task<MeDto> RefreshMeAsync(CancellationToken ct = default)
    {
        Me = await _api.GetMeAsync(ct);
        MeChanged?.Invoke(Me);
        return Me;
    }

    public async Task StopAsync()
    {
        await _lifetime.CancelAsync();
        await _connection.StopAsync();
        (Me, MyUserId, ActiveConversationId) = (null, Guid.Empty, null);
    }

    public async Task<MessageDto> SendAsync(Guid conversationId, string kind, object payload, Guid clientMessageId, CancellationToken ct = default)
    {
        var request = new SendMessageRequest(conversationId, kind, ContractJson.ToElement(payload), clientMessageId);
        MessageDto sent;
        try
        {
            sent = _connection.State == HubConnectionState.Connected
                ? await _connection.SendMessageAsync(request, ct)
                : await _api.SendMessageAsync(request, ct);
        }
        catch (HubException ex)
        {
            throw new ChatSendException(CleanHubError(ex.Message));
        }
        catch (ApiException ex)
        {
            throw new ChatSendException(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not ChatSendException)
        {
            // The connection dropped mid-call. Same request over HTTP: if the first got through, the
            // server hands back that message instead of making a second.
            try
            {
                sent = await _api.SendMessageAsync(request, ct);
            }
            catch (ApiException apiError)
            {
                throw new ChatSendException(apiError.Message);
            }
        }

        MessageArrived?.Invoke(sent);
        return sent;
    }

    /// <summary>SignalR prefixes the server's message with "An unexpected error occurred invoking ... HubException: ".</summary>
    static string CleanHubError(string message)
    {
        const string marker = "HubException: ";
        var at = message.IndexOf(marker, StringComparison.Ordinal);
        return at >= 0 ? message[(at + marker.Length)..] : message;
    }
}

public sealed class ChatSendException(string message) : Exception(message);
