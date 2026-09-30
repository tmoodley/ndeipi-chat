# NdeipiChat.SubApps.Sdk

The contract between the Ndeipi shell and a micro-app.

- `[SubAppRoot("your-app-id")]` marks the component the shell opens.
- `SubAppContext`, a cascading parameter, carries the signed-in user, an `HttpClient` for the Ndeipi
  API that already carries their sign-in, a way back to the launcher, realtime topics
  (`ISubAppRealtime`) and on-device keys and values (`ISubAppDevice`).

```razor
@attribute [SubAppRoot("bookings")]

<header class="bar"><h1>Bookings</h1></header>
<div class="page">Hello, @Context.DisplayName</div>

@code {
    [CascadingParameter] public SubAppContext Context { get; set; } = null!;
}
```

Start a new app with the template (`NdeipiChat.SubApps.Templates`) and run it with the dev host
(`NdeipiChat.SubApps.DevHost`). The guide is
[docs/micro-apps.md](https://github.com/tmoodley/ndeipi-chat/blob/main/docs/micro-apps.md).
