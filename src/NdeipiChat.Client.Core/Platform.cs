using NdeipiChat.Contracts;

namespace NdeipiChat.Client;

public sealed class ClientOptions
{
    /// <summary>Base URL of NdeipiChat.Api, e.g. https://chat.example.com/.</summary>
    public required Uri ApiBaseUrl { get; init; }

    /// <summary>Must be listed in the API's MobileAuth:RedirectUris and registered as the app's URL scheme.</summary>
    public string RedirectUri { get; init; } = "ndeipichat://auth";

    /// <summary>Where the app keeps its own files, such as livestock captures waiting to upload.</summary>
    public string DataDirectory { get; init; } = Path.Combine(Path.GetTempPath(), "ndeipi-chat");

    /// <summary>Shown to the operator when listing the devices that can sign registrations.</summary>
    public string DeviceName { get; init; } = "This device";
}

/// <summary>The phone's position, for the spec's gpsTelemetry. Null if location is off or refused.</summary>
public interface ILocationProvider
{
    Task<GpsTelemetry?> GetLocationAsync(CancellationToken ct);
}

/// <summary>Small remembered values, such as the operator's ranch code.</summary>
public interface ISettingsStore
{
    string? Get(string key);
    void Set(string key, string value);
}

public sealed class InMemorySettingsStore : ISettingsStore
{
    readonly Dictionary<string, string> _values = [];

    public string? Get(string key) => _values.GetValueOrDefault(key);

    public void Set(string key, string value) => _values[key] = value;
}

/// <summary>Where the sign-in tokens live -- SecureStorage in the app.</summary>
public interface ITokenStore
{
    Task<TokenResponse?> LoadAsync();
    Task SaveAsync(TokenResponse tokens);
    Task ClearAsync();
}

/// <summary>Opens a URL in the system browser and returns the query of the redirect back to the app.</summary>
public interface IBrowserAuthenticator
{
    Task<IReadOnlyDictionary<string, string>> AuthenticateAsync(Uri url, Uri callbackUri, CancellationToken ct);
}

public interface INavigator
{
    Task GoToAsync(string route, IDictionary<string, object>? parameters = null);
    Task GoBackAsync();
    Task ShowMainAsync();
    Task ShowSignInAsync();
}

public interface IDialogs
{
    Task AlertAsync(string title, string message);
    Task OpenBrowserAsync(Uri url);
}

/// <summary>Runs work on the UI thread. SignalR raises its events on background threads.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

/// <summary>Implemented by view models that take navigation parameters.</summary>
public interface INavigationAware
{
    Task OnNavigatedToAsync(IReadOnlyDictionary<string, object> parameters);
}

/// <summary>Implemented by view models that care whether their page is on screen.</summary>
public interface IActivatable
{
    void Activate();
    void Deactivate();
}

public static class Routes
{
    public const string Chat = "chat";
    public const string AssetTransfer = "asset-transfer";
    public const string BankTransfer = "bank-transfer";
    public const string Wallet = "wallet";
    public const string RegisterCow = "register-cow";
    public const string Cow = "cow";
    public const string ComposePost = "compose-post";

    public const string ConversationIdParameter = "conversationId";
    public const string CowIdParameter = "cowId";
}

public sealed class InMemoryTokenStore : ITokenStore
{
    TokenResponse? _tokens;

    public Task<TokenResponse?> LoadAsync() => Task.FromResult(_tokens);

    public Task SaveAsync(TokenResponse tokens)
    {
        _tokens = tokens;
        return Task.CompletedTask;
    }

    public Task ClearAsync()
    {
        _tokens = null;
        return Task.CompletedTask;
    }
}

public sealed class ImmediateDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

public static class Display
{
    public static string Initials(string? name)
    {
        var parts = (name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant(),
            _ => $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant()
        };
    }

    /// <summary>WeChat-style list time: "14:05", "Yesterday", "Tuesday", then a date.</summary>
    public static string ListTime(DateTimeOffset at, DateTimeOffset now)
    {
        var local = at.ToLocalTime().Date;
        var today = now.ToLocalTime().Date;
        if (local == today)
            return at.ToLocalTime().ToString("HH:mm");
        if (local == today.AddDays(-1))
            return "Yesterday";
        return local > today.AddDays(-7) ? at.ToLocalTime().ToString("dddd") : at.ToLocalTime().ToString("d");
    }

    public static string ShortAddress(string? address) =>
        address is null ? "" : address.Length <= 14 ? address : $"{address[..6]}…{address[^4..]}";
}
