namespace NdeipiChat.App.Platform;

/// <summary>
/// A line in the phone's log (logcat, tag "ndeipi"), to follow navigation on a device: see it with
/// `adb logcat -s ndeipi`. Debug.WriteLine doesn't reach logcat without a debugger attached.
/// Never log tokens, codes or other secrets here.
/// </summary>
public static class AppLog
{
    public static void Info(string message)
    {
#if ANDROID
        Android.Util.Log.Info("ndeipi", message);
#else
        System.Diagnostics.Debug.WriteLine("[ndeipi] " + message);
#endif
    }
}
