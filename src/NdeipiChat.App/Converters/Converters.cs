using System.Globalization;

namespace NdeipiChat.App.Converters;

public sealed class NotConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

public sealed class HasTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => !string.IsNullOrWhiteSpace(value as string);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class IsBlankConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => string.IsNullOrWhiteSpace(value as string);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Shows an in-memory photo (e.g. one just taken, or fetched with the API's auth header).</summary>
public sealed class BytesToImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is byte[] { Length: > 0 } bytes ? ImageSource.FromStream(() => new MemoryStream(bytes)) : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class IsNotNullConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not null;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// An app's or person's colour name ("blue", as <see cref="NdeipiChat.Client.ViewModels.AppTones"/>
/// gives it) as the gradient the web draws for tone-blue.
/// </summary>
public sealed class ToneBrushConverter : IValueConverter
{
    static readonly Dictionary<string, (string From, string To)> Tones = new()
    {
        ["blue"] = ("#5B9BFF", "#3D7BF7"),
        ["green"] = ("#7ED957", "#4CB944"),
        ["red"] = ("#FF7A7A", "#E5484D"),
        ["yellow"] = ("#FFD36B", "#F5B324"),
        ["purple"] = ("#B69CFF", "#8B5CF6"),
        ["brown"] = ("#C8A27A", "#9C7652"),
        ["teal"] = ("#5EE0D0", "#14B8A6"),
        ["orange"] = ("#FFA66B", "#F97316"),
        ["pink"] = ("#FF9CC8", "#EC4899")
    };

    public static Brush For(string? tone)
    {
        var (from, to) = Tones.TryGetValue(tone ?? "", out var t) ? t : Tones["purple"];
        return new LinearGradientBrush([new(Color.FromArgb(from), 0), new(Color.FromArgb(to), 1)], new Point(0, 0), new Point(1, 1));
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => For(value as string);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
