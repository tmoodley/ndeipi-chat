namespace NdeipiChat.Contracts;

/// <summary>SignalR route and client-to-server method names, shared by the API and the app.</summary>
public static class ChatHubContract
{
    public const string Path = "/hubs/chat";

    public const string SendMessage = nameof(SendMessage);
    public const string MarkRead = nameof(MarkRead);
    public const string Typing = nameof(Typing);

    /// <summary>Starts receiving <see cref="IChatClient.TopicMessage"/> for a topic, e.g. "events:{id}".</summary>
    public const string Subscribe = nameof(Subscribe);
    public const string Unsubscribe = nameof(Unsubscribe);
}

/// <summary>A message on a realtime topic: sub-apps' live updates, without a hub method each.</summary>
public sealed record TopicMessageDto(string Topic, System.Text.Json.JsonElement Payload);

/// <summary>
/// Server-to-client callbacks. The hub is strongly typed against this interface, and the app
/// subscribes with <c>connection.On(nameof(IChatClient.X), ...)</c>, so a rename breaks the build
/// on both sides instead of silently dropping events.
/// </summary>
public interface IChatClient
{
    Task MessageReceived(MessageDto message);

    /// <summary>A message's mutable state changed -- e.g. a token transfer was confirmed on-chain.</summary>
    Task MessageStateChanged(MessageStateDto state);

    Task ConversationUpdated(ConversationDto conversation);
    Task Typing(TypingDto typing);
    Task ReadReceipt(ReadReceiptDto receipt);
    Task BankingStatusChanged(BankingStatusDto status);

    /// <summary>Someone sent, accepted, declined or cancelled a Shamwari request, or unfriended you.</summary>
    Task ShamwarisChanged();

    /// <summary>To a post's author: its NFT moved on (Processing, Confirmed, Failed).</summary>
    Task PostNftChanged(PostNftDto nft);

    /// <summary>A message on a topic this connection subscribed to.</summary>
    Task TopicMessage(TopicMessageDto message);
}
