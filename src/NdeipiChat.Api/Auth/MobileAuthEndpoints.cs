using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Auth;

public static class MobileAuthEndpoints
{
    public const string CompletePath = "/api/auth/mobile/complete";

    public static void MapMobileAuth(this IEndpointRouteBuilder app)
    {
        app.MapGet(MobileAuthContract.SignInPath, SignInPage);
        app.MapPost(CompletePath, Complete).RequireAuthorization();
        app.MapPost(MobileAuthContract.TokenPath, async (MobileTokenRequest request, MobileAuthService auth, CancellationToken ct) =>
            await auth.RedeemCodeAsync(request, ct) is { } tokens ? Results.Ok(tokens) : Results.BadRequest(new { error = "invalid_grant" }));
        app.MapPost(MobileAuthContract.RefreshPath, async (RefreshTokenRequest request, MobileAuthService auth, CancellationToken ct) =>
            await auth.RefreshAsync(request.RefreshToken, ct) is { } tokens ? Results.Ok(tokens) : Results.Unauthorized());
        app.MapPost(MobileAuthContract.SignOutPath, async (RefreshTokenRequest request, MobileAuthService auth, CancellationToken ct) =>
        {
            await auth.SignOutAsync(request.RefreshToken, ct);
            return Results.NoContent();
        });
    }

    static IResult SignInPage(
        [FromQuery(Name = "redirect_uri")] string? redirectUri,
        [FromQuery(Name = "code_challenge")] string? codeChallenge,
        [FromQuery(Name = "code_challenge_method")] string? codeChallengeMethod,
        [FromQuery(Name = "state")] string? state,
        MobileAuthService auth,
        IOptions<ClerkOptions> clerk)
    {
        if (!auth.IsAllowedRedirect(redirectUri))
            return Results.BadRequest("Unknown redirect_uri.");
        if (codeChallengeMethod != "S256" || !MobileAuthService.IsValidChallenge(codeChallenge))
            return Results.BadRequest("An S256 code_challenge is required.");
        if (string.IsNullOrEmpty(state) || state.Length > 200)
            return Results.BadRequest("A state value is required.");

        return Results.Content(RenderSignInPage(clerk.Value, redirectUri!, codeChallenge!, state), "text/html; charset=utf-8");
    }

    static async Task<IResult> Complete(
        CompleteSignInRequest request,
        ClaimsPrincipal principal,
        CurrentUserService users,
        MobileAuthService auth,
        CancellationToken ct)
    {
        if (!auth.IsAllowedRedirect(request.RedirectUri) || !MobileAuthService.IsValidChallenge(request.CodeChallenge))
            return Results.BadRequest("Unknown redirect_uri or invalid code_challenge.");

        var sessionId = principal.FindFirst(ClerkAuthentication.SessionIdClaim)?.Value;
        if (sessionId is null)
            return Results.BadRequest("The token doesn't belong to a Clerk session.");

        var user = await users.GetAsync(principal, ct);
        var code = await auth.IssueCodeAsync(user, sessionId, request.RedirectUri, request.CodeChallenge, ct);
        return Results.Ok(new CompleteSignInResponse(code));
    }

    public sealed record CompleteSignInRequest(string RedirectUri, string CodeChallenge);

    public sealed record CompleteSignInResponse(string Code);

    /// <summary>
    /// Clerk's hosted sign-in, mounted with clerk-js. Once Clerk has a session the page trades a
    /// session token for a one-time code and hands it back to the app through its redirect URI.
    /// </summary>
    static string RenderSignInPage(ClerkOptions clerk, string redirectUri, string codeChallenge, string state)
    {
        // The default encoder escapes < > & ' so the JSON can't close the script element.
        var config = JsonSerializer.Serialize(new { redirectUri, codeChallenge, state, completePath = CompletePath });
        var html = HtmlEncoder.Default;
        var scriptUrl = $"{clerk.AuthorityUrl}/npm/@clerk/clerk-js@{clerk.ClerkJsVersion}/dist/clerk.browser.js";

        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>Sign in</title>
              <style>
                body { margin: 0; min-height: 100vh; display: flex; flex-direction: column; align-items: center; justify-content: center; background: #ededed; font-family: system-ui, -apple-system, "Segoe UI", sans-serif; }
                #status { color: #555; text-align: center; padding: 24px; }
              </style>
            </head>
            <body>
              <div id="sign-in"></div>
              <p id="status">Loading…</p>
              <script id="config" type="application/json">{{config}}</script>
              <script>
                const config = JSON.parse(document.getElementById('config').textContent);
                const status = document.getElementById('status');

                async function handBackToApp() {
                  status.textContent = 'Signing you in…';
                  const token = await window.Clerk.session.getToken();
                  const response = await fetch(config.completePath, {
                    method: 'POST',
                    headers: { 'Authorization': 'Bearer ' + token, 'Content-Type': 'application/json' },
                    body: JSON.stringify({ redirectUri: config.redirectUri, codeChallenge: config.codeChallenge })
                  });
                  if (!response.ok) {
                    status.textContent = 'Sign-in failed (' + response.status + '). Close this window and try again.';
                    return;
                  }
                  const { code } = await response.json();
                  window.location.replace(config.redirectUri + '?code=' + encodeURIComponent(code) + '&state=' + encodeURIComponent(config.state));
                  status.textContent = 'Signed in. You can return to the app.';
                }

                window.addEventListener('load', async () => {
                  try {
                    await window.Clerk.load();
                    if (window.Clerk.user) {
                      await handBackToApp();
                      return;
                    }
                    status.textContent = '';
                    // After signing in or up, Clerk reloads this page -- which then finds the session above.
                    window.Clerk.mountSignIn(document.getElementById('sign-in'), {
                      forceRedirectUrl: window.location.href,
                      signUpForceRedirectUrl: window.location.href
                    });
                  } catch (e) {
                    status.textContent = 'Could not load sign-in: ' + e.message;
                  }
                });
              </script>
              <script async crossorigin="anonymous" data-clerk-publishable-key="{{html.Encode(clerk.PublishableKey)}}" src="{{html.Encode(scriptUrl)}}" type="text/javascript"></script>
            </body>
            </html>
            """;
    }
}
