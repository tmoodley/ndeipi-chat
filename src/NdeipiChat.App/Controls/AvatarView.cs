using Microsoft.Maui.Controls.Shapes;

namespace NdeipiChat.App.Controls;

/// <summary>WeChat's rounded-square avatar: the picture if there is one, else initials on a colour picked from the name.</summary>
public sealed class AvatarView : ContentView
{
    static readonly Color[] Palette =
    [
        Color.FromArgb("#7EB6E8"), Color.FromArgb("#F2A65A"), Color.FromArgb("#8BC34A"),
        Color.FromArgb("#B39DDB"), Color.FromArgb("#4DB6AC"), Color.FromArgb("#E57373")
    ];

    public static readonly BindableProperty TextProperty = BindableProperty.Create(
        nameof(Text), typeof(string), typeof(AvatarView), "", propertyChanged: (b, _, n) => ((AvatarView)b).SetText(n as string));

    public static readonly BindableProperty ImageUrlProperty = BindableProperty.Create(
        nameof(ImageUrl), typeof(string), typeof(AvatarView), null, propertyChanged: (b, _, n) => ((AvatarView)b).SetImage(n as string));

    public static readonly BindableProperty SizeProperty = BindableProperty.Create(
        nameof(Size), typeof(double), typeof(AvatarView), 40d, propertyChanged: (b, _, n) => ((AvatarView)b).Resize((double)n));

    readonly Label _initials = new()
    {
        TextColor = Colors.White,
        FontAttributes = FontAttributes.Bold,
        HorizontalOptions = LayoutOptions.Center,
        VerticalOptions = LayoutOptions.Center
    };

    readonly Image _picture = new() { Aspect = Aspect.AspectFill, IsVisible = false };
    readonly Border _frame;

    public AvatarView()
    {
        _frame = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 6 },
            BackgroundColor = Palette[0],
            Content = new Grid { Children = { _initials, _picture } }
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

    void SetText(string? text)
    {
        _initials.Text = text;
        _frame.BackgroundColor = Palette[(uint)(text ?? "").GetHashCode(StringComparison.Ordinal) % Palette.Length];
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
        _initials.FontSize = size * 0.38;
    }
}
