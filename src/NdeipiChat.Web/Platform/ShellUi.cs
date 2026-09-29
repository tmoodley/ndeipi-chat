using Microsoft.JSInterop;
using NdeipiChat.Client.ViewModels;
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
    /// <summary>The same colour the phone app gives it (<see cref="AppTones"/>), as a CSS class.</summary>
    public static string Tone(string appId) => "tone-" + AppTones.For(appId);
}
