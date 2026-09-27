using Microsoft.AspNetCore.WebUtilities;
using NdeipiChat.Client;
using NdeipiChat.Client.Auth;
using NdeipiChat.Client.Livestock;
using NdeipiChat.Client.ViewModels;
using NdeipiChat.Contracts;
using NdeipiChat.Tests.Infrastructure;

namespace NdeipiChat.Tests;

/// <summary>
/// Sub-apps on the phone (SRS step 4): the app checks the bundle itself, then opens the web shell's
/// sub-app page in its WebView, handing over its sign-in so there's no second login.
/// </summary>
public sealed class PhoneSubAppTests(TestApp app) : IClassFixture<TestApp>
{
    /// <summary>What the phone's HttpClientFactory gives: clients bound to the API.</summary>
    sealed class ServerClients(TestApp app) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(app.Server.CreateHandler()) { BaseAddress = app.Server.BaseAddress };
    }

    WebSubAppViewModel Page(ClientHarness phone, bool requireSignature) => new(
        phone.Launcher(new InMemorySettingsStore()),
        phone.Auth,
        new SubAppVerifier(SubAppTrust.Of(requireSignature, PublisherKeys.All), new DotNetP256Signer()),
        new ServerClients(app));

    static Dictionary<string, object> Open(string appId) => new() { [Routes.SubAppIdParameter] = appId };

    [Fact]
    public async Task The_phone_hands_its_sign_in_to_the_web_shell_without_a_second_login()
    {
        var user = await app.CreateUserAsync("Handoff Phone");
        await using var phone = await ClientHarness.SignInAsync(app, user);

        var url = await phone.Auth.CreateWebHandoffAsync("apps/inventory");

        Assert.Equal("/" + MobileAuthContract.WebCallbackPath, url.AbsolutePath);
        var query = QueryHelpers.ParseQuery(url.Query);
        Assert.Equal(("apps/inventory", "1"), (query["next"].ToString(), query["embedded"].ToString()));
        Assert.StartsWith("#verifier=", url.Fragment);

        // What the web shell in the WebView does with it: redeem the code with the verifier from the fragment.
        var callback = new Uri(app.Server.BaseAddress, MobileAuthContract.WebCallbackPath).ToString();
        var webStore = new InMemoryTokenStore();
        var web = new AuthService(new HttpClient(app.Server.CreateHandler()) { BaseAddress = app.Server.BaseAddress },
            webStore, new NoBrowser(), new ClientOptions { ApiBaseUrl = app.Server.BaseAddress, RedirectUri = callback }, TimeProvider.System);
        var pending = new PendingSignIn(url.Fragment["#verifier=".Length..], query["state"]!);
        var result = new Dictionary<string, string> { ["code"] = query["code"]!, ["state"] = query["state"]! };
        await web.CompleteSignInAsync(pending, result);

        Assert.Equal(user.Id, await web.GetUserIdAsync());
        // The WebView gets a sign-in of its own: refreshing it doesn't disturb the phone's.
        Assert.NotEqual((await phone.Auth.GetAccessTokenAsync())!, (await web.GetAccessTokenAsync(forceRefresh: true))!);
        Assert.Equal(user.Id, (await phone.Api.GetMeAsync()).Id);
        Assert.NotNull(await phone.Auth.GetAccessTokenAsync(forceRefresh: true));

        // The code works once.
        var again = new AuthService(new HttpClient(app.Server.CreateHandler()) { BaseAddress = app.Server.BaseAddress },
            new InMemoryTokenStore(), new NoBrowser(), new ClientOptions { ApiBaseUrl = app.Server.BaseAddress, RedirectUri = callback }, TimeProvider.System);
        await Assert.ThrowsAsync<AuthException>(() => again.CompleteSignInAsync(pending, result));
    }

    [Fact]
    public async Task A_sub_app_opens_only_once_its_bundle_passes_the_phones_own_checks()
    {
        var user = await app.CreateUserAsync("Phone Opener");
        await using var phone = await ClientHarness.SignInAsync(app, user);

        // The test API's bundles aren't signed: a release build of the app refuses them.
        var strict = Page(phone, requireSignature: true);
        await strict.OnNavigatedToAsync(Open(InventoryContract.AppId));
        Assert.Null(strict.Url);
        Assert.Contains("isn't signed", strict.ErrorMessage);

        // A development build accepts unsigned bundles, and opens the sub-app signed in.
        var debug = Page(phone, requireSignature: false);
        await debug.OnNavigatedToAsync(Open(InventoryContract.AppId));
        Assert.Null(debug.ErrorMessage);
        Assert.Equal(("Inventory", false), (debug.Title, debug.IsLoading));
        Assert.Equal("/" + MobileAuthContract.WebCallbackPath, debug.Url!.AbsolutePath);
        Assert.Equal("apps/inventory", QueryHelpers.ParseQuery(debug.Url.Query)["next"].ToString());
        Assert.Equal(app.Server.BaseAddress, debug.Site);
    }

    [Fact]
    public async Task Apps_that_arent_in_the_manifest_or_arent_loadable_dont_open()
    {
        var user = await app.CreateUserAsync("Phone Wanderer");
        await using var phone = await ClientHarness.SignInAsync(app, user);

        var unknown = Page(phone, requireSignature: false);
        await unknown.OnNavigatedToAsync(Open("trading"));
        Assert.Equal("This app isn't available to you.", unknown.ErrorMessage);

        var builtIn = Page(phone, requireSignature: false);
        await builtIn.OnNavigatedToAsync(Open(BuiltInApps.Chats));
        Assert.Equal("Chats can't be opened here.", builtIn.ErrorMessage);
        Assert.Null(builtIn.Url);
    }

    sealed class NoBrowser : IBrowserAuthenticator
    {
        public Task<IReadOnlyDictionary<string, string>> AuthenticateAsync(Uri url, Uri callbackUri, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
