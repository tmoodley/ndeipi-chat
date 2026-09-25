using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using NdeipiChat.Client;
using NdeipiChat.Client.Auth;
using NdeipiChat.Tests.Infrastructure;

namespace NdeipiChat.Tests;

/// <summary>The web app's side of the API: its pages, and sign-in by leaving the page and coming back.</summary>
public sealed class WebAppTests(TestApp app) : IClassFixture<TestApp>
{
    sealed class NoBrowser : IBrowserAuthenticator
    {
        public Task<IReadOnlyDictionary<string, string>> AuthenticateAsync(Uri url, Uri callbackUri, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    AuthService NewAuth() => new(
        new HttpClient(app.Server.CreateHandler()) { BaseAddress = app.Server.BaseAddress },
        new InMemoryTokenStore(),
        new NoBrowser(),
        new ClientOptions { ApiBaseUrl = app.Server.BaseAddress, RedirectUri = TestApp.WebRedirectUri },
        TimeProvider.System);

    [Fact]
    public async Task Sign_in_completes_after_the_page_has_been_left_and_reloaded()
    {
        var user = await app.CreateUserAsync("Web Signer");

        var (url, pending) = NewAuth().StartSignIn();
        var query = QueryHelpers.ParseQuery(url.Query);
        Assert.Equal(TestApp.WebRedirectUri, query["redirect_uri"].ToString());
        using (var browser = app.CreateClient())
        using (var page = await browser.GetAsync(url))
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);

        // What the sign-in page does once Clerk has a session.
        using var clerkPage = app.ClientWithToken(TestTokens.Create(user.ClerkId, user.SessionId, azp: TestApp.TrustedOrigin));
        using var completed = await clerkPage.PostAsJsonAsync("api/auth/mobile/complete",
            new { redirectUri = TestApp.WebRedirectUri, codeChallenge = query["code_challenge"].ToString() });
        completed.EnsureSuccessStatusCode();
        var code = (await completed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;

        // The page reloads on the callback: a new AuthService, with the pending sign-in from storage.
        var restored = JsonSerializer.Deserialize<PendingSignIn>(JsonSerializer.Serialize(pending))!;
        var auth = NewAuth();
        await Assert.ThrowsAsync<AuthException>(() => auth.CompleteSignInAsync(restored, new Dictionary<string, string> { ["code"] = code, ["state"] = "not-mine" }));
        await auth.CompleteSignInAsync(restored, new Dictionary<string, string> { ["code"] = code, ["state"] = pending.State });

        Assert.Equal(user.Id, await auth.GetUserIdAsync());
        Assert.NotNull(await auth.GetAccessTokenAsync());
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/chats")]
    [InlineData("/chat/5d7c0a52-0f37-4c43-9a55-1e0c9f0b8f16/send-money")]
    [InlineData("/signin/callback?code=x&state=y")]
    public async Task App_pages_are_served_the_web_app(string path)
    {
        using var client = app.CreateClient();
        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<title>Ndeipi Chat</title>", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/api/nothing-here")]
    [InlineData("/hubs/nothing-here")]
    [InlineData("/auth/nothing-here")]
    public async Task Unknown_API_paths_stay_404(string path)
    {
        using var client = app.CreateClient();
        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_API_still_answers_under_its_own_paths()
    {
        using var client = app.CreateClient();
        Assert.Equal("{\"status\":\"ok\"}", await client.GetStringAsync("/health"));
        using var me = await client.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }
}
