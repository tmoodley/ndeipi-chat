using NdeipiChat.App.Controls;
using NdeipiChat.Client.Extensions;
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
    readonly AvatarView _avatar = new() { Size = 40, VerticalOptions = LayoutOptions.Start };
    readonly ActivityIndicator _sending = new() { IsRunning = true, WidthRequest = 16, HeightRequest = 16, VerticalOptions = LayoutOptions.Center };
    readonly Button _retry = new()
    {
        Text = "!",
        TextColor = Colors.White,
        BackgroundColor = Color.FromArgb("#FA5151"),
        CornerRadius = 11,
        WidthRequest = 22,
        HeightRequest = 22,
        Padding = 0,
        FontSize = 14,
        FontAttributes = FontAttributes.Bold,
        VerticalOptions = LayoutOptions.Center
    };
    readonly HorizontalStackLayout _line = new() { Spacing = 8 };
    readonly Grid _row = new() { ColumnDefinitions = [new(40), new(GridLength.Star), new(40)], ColumnSpacing = 8 };

    public MessageRow()
    {
        _time.SetBinding(Label.TextProperty, nameof(MessageViewModel.TimeText));
        _time.SetBinding(IsVisibleProperty, nameof(MessageViewModel.ShowTimestamp));
        _avatar.SetBinding(AvatarView.TextProperty, nameof(MessageViewModel.SenderInitials));
        _avatar.SetBinding(AvatarView.ImageUrlProperty, nameof(MessageViewModel.SenderAvatarUrl));
        _sending.SetBinding(IsVisibleProperty, nameof(MessageViewModel.IsSending));
        _retry.SetBinding(IsVisibleProperty, nameof(MessageViewModel.IsFailed));
        _retry.Clicked += OnRetry;

        _row.Add(_avatar);
        _row.Add(_line, 1);
        Content = new VerticalStackLayout { Padding = new Thickness(12, 4), Children = { _time, _row } };
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
            _line.Add(_retry);
            _line.Add(_sending);
        }
        if (Bubble is not null)
            _line.Add(Bubble);

        // The spinner and "!" keep their IsVisible bindings: setting IsVisible here would clear them.
        Grid.SetColumn(_avatar, mine ? 2 : 0);
        _line.HorizontalOptions = mine ? LayoutOptions.End : LayoutOptions.Start;
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
