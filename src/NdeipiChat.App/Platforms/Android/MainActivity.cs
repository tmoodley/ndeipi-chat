using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Views;

namespace NdeipiChat.App;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    WindowSoftInputMode = SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode
        | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
}

/// <summary>Receives the sign-in page's redirect to ndeipichat://auth and hands it to WebAuthenticator.</summary>
[Activity(NoHistory = true, LaunchMode = LaunchMode.SingleTop, Exported = true)]
[IntentFilter(
    [Intent.ActionView],
    Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataScheme = "ndeipichat",
    DataHost = "auth")]
public class WebAuthenticationCallbackActivity : WebAuthenticatorCallbackActivity
{
}
