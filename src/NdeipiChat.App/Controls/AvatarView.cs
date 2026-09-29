using Microsoft.Maui.Controls.Shapes;
using NdeipiChat.App.Converters;
using NdeipiChat.Client.ViewModels;

namespace NdeipiChat.App.Controls;

/// <summary>
/// A person's picture in a soft square, as on the web: the photo if there is one, else their
/// initials on a gradient picked from the name.
/// </summary>
public sealed class AvatarView : ContentView
{
    public static readonly BindableProperty TextProperty = BindableProperty.Create(
        nameof(Text), typeof(string), typeof(AvatarView), "", propertyChanged: (b, _, n) => ((AvatarView)b).SetText(n as string));

    public static readonly BindableProperty ImageUrlProperty = BindableProperty.Create(
        nameof(ImageUrl), typeof(string), typeof(AvatarView), null, propertyChanged: (b, _, n) => ((AvatarView)b).SetImage(n as string));

    public static readonly BindableProperty SizeProperty = BindableProperty.Create(
        nameof(Size), typeof(double), typeof(AvatarView), 40d, propertyChanged: (b, _, n) => ((AvatarView)b).Resize((double)n));

    /// <summary>Round instead of a soft square (the home screen's contacts).</summary>
    public static readonly BindableProperty IsRoundProperty = BindableProperty.Create(
        nameof(IsRound), typeof(bool), typeof(AvatarView), false, propertyChanged: (b, _, _) => ((AvatarView)b).Resize(((AvatarView)b).Size));

    readonly Label _initials = new()
    {
        TextColor = Colors.White,
        FontAttributes = FontAttributes.Bold,
        HorizontalOptions = LayoutOptions.Center,
        VerticalOptions = LayoutOptions.Center
    };

    readonly Image _picture = new() { Aspect = Aspect.AspectFill, IsVisible = false };
    readonly RoundRectangle _shape = new();
    readonly Border _frame;

    public AvatarView()
    {
        _frame = new Border
        {
            StrokeThickness = 0,
            StrokeShape = _shape,
            Background = ToneBrushConverter.For("blue"),
            Content = new Grid { Children = { _initials, _picture } },
            Shadow = new Shadow { Brush = Colors.Black, Opacity = 0.10f, Radius = 8, Offset = new Point(0, 3) }
        };
        Content = _frame;
        Resize(Size);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string? ImageUrl
    {
        get => (string?)GetValue(ImageUrlProperty);
        set => SetValue(ImageUrlProperty, value);
    }

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public bool IsRound
    {
        get => (bool)GetValue(IsRoundProperty);
        set => SetValue(IsRoundProperty, value);
    }

    void SetText(string? text)
    {
        _initials.Text = text;
        _frame.Background = ToneBrushConverter.For(AppTones.ForName(text));
    }

    void SetImage(string? url)
    {
        var valid = Uri.TryCreate(url, UriKind.Absolute, out var uri);
        _picture.Source = valid ? ImageSource.FromUri(uri!) : null;
        _picture.IsVisible = valid;
    }

    void Resize(double size)
    {
        _frame.WidthRequest = size;
        _frame.HeightRequest = size;
        _shape.CornerRadius = IsRound ? size / 2 : size * 0.32;
        _initials.FontSize = size * 0.38;
    }
}
