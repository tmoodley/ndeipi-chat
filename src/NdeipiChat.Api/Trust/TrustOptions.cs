namespace NdeipiChat.Api.Trust;

/// <summary>
/// "Trust": the developer apps people link accounts through. A platform with no client id (or, for
/// Telegram, no bot) shows as "not available yet" rather than failing.
/// </summary>
public sealed class TrustOptions
{
    public const string Section = "Trust";

    /// <summary>
    /// Base64 of 32 random bytes, to encrypt the OAuth tokens kept for re-checking linked accounts.
    /// Without it no tokens are kept, and an account counts from when it was linked until it decays.
    /// Keep it in user-secrets or the server's environment, never in the repo.
    /// </summary>
    public string TokenKey { get; set; } = "";

    /// <summary>The site's public address, for the OAuth callback. Empty means the one the request came in on.</summary>
    public string PublicBaseUrl { get; set; } = "";

    public OAuthAppOptions LinkedIn { get; set; } = new();
    public OAuthAppOptions X { get; set; } = new();
    public OAuthAppOptions Facebook { get; set; } = new();
    public TelegramOptions Telegram { get; set; } = new();

    /// <summary>How often linked accounts are re-checked (and decay applied).</summary>
    public TimeSpan RecheckInterval { get; set; } = TimeSpan.FromHours(24);
}

public sealed class OAuthAppOptions
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public bool IsConfigured => ClientId.Length > 0 && ClientSecret.Length > 0;
}

public sealed class TelegramOptions
{
    /// <summary>The bot's username (no @), set as the login widget's domain bot with BotFather's /setdomain.</summary>
    public string BotUsername { get; set; } = "";

    /// <summary>The bot's token: login widget data is signed with it.</summary>
    public string BotToken { get; set; } = "";

    public bool IsConfigured => BotUsername.Length > 0 && BotToken.Length > 0;
}
