namespace NdeipiChat.Contracts;

/// <summary>
/// The Trust Score (SRS "Ndeipi Enterprise Server: CRM Trust Score Engine"): a 0-100 score from the
/// identities someone has linked, weighted by how hard each is to fake (§3.2), plus their record of
/// completed settlements (§3.3). Everyone can see anyone's score; only its owner sees which accounts
/// back it. It's broadcast live on <see cref="Topic"/> whenever it changes (§3.4.4).
/// </summary>
public static class TrustContract
{
    public const string BasePath = "api/trust";

    /// <summary>The page where people link accounts and see what makes up their score.</summary>
    public const string PagePath = "trust";

    /// <summary>"trust:{userId:N}": anyone may follow anyone's score.</summary>
    public static string Topic(Guid userId) => $"trust:{userId:N}";

    /// <summary>§3.2: tier weights, out of 100.</summary>
    public const int Tier1Weight = 50, Tier2Weight = 30, Tier3Weight = 20;

    /// <summary>§3.3: points for completed settlements, one per different counterparty, up to this many.</summary>
    public const int MaxHistoryPoints = 15;

    /// <summary>
    /// Work record: a point for each different client who approved and paid for someone's work (a
    /// completed gig not rated below <see cref="MinWorkStars"/>), up to this many.
    /// </summary>
    public const int MaxWorkPoints = 15;

    public const int MinWorkStars = 3;

    /// <summary>How far back completed work counts.</summary>
    public const int WorkYears = 2;

    /// <summary>§3.3 decay: a linked social account counts fully this long after it was last confirmed…</summary>
    public const int FullCreditDays = 90;

    /// <summary>…then half, until this many days, after which it no longer counts until it's linked again.</summary>
    public const int HalfCreditDays = 180;

    public static string Level(int score) => score switch
    {
        >= 85 => "Highly trusted",
        >= 60 => "Trusted",
        >= 30 => "Building trust",
        _ => "New"
    };
}

/// <summary>The accounts that count toward a score, and the tier each counts in (§3.2).</summary>
public static class TrustPlatforms
{
    /// <summary>Tier 1: Bridge's KYC (photo ID and selfie), from the wallet's verification. Nothing to link.</summary>
    public const string Bridge = "bridge";

    /// <summary>Tier 1: Mpost digital identity and address. Needs Mpost's API and a partner account.</summary>
    public const string Mpost = "mpost";

    public const string LinkedIn = "linkedin";

    /// <summary>X counts in tier 2 with Premium (a paid, verified account), otherwise in tier 3.</summary>
    public const string X = "x";

    public const string Telegram = "telegram";
    public const string Facebook = "facebook";

    /// <summary>Tier 3. Proving a WhatsApp number needs the WhatsApp Business API.</summary>
    public const string WhatsApp = "whatsapp";

    // Tier 3, from the accounts someone signs in to Ndeipi with (Clerk). Nothing to link on the trust page.
    public const string Google = "google";
    public const string Apple = "apple";
    public const string Microsoft = "microsoft";
    public const string GitHub = "github";

    /// <summary>Tier 3: a phone number verified at sign-in.</summary>
    public const string Phone = "phone";

    /// <summary>The ones linked on the trust page, in the order it lists them.</summary>
    public static readonly IReadOnlyList<string> All = [Bridge, Mpost, LinkedIn, X, Telegram, Facebook, WhatsApp];

    /// <summary>The ones that come only from signing in (LinkedIn, Facebook and X can come either way).</summary>
    public static readonly IReadOnlyList<string> SignInOnly = [Google, Apple, Microsoft, GitHub, Phone];

    /// <summary>Clerk's provider for a sign-in account ("oauth_google"), as a platform, or null if it doesn't count.</summary>
    public static string? FromSignInProvider(string provider) => provider switch
    {
        "oauth_google" => Google,
        "oauth_apple" => Apple,
        "oauth_microsoft" => Microsoft,
        "oauth_github" => GitHub,
        "oauth_linkedin" or "oauth_linkedin_oidc" => LinkedIn,
        "oauth_facebook" => Facebook,
        "oauth_x" or "oauth_twitter" => X,
        _ => null
    };

    public static string Label(string platform) => platform switch
    {
        Bridge => "Identity check (Bridge)",
        Mpost => "Mpost",
        LinkedIn => "LinkedIn",
        X => "X",
        Telegram => "Telegram",
        Facebook => "Facebook",
        WhatsApp => "WhatsApp",
        Google => "Google",
        Apple => "Apple",
        Microsoft => "Microsoft",
        GitHub => "GitHub",
        Phone => "Phone number",
        _ => platform
    };

    /// <summary>The tier a platform counts in. <paramref name="premium"/>: X Premium.</summary>
    public static int Tier(string platform, bool premium = false) => platform switch
    {
        Bridge or Mpost => 1,
        LinkedIn => 2,
        X => premium ? 2 : 3,
        _ => 3
    };
}

/// <summary>A tier of a score: how much it gives and which platforms (by name, no handles) earn it.</summary>
public sealed record TrustTierDto(int Tier, int Weight, decimal Earned, IReadOnlyList<string> Platforms);

/// <summary>Anyone's score, as everyone sees it.</summary>
/// <param name="IdentityVerified">Tier 1 is met: their identity has been formally checked.</param>
/// <param name="Settlements">Completed settlements with different counterparties (they earn <see cref="HistoryPoints"/>).</param>
/// <param name="WorkReferences">Different clients who approved and paid for their work (they earn <see cref="WorkPoints"/>).</param>
public sealed record TrustScoreDto(
    Guid UserId,
    int Score,
    string Level,
    bool IdentityVerified,
    IReadOnlyList<TrustTierDto> Tiers,
    int Settlements,
    decimal HistoryPoints,
    int WorkReferences,
    decimal WorkPoints,
    DateTimeOffset UpdatedAt);

/// <summary>One account on the owner's trust page.</summary>
/// <param name="Available">This server can link it (its developer app is set up).</param>
/// <param name="Credit">0-1: how much it counts now; it halves after <see cref="TrustContract.FullCreditDays"/>.</param>
/// <param name="Note">What the person should know, e.g. "Premium: counts in tier 2", or why it can't be linked.</param>
/// <param name="ViaSignIn">It comes from an account they sign in to Ndeipi with: kept up to date by signing in, not linked here.</param>
public sealed record TrustLinkDto(
    string Platform,
    string Label,
    int Tier,
    bool Available,
    bool Linked,
    string? Handle,
    DateTimeOffset? LinkedAt,
    DateTimeOffset? LastConfirmedAt,
    decimal Credit,
    string? Note,
    bool ViaSignIn = false);

/// <summary>The owner's own trust page: their score and every account that could count.</summary>
public sealed record MyTrustDto(TrustScoreDto Score, IReadOnlyList<TrustLinkDto> Links);

/// <summary>Where to send the person to link an OAuth account.</summary>
public sealed record TrustLinkStartDto(string AuthorizeUrl);

/// <summary>What Telegram's login widget hands back, to be checked against the bot's token.</summary>
public sealed record TelegramLoginRequest(long Id, string? FirstName, string? LastName, string? Username, string? PhotoUrl, long AuthDate, string Hash);

/// <summary>The bot the Telegram login widget needs, or null if Telegram isn't set up.</summary>
public sealed record TrustSettingsDto(string? TelegramBot);
