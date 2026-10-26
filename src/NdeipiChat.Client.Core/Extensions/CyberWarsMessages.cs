using System.Text.Json;
using NdeipiChat.Contracts;

namespace NdeipiChat.Client.Extensions;

/// <summary>
/// A CyberWars card in a chat: a map ping or raid alert that opens the game at a sector, or a
/// /send-coop answer that opens the chat's transfer screen filled in. Opening it moves nothing.
/// </summary>
public sealed class CyberWarsCardMessageViewModel(MessageDto message, MessageRenderContext context, CyberWarsCardPayload card) : MessageViewModel(message, context)
{
    public CyberWarsCardPayload Card { get; } = card;
    public string Icon => Card.Icon;
    public string Title => Card.Title;
    public string Text => Card.Text;
    public string Action => Card.Action;

    /// <summary>The web shell's path for the card's link.</summary>
    public string Route => Card.Route;

    public override string Preview => $"{Icon} {Title}";
}

public sealed class CyberWarsCardRenderer : IMessageRenderer
{
    public string Kind => MessageKinds.CyberWarsCard;

    public MessageViewModel Create(MessageDto message, MessageRenderContext context) =>
        new CyberWarsCardMessageViewModel(message, context, ContractJson.Read<CyberWarsCardPayload>(message.Payload)
            ?? throw new JsonException("Empty CyberWars card."));
}

/// <summary>Opens a card's link in a native shell: the game in its WebView, or the transfer screen.</summary>
public static class CyberWarsNavigation
{
    public static Task OpenAsync(INavigator navigator, CyberWarsCardMessageViewModel card)
    {
        var route = card.Route;
        var query = route.IndexOf('?') is var i and >= 0 ? route[(i + 1)..] : "";
        if (route.StartsWith($"chat/{card.Message.ConversationId}/transfer", StringComparison.OrdinalIgnoreCase))
        {
            var parameters = new Dictionary<string, object> { [Routes.ConversationIdParameter] = card.Message.ConversationId };
            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
                if (pair.Split('=', 2) is [var key, var value])
                    parameters[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value);
            return navigator.GoToAsync(Routes.AssetTransfer, parameters);
        }
        return navigator.GoToAsync(Routes.SubApp(CyberWarsContract.AppId), new Dictionary<string, object>
        {
            [Routes.SubAppRouteParameter] = route.StartsWith($"apps/{CyberWarsContract.AppId}", StringComparison.Ordinal) ? route : $"apps/{CyberWarsContract.AppId}"
        });
    }
}
