using CommunityToolkit.Mvvm.ComponentModel;
using NdeipiChat.Client.Auth;
using NdeipiChat.Contracts;

namespace NdeipiChat.Client.ViewModels;

/// <summary>
/// A runtime-loaded sub-app on the phone (SRS step 4). It runs as a web micro-frontend in the app's
/// WebView, not as downloaded native code, which the app stores don't allow. Before opening it, the
/// app itself downloads the bundle and checks its hash and publisher signature against the keys
/// compiled into this store-signed app (NFR-02-01). Then it hands its sign-in to the web shell, so
/// there's no second login (SR-03-02).
/// </summary>
public sealed partial class WebSubAppViewModel(
    LauncherViewModel launcher,
    AuthService auth,
    SubAppVerifier verifier,
    IHttpClientFactory httpClients) : ObservableObject, INavigationAware
{
    [ObservableProperty]
    public partial string Title { get; set; } = "";

    /// <summary>The page to show: set once the bundle has passed its checks.</summary>
    [ObservableProperty]
    public partial Uri? Url { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    /// <summary>The site the sub-app lives on; the WebView keeps to it.</summary>
    public Uri? Site { get; private set; }

    IReadOnlyDictionary<string, object> _parameters = new Dictionary<string, object>();
    bool _signedInAgain;

    /// <summary>
    /// The web shell lost its sign-in (it asks with <see cref="MobileAuthContract.EmbedSignInPath"/>):
    /// hand it a fresh one, once. A second request means handoffs aren't working, so it says so.
    /// </summary>
    public async Task SignInAgainAsync()
    {
        if (_signedInAgain)
        {
            ErrorMessage = $"Couldn't sign {Title} in. Go back and open it again.";
            return;
        }
        _signedInAgain = true;
        await OnNavigatedToAsync(_parameters);
    }

    public async Task OnNavigatedToAsync(IReadOnlyDictionary<string, object> parameters)
    {
        _parameters = parameters;
        (Url, ErrorMessage, IsLoading) = (null, null, true);
        try
        {
            var appId = parameters.TryGetValue(Routes.SubAppIdParameter, out var id) ? id?.ToString() : null;
            if (!launcher.IsLoaded)
                await launcher.LoadCommand.ExecuteAsync(null);
            var app = launcher.Apps.FirstOrDefault(a => a.Id == appId)?.App;
            if (app is null)
            {
                ErrorMessage = "This app isn't available to you.";
                return;
            }
            Title = app.Title;
            if (app.BundleUri is null)
            {
                ErrorMessage = $"{app.Title} can't be opened here.";
                return;
            }

            // The same bundle the web shell will load, checked here against this app's own trust.
            var http = httpClients.CreateClient(AuthService.HttpClientName);
            Site = http.BaseAddress;
            byte[] bundle;
            try
            {
                bundle = await http.GetByteArrayAsync(app.BundleUri);
            }
            catch (HttpRequestException)
            {
                ErrorMessage = $"Couldn't download {app.Title}. Check your connection and try again.";
                return;
            }
            if (await verifier.ProblemWithAsync(app, bundle) is { } problem)
            {
                ErrorMessage = problem;
                return;
            }

            // Opened with a query (e.g. a gig started from a chat): keep it, but only on this app's own page.
            var route = parameters.TryGetValue(Routes.SubAppRouteParameter, out var r) && r is string asked
                && asked.StartsWith(app.Route + "?", StringComparison.Ordinal) ? asked : app.Route;
            Url = await auth.CreateWebHandoffAsync(route);
        }
        catch (Exception ex) when (ex is AuthException or HttpRequestException)
        {
            ErrorMessage = ex is AuthException ? ex.Message : "Couldn't reach the server. Check your connection and try again.";
        }
        finally
        {
            IsLoading = false;
        }
    }
}
