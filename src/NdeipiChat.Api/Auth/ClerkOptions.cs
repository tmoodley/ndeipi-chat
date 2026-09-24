namespace Ndeipi.Api.Auth;

public sealed class ClerkOptions
{
    public const string Section = "Clerk";

    /// <summary>
    /// The instance's Frontend API URL, e.g. https://your-app.clerk.accounts.dev -- the issuer of
    /// every session token and the host of its signing keys.
    /// </summary>
    public string Authority { get; set; } = "";

    public string PublishableKey { get; set; } = "";

    /// <summary>Backend API secret key (sk_...). Server-side only; never ship it in the app.</summary>
    public string SecretKey { get; set; } = "";

    public string BackendApiUrl { get; set; } = "https://api.clerk.com/v1/";

    /// <summary>
    /// Origins a token's <c>azp</c> claim may name. Tokens minted by the Backend API carry no
    /// <c>azp</c> and are always accepted; leave empty to skip the check.
    /// </summary>
    public string[] AuthorizedParties { get; set; } = [];

    /// <summary>
    /// Optional Clerk JWT template for the app's access tokens. Plain session tokens last 60
    /// seconds; a template lets you choose a longer lifetime and fewer refreshes.
    /// </summary>
    public string? SessionTokenTemplate { get; set; }

    public string ClerkJsVersion { get; set; } = "5";

    public TimeSpan ProfileRefreshInterval { get; set; } = TimeSpan.FromHours(12);

    public string AuthorityUrl => Authority.TrimEnd('/');
}

public sealed class MobileAuthOptions
{
    public const string Section = "MobileAuth";

    /// <summary>The only places the sign-in page will send a code, e.g. ndeipichat://auth.</summary>
    public string[] RedirectUris { get; set; } = [];

    public TimeSpan CodeLifetime { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(30);
}
