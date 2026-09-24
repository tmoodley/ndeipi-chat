using System.Text.Json;
using Ndeipi.Api.Data;
using NdeipiChat.Contracts;

namespace Ndeipi.Api.Chat;

/// <summary>
/// A chat extension on the server: owns one message <see cref="Kind"/>, validates what the app
/// sends and does whatever the message means. Register it with
/// <c>services.AddMessageKind&lt;T&gt;()</c>; the app registers a matching renderer. A kind with
/// no handler is rejected, so the server decides what can be sent.
/// </summary>
public interface IMessageKindHandler
{
    string Kind { get; }

    /// <summary>
    /// Validates and normalises the payload. Anything the handler adds to the
    /// <see cref="ChatDbContext"/> is saved in the same transaction as the message, so the two
    /// can't disagree. Throw <see cref="ChatRejectedException"/> to refuse the message.
    /// </summary>
    Task<PreparedMessage> PrepareAsync(MessageContext context, JsonElement payload, CancellationToken ct);

    /// <summary>
    /// Runs once the message is stored and delivered -- the place for calls to outside services.
    /// Failures are logged, not returned to the sender: report them through the message's state.
    /// </summary>
    Task AfterSendAsync(MessageContext context, ChatMessage message, CancellationToken ct) => Task.CompletedTask;
}

/// <param name="MessageId">The id the message will be stored under, for rows that point back at it.</param>
public sealed record MessageContext(User Sender, Conversation Conversation, IReadOnlyList<User> Members, Guid MessageId)
{
    public User? Member(Guid userId) => Members.FirstOrDefault(m => m.Id == userId);
}

/// <param name="Payload">What gets stored and delivered -- normalised, not necessarily what was sent.</param>
/// <param name="InitialState">The message's first <see cref="MessageDto.State"/>, if it has one.</param>
public sealed record PreparedMessage(JsonElement Payload, object? InitialState = null);

/// <summary>A message or request the server refuses; the text is shown to the user.</summary>
public sealed class ChatRejectedException(string message) : Exception(message);

public static class MessagePayload
{
    public static T Read<T>(JsonElement payload) where T : class
    {
        try
        {
            return ContractJson.Read<T>(payload) ?? throw new ChatRejectedException("The message is empty.");
        }
        catch (JsonException)
        {
            throw new ChatRejectedException("The message payload is malformed.");
        }
    }

    public static string? Memo(string? memo)
    {
        memo = memo?.Trim();
        if (memo is { Length: > 280 })
            throw new ChatRejectedException("Keep the note under 280 characters.");
        return string.IsNullOrEmpty(memo) ? null : memo;
    }

    /// <summary>The other party of a transfer: someone else in this conversation.</summary>
    public static User Recipient(MessageContext context, Guid recipientId)
    {
        var recipient = context.Member(recipientId) ?? throw new ChatRejectedException("The recipient isn't in this conversation.");
        if (recipient.Id == context.Sender.Id)
            throw new ChatRejectedException("You can't send to yourself.");
        return recipient;
    }
}

public sealed class TextMessageHandler : IMessageKindHandler
{
    public const int MaxLength = 4000;

    public string Kind => MessageKinds.Text;

    public Task<PreparedMessage> PrepareAsync(MessageContext context, JsonElement payload, CancellationToken ct)
    {
        var text = MessagePayload.Read<TextPayload>(payload).Text?.Trim();
        if (string.IsNullOrEmpty(text))
            throw new ChatRejectedException("The message is empty.");
        if (text.Length > MaxLength)
            throw new ChatRejectedException($"Messages are limited to {MaxLength} characters.");

        return Task.FromResult(new PreparedMessage(ContractJson.ToElement(new TextPayload(text))));
    }
}
