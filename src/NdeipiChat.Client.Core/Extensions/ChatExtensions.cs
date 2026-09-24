using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using NdeipiChat.Contracts;

namespace NdeipiChat.Client.Extensions;

/// <summary>
/// The app half of a chat extension: turns messages of one kind into a view model. The app pairs
/// each view model type with a DataTemplate, so a new kind is a renderer, a view model and a
/// template -- plus a server-side handler for the same kind.
/// </summary>
public interface IMessageRenderer
{
    string Kind { get; }
    MessageViewModel Create(MessageDto message, MessageRenderContext context);
}

/// <summary>An entry in the chat's "+" panel (Transfer, Send money, ...).</summary>
public interface IComposerAction
{
    string Title { get; }

    /// <summary>A short glyph or emoji shown on the tile.</summary>
    string Glyph { get; }

    int Order { get; }

    bool IsAvailable(ConversationDto conversation) => true;

    Task ExecuteAsync(ComposerContext context);
}

public sealed record MessageRenderContext(Guid MyUserId, Func<Guid, UserDto?> FindUser);

public sealed record ComposerContext(ConversationDto Conversation, Guid MyUserId);

public sealed class ChatExtensions(IEnumerable<IMessageRenderer> renderers, IEnumerable<IComposerAction> actions)
{
    readonly Dictionary<string, IMessageRenderer> _renderers = renderers.ToDictionary(r => r.Kind, StringComparer.Ordinal);

    public IReadOnlyList<IComposerAction> Actions { get; } = actions.OrderBy(a => a.Order).ToList();

    /// <summary>A view model for any message; kinds this build doesn't know render as "update the app".</summary>
    public MessageViewModel Create(MessageDto message, MessageRenderContext context)
    {
        MessageViewModel vm;
        try
        {
            vm = _renderers.TryGetValue(message.Kind, out var renderer)
                ? renderer.Create(message, context)
                : new UnsupportedMessageViewModel(message, context);
        }
        catch (Exception ex) when (ex is JsonException or NullReferenceException or InvalidOperationException)
        {
            vm = new UnsupportedMessageViewModel(message, context);
        }

        if (message.State is { } state)
            vm.ApplyState(state);
        return vm;
    }
}

public enum DeliveryStatus
{
    Sending,
    Sent,
    Failed
}

public abstract partial class MessageViewModel : ObservableObject
{
    protected MessageViewModel(MessageDto message, MessageRenderContext context)
    {
        Message = message;
        IsMine = message.SenderId == context.MyUserId;
        var sender = context.FindUser(message.SenderId);
        SenderName = IsMine ? "You" : sender?.DisplayName ?? "Someone";
        SenderAvatarUrl = sender?.AvatarUrl;
        SenderInitials = Display.Initials(sender?.DisplayName);
        Delivery = DeliveryStatus.Sent;
    }

    public MessageDto Message { get; private set; }

    public Guid Id => Message.Id;
    public Guid ClientMessageId => Message.ClientMessageId;
    public string Kind => Message.Kind;
    public DateTimeOffset SentAt => Message.SentAt;
    public string TimeText => SentAt.ToLocalTime().ToString("HH:mm");

    public bool IsMine { get; }
    public string SenderName { get; }
    public string? SenderAvatarUrl { get; }
    public string SenderInitials { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFailed), nameof(IsSending))]
    public partial DeliveryStatus Delivery { get; set; }

    [ObservableProperty]
    public partial string? DeliveryError { get; set; }

    /// <summary>Show a time divider above this message (first message, or a gap of a few minutes).</summary>
    [ObservableProperty]
    public partial bool ShowTimestamp { get; set; }

    public bool IsFailed => Delivery == DeliveryStatus.Failed;
    public bool IsSending => Delivery == DeliveryStatus.Sending;

    /// <summary>One line for the chat list.</summary>
    public abstract string Preview { get; }

    /// <summary>Called with the message's state when it arrives and each time it changes.</summary>
    public virtual void ApplyState(JsonElement state)
    {
    }

    /// <summary>The server's copy of a message this device sent optimistically.</summary>
    public void Confirm(MessageDto message)
    {
        Message = message;
        Delivery = DeliveryStatus.Sent;
        DeliveryError = null;
        OnPropertyChanged(nameof(Id));
        OnPropertyChanged(nameof(SentAt));
        OnPropertyChanged(nameof(TimeText));
        if (message.State is { } state)
            ApplyState(state);
    }
}

public sealed class UnsupportedMessageViewModel(MessageDto message, MessageRenderContext context) : MessageViewModel(message, context)
{
    public string Text => "This message needs a newer version of the app.";
    public override string Preview => "[Message]";
}
