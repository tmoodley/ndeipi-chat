# NdeipiChat.SubApps.DevHost

Runs a Ndeipi micro-app on its own, with no sign-in, database or Ndeipi API. It stands in for the
shell:

- **A fake user** you can switch between.
- **A mock API**: you answer the app's calls in `Program.cs`.
- **In-memory realtime topics**: publish to them from a mock or from the Realtime panel.
- **On-device keys and values**, done the same way as the web shell.

A bar lets you view the app at phone, tablet or desktop width, as the phone app shows it, and in
light or dark. Panels show every API call, the topics the app follows, and any crash.

```csharp
var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.AddSubAppDevHost<BookingsApp>(dev =>
{
    var bookings = new List<BookingDto>();
    dev.Api.MapGet("api/bookings", _ => bookings);
    dev.Api.MapPost("api/bookings", async request =>
    {
        var booking = new BookingDto(Guid.NewGuid(), request.ReadJson<AddBooking>()!.Title, request.User.DisplayName);
        bookings.Add(booking);
        await request.Realtime.PublishAsync("bookings:all", booking);
        return DevResults.Created(booking);
    });
});
await builder.Build().RunAsync();
```

The host project's `wwwroot/index.html` loads the shell's styles and the dev host script:

```html
<link rel="stylesheet" href="_content/NdeipiChat.SubApps.DevHost/ndeipi-shell.css" />
<link rel="stylesheet" href="_content/NdeipiChat.SubApps.DevHost/devhost.css" />
...
<div id="app"></div>
<script src="_framework/blazor.webassembly.js" autostart="false"></script>
<script src="_content/NdeipiChat.SubApps.DevHost/devhost.js"></script>
```

The `ndeipi-subapp` template (`NdeipiChat.SubApps.Templates`) sets all of this up.
