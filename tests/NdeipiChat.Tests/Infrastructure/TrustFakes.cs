using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace NdeipiChat.Tests.Infrastructure;

/// <summary>
/// LinkedIn, X and Facebook, as far as linking an account needs: the code a test hands the callback
/// is traded for a token, and the token says whose account it is (<see cref="Accounts"/>).
/// </summary>
public sealed class StubPlatforms : HttpMessageHandler
{
    public sealed record Account(string Id, string Name, string? VerifiedType = null);

    /// <summary>By code: the account a callback with that code links.</summary>
    public ConcurrentDictionary<string, Account> Accounts { get; } = new();

    /// <summary>Tokens the platform no longer accepts (the person revoked access).</summary>
    public ConcurrentDictionary<string, bool> Revoked { get; } = new();

    public ConcurrentQueue<string> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!;
        Requests.Enqueue($"{request.Method} {url.GetLeftPart(UriPartial.Path)}");
        var path = url.AbsolutePath;

        if (path.EndsWith("/accessToken") || path.EndsWith("/oauth2/token") || path.EndsWith("/oauth/access_token"))
        {
            var form = request.Content is null
                ? System.Web.HttpUtility.ParseQueryString(url.Query)
                : System.Web.HttpUtility.ParseQueryString(await request.Content.ReadAsStringAsync(ct));
            var code = form["code"];
            return code is not null && Accounts.ContainsKey(code)
                ? Json(new { access_token = "at-" + code, refresh_token = "rt-" + code, expires_in = 7200 })
                : Json(new { error = "invalid_grant" }, HttpStatusCode.BadRequest);
        }

        var token = request.Headers.Authorization?.Parameter ?? "";
        if (!token.StartsWith("at-") || Revoked.ContainsKey(token) || !Accounts.TryGetValue(token[3..], out var who))
            return Json(new { error = "unauthorized" }, HttpStatusCode.Unauthorized);

        return url.Host switch
        {
            "api.linkedin.com" => Json(new { sub = who.Id, name = who.Name }),
            "api.x.com" => Json(new { data = new { id = who.Id, username = who.Name, verified_type = who.VerifiedType ?? "none" } }),
            _ => Json(new { id = who.Id, name = who.Name })
        };
    }

    static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
}
