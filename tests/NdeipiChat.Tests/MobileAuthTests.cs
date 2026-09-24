using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NdeipiChat.Client.Auth;
using NdeipiChat.Contracts;
using NdeipiChat.Tests.Infrastructure;

namespace NdeipiChat.Tests;

public sealed class MobileAuthTests(TestApp app) : IClassFixture<TestApp>
{
    /// <summary>What the sign-in page does once Clerk has signed the user in.</summary>
    async Task<string> CompleteSignInAsync(TestUser user, string challenge)
    {
        using var page = app.ClientWithToken(TestTokens.Create(user.ClerkId, user.SessionId, azp: TestApp.TrustedOrigin));
        using var response = await page.PostAsJsonAsync("api/auth/mobile/complete", new { redirectUri = TestApp.RedirectUri, codeChallenge = challenge });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;
    }

    async Task<HttpResponseMessage> RedeemAsync(string code, string verifier) =>
        await app.CreateClient().PostAsJsonAsync("api/auth/mobile/token", new MobileTokenRequest(code, verifier, TestApp.RedirectUri), ContractJson.Options);

    async Task<HttpResponseMessage> RefreshAsync(string refreshToken) =>
        await app.CreateClient().PostAsJsonAsync("api/auth/mobile/refresh", new RefreshTokenRequest(refreshToken), ContractJson.Options);

    async Task<TokenResponse> SignInAsync(TestUser user)
    {
        var verifier = Pkce.NewSecret();
        using var response = await RedeemAsync(await CompleteSignInAsync(user, Pkce.Challenge(verifier)), verifier);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TokenResponse>(ContractJson.Options))!;
    }

    [Fact]
    public async Task A_code_is_spent_by_one_attempt_and_needs_its_verifier()
    {
        var user = await app.CreateUserAsync("Rudo Code");
        var verifier = Pkce.NewSecret();
        var code = await CompleteSignInAsync(user, Pkce.Challenge(verifier));

        using (var wrongVerifier = await RedeemAsync(code, Pkce.NewSecret()))
            Assert.Equal(HttpStatusCode.BadRequest, wrongVerifier.StatusCode);
        using (var spent = await RedeemAsync(code, verifier))
            Assert.Equal(HttpStatusCode.BadRequest, spent.StatusCode);

        var tokens = await SignInAsync(user);
        Assert.Equal(user.Id, tokens.UserId);
        Assert.True(tokens.AccessTokenExpiresAt > DateTimeOffset.UtcNow);
        using var client = app.ClientWithToken(tokens.AccessToken);
        Assert.Equal(user.Id, (await client.GetFromJsonAsync<MeDto>("api/me", ContractJson.Options))!.Id);
    }

    [Fact]
    public async Task Refresh_tokens_rotate_and_a_reused_one_ends_the_sign_in()
    {
        var user = await app.CreateUserAsync("Rudo Rotate");
        var first = await SignInAsync(user);

        using var rotated = await RefreshAsync(first.RefreshToken);
        rotated.EnsureSuccessStatusCode();
        var second = (await rotated.Content.ReadFromJsonAsync<TokenResponse>(ContractJson.Options))!;
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);

        // The first token again: someone has a copy. The whole sign-in ends, at Clerk too.
        using (var reused = await RefreshAsync(first.RefreshToken))
            Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);
        Assert.False(app.Clerk.IsActive(user.SessionId));
        using (var afterTheft = await RefreshAsync(second.RefreshToken))
            Assert.Equal(HttpStatusCode.Unauthorized, afterTheft.StatusCode);
    }

    [Fact]
    public async Task Signing_out_of_Clerk_signs_the_app_out()
    {
        var user = await app.CreateUserAsync("Rudo Signout");
        var tokens = await SignInAsync(user);

        app.Clerk.EndSession(user.SessionId);

        using var refresh = await RefreshAsync(tokens.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task The_sign_in_page_only_serves_registered_apps()
    {
        using var client = app.CreateClient();
        var challenge = Pkce.Challenge(Pkce.NewSecret());

        using (var foreignRedirect = await client.GetAsync($"auth/mobile/sign-in?redirect_uri={Uri.EscapeDataString("https://evil.test/cb")}&code_challenge={challenge}&code_challenge_method=S256&state=s"))
            Assert.Equal(HttpStatusCode.BadRequest, foreignRedirect.StatusCode);
        using (var plainPkce = await client.GetAsync($"auth/mobile/sign-in?redirect_uri={Uri.EscapeDataString(TestApp.RedirectUri)}&code_challenge={challenge}&code_challenge_method=plain&state=s"))
            Assert.Equal(HttpStatusCode.BadRequest, plainPkce.StatusCode);

        const string hostileState = "</script><script>alert(1)</script>";
        using var page = await client.GetAsync($"auth/mobile/sign-in?redirect_uri={Uri.EscapeDataString(TestApp.RedirectUri)}&code_challenge={challenge}&code_challenge_method=S256&state={Uri.EscapeDataString(hostileState)}");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("data-clerk-publishable-key=\"pk_test_publishable\"", html);
        Assert.Contains("https://clerk.test/npm/@clerk/clerk-js@5/dist/clerk.browser.js", html);
        Assert.DoesNotContain(hostileState, html);
    }

    [Fact]
    public async Task Codes_are_only_issued_for_registered_redirects()
    {
        var user = await app.CreateUserAsync("Rudo Redirect");
        using var page = app.ClientWithToken(TestTokens.Create(user.ClerkId, user.SessionId));
        using var response = await page.PostAsJsonAsync("api/auth/mobile/complete",
            new { redirectUri = "evilapp://auth", codeChallenge = Pkce.Challenge(Pkce.NewSecret()) });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
