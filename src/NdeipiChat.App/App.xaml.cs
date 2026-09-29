using NdeipiChat.Client.ViewModels;

namespace NdeipiChat.App;

public partial class App : Application
{
    readonly AppShell _shell;
    readonly AppCoordinator _coordinator;

    public App(AppShell shell, AppCoordinator coordinator)
    {
        InitializeComponent();
        UserAppTheme = Preferences.Default.Get(ThemeKey, "") switch
        {
            "dark" => AppTheme.Dark,
            "light" => AppTheme.Light,
            _ => AppTheme.Unspecified
        };
        _shell = shell;
        _coordinator = coordinator;
        ColorTabBar(shell);

        // Livestock captures taken out of signal upload the moment the phone is back online.
        Connectivity.Current.ConnectivityChanged += async (_, e) =>
        {
            if (e.NetworkAccess == NetworkAccess.Internet)
                await _coordinator.SendWaitingCapturesAsync();
        };
    }

    /// <summary>
    /// The tab bar's colours, set on the shell (Android reads them there). Done here, not in
    /// AppShell.xaml: the shell is created before this App, so its XAML can't see App.xaml's colours.
    /// </summary>
    void ColorTabBar(Shell shell)
    {
        Color Get(string key) => (Color)Resources[key];
        shell.SetAppThemeColor(Shell.TabBarBackgroundColorProperty, Get("TabBarLight"), Get("TabBarDark"));
        shell.SetAppThemeColor(Shell.TabBarUnselectedColorProperty, Get("MutedLight"), Get("MutedDark"));
        Shell.SetTabBarForegroundColor(shell, Get("Brand"));
        Shell.SetTabBarTitleColor(shell, Get("Brand"));
    }

    const string ThemeKey = "ndeipi.theme";

    /// <summary>Light or dark, remembered on this phone; until chosen, the phone's own setting.</summary>
    public static void SetTheme(AppTheme theme)
    {
        Current!.UserAppTheme = theme;
        Preferences.Default.Set(ThemeKey, theme == AppTheme.Dark ? "dark" : "light");
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(_shell);
        window.Created += async (_, _) => await _coordinator.StartAsync();
        return window;
    }
}
