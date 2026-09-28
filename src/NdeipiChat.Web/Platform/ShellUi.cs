using Microsoft.JSInterop;
using NdeipiChat.Contracts;

namespace NdeipiChat.Web.Platform;

/// <summary>
/// The shell's own state: whether the apps drawer is open (the dock's More, the home screen's
/// "All apps") and the light/dark theme, which is remembered in the browser.
/// </summary>
public sealed class ShellUi(IJSRuntime js)
{
    public bool IsDrawerOpen { get; private set; }

    /// <summary>The drawer opened straight into editing pinned apps.</summary>
    public bool IsEditingPins { get; set; }

    public bool IsDark { get; private set; }

    public event Action? Changed;

    public void OpenDrawer(bool edit = false)
    {
        (IsDrawerOpen, IsEditingPins) = (true, edit);
        Changed?.Invoke();
    }

    public void CloseDrawer()
    {
        (IsDrawerOpen, IsEditingPins) = (false, false);
        Changed?.Invoke();
    }

    public async Task LoadThemeAsync()
    {
        IsDark = await js.InvokeAsync<bool>("ndeipi.theme.isDark");
        Changed?.Invoke();
    }

    public async Task SetDarkAsync(bool dark)
    {
        await js.InvokeVoidAsync("ndeipi.theme.set", dark ? "dark" : "light");
        IsDark = dark;
        Changed?.Invoke();
    }
}

/// <summary>How apps look in the shell: a colour for each app's icon square.</summary>
public static class AppLook
{
    static readonly string[] Tones = ["tone-blue", "tone-green", "tone-red", "tone-yellow", "tone-purple", "tone-brown", "tone-teal", "tone-orange", "tone-pink"];

    public static string Tone(string appId) => appId switch
    {
        BuiltInApps.Chats => "tone-blue",
        BuiltInApps.Feed => "tone-pink",
        BuiltInApps.Shamwaris => "tone-green",
        BuiltInApps.Wallet => "tone-brown",
        BuiltInApps.Herd => "tone-yellow",
        "events" => "tone-purple",
        "gigs" => "tone-orange",
        "inventory" => "tone-teal",
        _ => Tones[(int)((uint)string.GetHashCode(appId, StringComparison.Ordinal) % Tones.Length)]
    };
}
