using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using NdeipiChat.Contracts;

namespace NdeipiChat.Web.Platform;

/// <summary>
/// The web shell inside the phone app's WebView, showing one sub-app (SRS step 4). It has no
/// navigation of its own, and "back" closes the WebView page: the app watches for
/// <see cref="ClosePath"/> and handles it. The mode lasts for the WebView's session.
/// </summary>
public sealed class WebEmbedding(IJSRuntime js, NavigationManager navigation)
{
    public const string ClosePath = MobileAuthContract.EmbedClosePath;
    const string Key = "ndeipi.embedded";

    /// <summary>
    /// In the app's WebView, signing in means asking the app for a fresh handoff (the app watches
    /// for <see cref="MobileAuthContract.EmbedSignInPath"/>): Clerk's page can't run in a WebView.
    /// </summary>
    public static void RequestSignIn(NavigationManager navigation) =>
        navigation.NavigateTo(MobileAuthContract.EmbedSignInPath, forceLoad: true);

    IJSInProcessRuntime Js => (IJSInProcessRuntime)js;

    public bool IsEmbedded => Js.Invoke<string?>("ndeipi.storage.get", "sessionStorage", Key) == "1";

    /// <summary>Whether this page is running in the Ndeipi app's own WebView.</summary>
    public bool IsInApp => Js.Invoke<bool>("ndeipi.isInApp", MobileAuthContract.InAppAgentToken);

    public void Enter() => Js.InvokeVoid("ndeipi.storage.set", "sessionStorage", Key, "1");

    /// <summary>Back to the launcher, or in the app, closes the WebView page.</summary>
    public void Leave()
    {
        if (IsEmbedded)
            navigation.NavigateTo(ClosePath, forceLoad: true);
        else
            navigation.NavigateTo("");
    }
}
