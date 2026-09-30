using Android.Webkit;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;

namespace NdeipiChat.App.Platforms.Android;

/// <summary>
/// Lets a page of the Ndeipi site in the WebView use the camera (scanning a Ndeipi Pay code, a
/// ticket at the gate), once the person has allowed the app the camera. Any other site, and
/// anything but the camera, is refused.
/// </summary>
public sealed class CameraWebChromeClient(IWebViewHandler handler, Func<Uri?> site) : MauiWebChromeClient(handler)
{
    public override void OnPermissionRequest(PermissionRequest? request)
    {
        if (request is null)
            return;
        var wanted = request.GetResources() ?? [];
        var ours = site() is { } s && Uri.TryCreate(request.Origin?.ToString(), UriKind.Absolute, out var origin)
            && string.Equals(origin.GetLeftPart(UriPartial.Authority), s.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
        if (!ours || wanted.Length == 0 || wanted.Any(r => r != PermissionRequest.ResourceVideoCapture))
        {
            request.Deny();
            return;
        }
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (await Permissions.RequestAsync<Permissions.Camera>() == PermissionStatus.Granted)
                request.Grant([PermissionRequest.ResourceVideoCapture]);
            else
                request.Deny();
        });
    }
}
