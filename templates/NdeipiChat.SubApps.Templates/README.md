# NdeipiChat.SubApps.Templates

`dotnet new` templates for Ndeipi micro-apps.

```bash
dotnet new install NdeipiChat.SubApps.Templates
dotnet new ndeipi-subapp -n Bookings
dotnet watch --project Bookings/Bookings.DevHost
```

This creates two projects:

- **`Bookings`:** the app, a Razor Class Library with its root component and a sample list backed by
  the API and live topics.
- **`Bookings.DevHost`:** runs the app in a stand-in shell with a fake user and a mock API.

| Option | What it does |
|---|---|
| `--app-id` | The app's id (defaults to the last part of the name, in lower case) |
| `--title` | The name people see (defaults to the last part of the name) |
| `--in-repo` | Reference the SDK projects in the ndeipi-chat repo instead of packages; use with `-o src` from the repo root |
| `--sdk-version` | The SDK package version to reference |
