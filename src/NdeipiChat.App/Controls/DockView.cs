using Microsoft.Maui.Controls.Shapes;
using NdeipiChat.App.Platform;
using NdeipiChat.Client;
using NdeipiChat.Client.ViewModels;
using NdeipiChat.Contracts;

namespace NdeipiChat.App.Controls;

/// <summary>
/// The web app's floating dock, drawn natively: a frosted pill with two apps, a raised Home in the
/// middle, a third app, and More. Android's own tab bar is hidden, so every main page shows this
/// (App.xaml's WithDock template) and the navigation looks the same everywhere.
/// </summary>
public sealed class DockView : ContentView
{
    /// <summary>As on the web: Chats, Social and Wallet when the user has them, then their pins.</summary>
    static readonly string[] PreferredApps = [BuiltInApps.Chats, BuiltInApps.Feed, BuiltInApps.Wallet];

    readonly Grid _slots = new() { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star), new(GridLength.Star), new(GridLength.Star), new(GridLength.Star)] };
    LauncherViewModel? _launcher;
    INavigator? _navigator;
    readonly Border _home;

    /// <summary>How far the Home button rises above the pill.</summary>
    const double RaisedBy = 24;

    public DockView()
    {
        var pill = new Border
        {
            Style = (Style)Application.Current!.Resources["Glass"],
            StrokeShape = new RoundRectangle { CornerRadius = 26 },
            Padding = new Thickness(6, 8),
            HeightRequest = 66,
            Content = _slots
        };
        // The raised Home sits over the pill, not in it: the pill's rounded border clips what it holds.
        _home = new Border
        {
            WidthRequest = 58,
            HeightRequest = 58,
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 20 },
            Background = (Brush)Application.Current.Resources["WalletGradient"],
            Shadow = (Shadow)Application.Current.Resources["BrandShadow"],
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Start,
            Content = new Label { Text = "🏠", FontSize = 24, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center }
        };
        _home.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(async () => await OpenAsync("Home", () => GoToAsync("//main/home"))) });
        pill.Margin = new Thickness(0, RaisedBy, 0, 0);
        Content = new Grid { Children = { pill, _home } };
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    void OnLoaded(object? sender, EventArgs e)
    {
        var services = Handler?.MauiContext?.Services ?? IPlatformApplication.Current?.Services;
        _launcher ??= services?.GetService<LauncherViewModel>();
        _navigator ??= services?.GetService<INavigator>();
        if (_launcher is not null)
            _launcher.ManifestChanged += Rebuild;
        if (Shell.Current is { } shell)
            shell.Navigated += OnNavigated;
        Rebuild();
    }

    void OnUnloaded(object? sender, EventArgs e)
    {
        if (_launcher is not null)
            _launcher.ManifestChanged -= Rebuild;
        if (Shell.Current is { } shell)
            shell.Navigated -= OnNavigated;
    }

    void OnNavigated(object? sender, ShellNavigatedEventArgs e) => Rebuild();

    /// <summary>Which of the five is showing, from the shell's location ("//main/chats/...").</summary>
    static string CurrentTab()
    {
        var location = Shell.Current?.CurrentState.Location.OriginalString ?? "";
        var parts = location.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 && parts[0] == "main" ? parts[1] : "home";
    }

    void Rebuild() => MainThread.BeginInvokeOnMainThread(() =>
    {
        var apps = _launcher is null
            ? []
            : PreferredApps.Select(id => _launcher.Apps.FirstOrDefault(a => a.Id == id)).OfType<SubAppItemViewModel>()
                .Concat(_launcher.Pinned)
                .DistinctBy(a => a.Id)
                .Take(3)
                .ToList();
        var current = CurrentTab();

        _slots.Clear();
        for (var i = 0; i < 2 && i < apps.Count; i++)
            _slots.Add(AppSlot(apps[i], current), i);
        _slots.Add(HomeSlot(current == "home"), 2);
        if (apps.Count > 2)
            _slots.Add(AppSlot(apps[2], current), 3);
        _slots.Add(Slot("⋯", "More", current == "me", () => GoToAsync("//main/me")), 4);
    });

    View AppSlot(SubAppItemViewModel app, string current) =>
        Slot(app.Icon, app.Id == BuiltInApps.Feed ? "Social" : app.Title, AppShell.TabRouteFor(app.Id) == current,
            () => _navigator?.GoToAsync(Routes.SubApp(app.Id), new Dictionary<string, object> { [Routes.SubAppRouteParameter] = app.Route }) ?? Task.CompletedTask);

    View Slot(string icon, string title, bool active, Func<Task> open)
    {
        var colour = active
            ? (Color)Application.Current!.Resources["Brand"]
            : (Color)Application.Current!.Resources[Application.Current.RequestedTheme == AppTheme.Dark ? "MutedDark" : "MutedLight"];
        var slot = new VerticalStackLayout
        {
            Spacing = 2,
            VerticalOptions = LayoutOptions.Center,
            Children =
            {
                new Label { Text = icon, FontSize = 21, HorizontalOptions = LayoutOptions.Center },
                new Label { Text = title, FontSize = 11, FontAttributes = FontAttributes.Bold, TextColor = colour, HorizontalOptions = LayoutOptions.Center, LineBreakMode = LineBreakMode.TailTruncation }
            }
        };
        slot.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(async () => await OpenAsync(title, open)) });
        return slot;
    }

    /// <summary>Home's place in the pill: just its label, under the raised button that sits over it.</summary>
    View HomeSlot(bool active)
    {
        var slot = new Grid
        {
            Children =
            {
                new Label
                {
                    Text = "Home", FontSize = 11, FontAttributes = FontAttributes.Bold,
                    HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.End,
                    TextColor = (Color)Application.Current!.Resources[active ? "Brand" : Application.Current.RequestedTheme == AppTheme.Dark ? "MutedDark" : "MutedLight"]
                }
            }
        };
        slot.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(async () => await OpenAsync("Home", () => GoToAsync("//main/home"))) });
        return slot;
    }

    static Task GoToAsync(string route) => Shell.Current?.GoToAsync(route) ?? Task.CompletedTask;

    static async Task OpenAsync(string title, Func<Task> open)
    {
        AppLog.Info($"dock {title}");
        try
        {
            await open();
        }
        catch (Exception ex)
        {
            AppLog.Info($"dock {title} failed: {ex.Message}");
        }
    }
}
