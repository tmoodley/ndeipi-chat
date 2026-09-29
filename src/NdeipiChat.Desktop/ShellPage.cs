using NdeipiChat.Client.Auth;

namespace NdeipiChat.Desktop;

/// <summary>
/// The desktop window: a sign-in screen, then the whole web app in a WebView. Sign-in happens in
/// the system browser (so Google, passkeys and saved passwords work as usual); the app then hands
/// its session to the web app with a one-time code, as the phone app does for sub-apps.
/// </summary>
public sealed class ShellPage : ContentPage
{
    static readonly Color Brand = Color.FromArgb("#3D7BF7");

    readonly AuthService _auth;
    readonly WebView _web = new() { IsVisible = false };
    readonly VerticalStackLayout _signIn;
    readonly Label _status = new() { HorizontalTextAlignment = TextAlignment.Center, TextColor = Colors.Gray, FontSize = 14 };
    readonly Button _button = new() { Text = "Sign in", BackgroundColor = Brand, TextColor = Colors.White, CornerRadius = 14, Padding = new Thickness(28, 12), FontAttributes = FontAttributes.Bold };
    readonly Button _cancel = new() { Text = "Cancel", IsVisible = false, BackgroundColor = Colors.Transparent, TextColor = Brand };
    CancellationTokenSource? _signingIn;
    IDispatcherTimer? _watch;

    public ShellPage(AuthService auth)
    {
        _auth = auth;
        BackgroundColor = Color.FromArgb("#EEF1F8");

        _signIn = new VerticalStackLayout
        {
            Spacing = 14,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            WidthRequest = 360,
            Children =
            {
                new Border
                {
                    WidthRequest = 88, HeightRequest = 88, StrokeThickness = 0, BackgroundColor = Brand,
                    StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 26 },
                    HorizontalOptions = LayoutOptions.Center,
                    Content = new Label { Text = "N", FontSize = 44, FontAttributes = FontAttributes.Bold, TextColor = Colors.White, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center }
                },
                new Label { Text = "Ndeipi", FontSize = 30, FontAttributes = FontAttributes.Bold, HorizontalTextAlignment = TextAlignment.Center, TextColor = Color.FromArgb("#16181D") },
                new Label { Text = "Chat, pay, find work and events, and share with your people.", HorizontalTextAlignment = TextAlignment.Center, TextColor = Colors.Gray },
                _button,
                _cancel,
                _status
            }
        };

        _button.Clicked += async (_, _) => await SignInAsync();
        _cancel.Clicked += (_, _) => _signingIn?.Cancel();
        _web.Navigating += OnNavigating;
        Content = new Grid { Children = { _web, _signIn } };
        Loaded += async (_, _) => await StartAsync();
    }

    async Task StartAsync()
    {
        if (await _auth.IsSignedInAsync())
            await OpenSiteAsync();
        else
            ShowSignIn(null);
    }

    async Task SignInAsync()
    {
        _signingIn = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        (_button.IsEnabled, _cancel.IsVisible, _status.Text) = (false, true, "Finish signing in in your browser, then come back here.");
        try
        {
            await _auth.SignInAsync(_signingIn.Token);
            await OpenSiteAsync();
        }
        catch (OperationCanceledException)
        {
            ShowSignIn("Sign-in was cancelled.");
        }
        catch (Exception ex) when (ex is AuthException or HttpRequestException or InvalidOperationException)
        {
            ShowSignIn(ex is HttpRequestException ? "Can't reach Ndeipi. Check your connection." : ex.Message);
        }
        finally
        {
            _cancel.IsVisible = false;
        }
    }

    /// <summary>Opens the web app already signed in, with a fresh one-time code each launch.</summary>
    async Task OpenSiteAsync()
    {
        _status.Text = "Opening Ndeipi…";
        try
        {
            _web.Source = await _auth.CreateWebHandoffAsync("", embedded: false);
            (_web.IsVisible, _signIn.IsVisible) = (true, false);
            WatchForSignOut();
        }
        catch (AuthException ex)
        {
            // The saved sign-in no longer works (revoked, or expired long ago).
            await _auth.SignOutAsync();
            ShowSignIn(ex.Message);
        }
        catch (HttpRequestException)
        {
            ShowSignIn("Can't reach Ndeipi. Check your connection, then try again.");
            _button.Text = "Try again";
        }
    }

    void ShowSignIn(string? message)
    {
        _watch?.Stop();
        (_web.IsVisible, _signIn.IsVisible, _button.IsEnabled, _status.Text) = (false, true, true, message ?? "");
        _button.Text = "Sign in";
    }

    /// <summary>
    /// Signing out in the web app moves it to its sign-in page (inside the page, not a navigation
    /// the WebView reports), so look every couple of seconds; then sign this app out too.
    /// </summary>
    void WatchForSignOut()
    {
        _watch ??= Dispatcher.CreateTimer();
        _watch.Interval = TimeSpan.FromSeconds(2);
        _watch.Tick -= OnWatchTick;
        _watch.Tick += OnWatchTick;
        _watch.Start();
    }

    async void OnWatchTick(object? sender, EventArgs e)
    {
        try
        {
            var path = (await _web.EvaluateJavaScriptAsync("location.pathname"))?.Trim('"') ?? "";
            if (path.TrimEnd('/') == "/signin")
            {
                _watch?.Stop();
                await _auth.SignOutAsync();
                ShowSignIn(null);
            }
        }
        catch (Exception)
        {
            // The page is between loads; look again next tick.
        }
    }

    /// <summary>Other sites (a post's link, a website on a profile) open in the system browser.</summary>
    void OnNavigating(object? sender, WebNavigatingEventArgs e)
    {
        if (!Uri.TryCreate(e.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return;
        if (uri.Host.Equals(MauiProgram.Site.Host, StringComparison.OrdinalIgnoreCase))
            return;
        e.Cancel = true;
        _ = Launcher.Default.OpenAsync(uri);
    }
}
