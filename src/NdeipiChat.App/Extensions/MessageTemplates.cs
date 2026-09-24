using NdeipiChat.Client.Extensions;

namespace NdeipiChat.App.Extensions;

/// <summary>Pairs a message view model type with the view that draws it.</summary>
public sealed record MessageTemplate(Type ViewModelType, DataTemplate Template);

public static class MessageTemplateRegistration
{
    public static IServiceCollection AddMessageTemplate<TViewModel, TView>(this IServiceCollection services)
        where TViewModel : MessageViewModel
        where TView : View =>
        services.AddSingleton(new MessageTemplate(typeof(TViewModel), new DataTemplate(typeof(TView))));
}

/// <summary>
/// Picks each message's view by its view model type. Templates are created once and reused --
/// CollectionView on Android recycles by template, so a fresh one per item would defeat it.
/// </summary>
public sealed class MessageTemplateSelector(IEnumerable<MessageTemplate> templates) : DataTemplateSelector
{
    readonly Dictionary<Type, DataTemplate> _templates = templates.ToDictionary(t => t.ViewModelType, t => t.Template);

    protected override DataTemplate OnSelectTemplate(object item, BindableObject container) =>
        _templates.TryGetValue(item.GetType(), out var template)
            ? template
            : _templates[typeof(UnsupportedMessageViewModel)];
}
