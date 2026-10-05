using NdeipiChat.App.Controls;
using NdeipiChat.Client.Extensions;
using NdeipiChat.Client.Realtime;
using NdeipiChat.Client.ViewModels;

namespace NdeipiChat.App.Views;

/// <summary>
/// What every message has in common, whatever its kind: the time divider, the sender's avatar on
/// their side, and for your own messages a spinner while sending or a red "!" to retry. Each
/// message view supplies only its <see cref="Bubble"/>.
/// </summary>
public class MessageRow : ContentView
{
    public static readonly BindableProperty BubbleProperty = BindableProperty.Create(
        nameof(Bubble), typeof(View), typeof(MessageRow), propertyChanged: (b, _, _) => ((MessageRow)b).Arrange());

    readonly Label _time = new() { Style = (Style)Application.Current!.Resources["Timestamp"] };
    readonly AvatarView _avatar = new() { Size = 34, VerticalOptions = LayoutOptions.End };
    readonly ActivityIndicator _sending = new() { IsRunning = true, WidthRequest = 16, HeightRequest = 16, VerticalOptions = LayoutOptions.Center };
    readonly Button _retry = new()
    {
        Text = "!",
        TextColor = Colors.White,
        BackgroundColor = Color.FromArgb("#E5484D"),
        CornerRadius = 11,
        WidthRequest = 22,
        HeightRequest = 22,
        Padding = 0,
        FontSize = 14,
        FontAttributes = FontAttributes.Bold,
        VerticalOptions = LayoutOptions.Center
    };
    /// <summary>Forward a text, gallery or listing to other chats (SRS §3.2): the arrow beside it.</summary>
    readonly Button _forward = new()
    {
        Text = "↪",
        FontSize = 16,
        WidthRequest = 32,
        HeightRequest = 32,
        CornerRadius = 16,
        Padding = 0,
        BackgroundColor = Colors.Transparent,
        BorderWidth = 1,
        BorderColor = Color.FromArgb("#33808080"),
        TextColor = Color.FromArgb("#8A8FA3"),
        VerticalOptions = LayoutOptions.Center
    };
    readonly HorizontalStackLayout _line = new() { Spacing = 8 };
    readonly Grid _row = new() { ColumnDefinitions = [new(34), new(GridLength.Star), new(34)], ColumnSpacing = 8 };

    public MessageRow()
    {
        _time.SetBinding(Label.TextProperty, nameof(MessageViewModel.TimeText));
        _time.SetBinding(IsVisibleProperty, nameof(MessageViewModel.ShowTimestamp));
        _avatar.SetBinding(AvatarView.TextProperty, nameof(MessageViewModel.SenderInitials));
        _avatar.SetBinding(AvatarView.ImageUrlProperty, nameof(MessageViewModel.SenderAvatarUrl));
        _sending.SetBinding(IsVisibleProperty, nameof(MessageViewModel.IsSending));
        _retry.SetBinding(IsVisibleProperty, nameof(MessageViewModel.IsFailed));
        _retry.Clicked += OnRetry;
        _forward.SetBinding(IsVisibleProperty, nameof(MessageViewModel.CanForward));
        _forward.Clicked += OnForward;

        _row.Add(_avatar);
        _row.Add(_line, 1);
        Content = new VerticalStackLayout { Padding = new Thickness(14, 4), Children = { _time, _row } };
    }

    public View? Bubble
    {
        get => (View?)GetValue(BubbleProperty);
        set => SetValue(BubbleProperty, value);
    }

    protected override void OnBindingContextChanged()
    {
        base.OnBindingContextChanged();
        Arrange();
    }

    /// <summary>Theirs: avatar left, bubble after it. Yours: bubble right, avatar after it, status before it.</summary>
    void Arrange()
    {
        var mine = BindingContext is MessageViewModel { IsMine: true };
        _line.Clear();
        if (mine)
        {
            _line.Add(_forward);
            _line.Add(_retry);
            _line.Add(_sending);
        }
        if (Bubble is not null)
            _line.Add(Bubble);
        if (!mine)
            _line.Add(_forward);

        // The spinner and "!" keep their IsVisible bindings: setting IsVisible here would clear them.
        Grid.SetColumn(_avatar, mine ? 2 : 0);
        _line.HorizontalOptions = mine ? LayoutOptions.End : LayoutOptions.Start;
    }

    /// <summary>Picks a chat (other than this one) and forwards the message there.</summary>
    async void OnForward(object? sender, EventArgs e)
    {
        if (BindingContext is not MessageViewModel message)
            return;
        var services = Handler?.MauiContext?.Services ?? IPlatformApplication.Current!.Services;
        var chats = services.GetRequiredService<ChatsViewModel>();
        if (chats.Conversations.Count == 0)
            await chats.RefreshCommand.ExecuteAsync(null);
        var targets = chats.Conversations.Where(c => c.Id != message.Message.ConversationId).Take(30).ToList();
        if (targets.Count == 0)
        {
            await Shell.Current.DisplayAlertAsync("Forward", "There are no other chats to forward to yet.", "OK");
            return;
        }

        // Titles can repeat (two people with one name), so the choice is matched by its position.
        var labels = targets.Select((c, i) => $"{c.Title}{(targets.Take(i).Any(t => t.Title == c.Title) ? $" ({i + 1})" : "")}").ToArray();
        var choice = await Shell.Current.DisplayActionSheetAsync("Forward to", "Cancel", null, labels);
        var index = Array.IndexOf(labels, choice);
        if (index < 0)
            return;
        try
        {
            await services.GetRequiredService<ChatApi>().ForwardAsync(message.Id, [targets[index].Id]);
            await Shell.Current.DisplayAlertAsync("Forwarded", $"Sent to {targets[index].Title}.", "OK");
        }
        catch (ApiException ex)
        {
            await Shell.Current.DisplayAlertAsync("Couldn't forward", ex.Message, "OK");
        }
    }

    async void OnRetry(object? sender, EventArgs e)
    {
        if (BindingContext is not MessageViewModel message)
            return;
        if (!string.IsNullOrEmpty(message.DeliveryError)
            && !await Shell.Current.DisplayAlertAsync("Not sent", message.DeliveryError, "Resend", "Cancel"))
            return;

        for (Element? e2 = Parent; e2 is not null; e2 = e2.Parent)
        {
            if (e2.BindingContext is ChatViewModel chat)
            {
                await chat.RetryCommand.ExecuteAsync(message);
                return;
            }
        }
    }
}
