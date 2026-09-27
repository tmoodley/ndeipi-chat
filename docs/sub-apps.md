# Building a sub-app

A sub-app is a Razor Class Library the web shell downloads the first time someone opens it. That
covers SRS SR-02 (Razor packaging, loading on first use, `<DynamicComponent>`) and NFR-02-01
(checked before it runs). The Inventory app (`src/NdeipiChat.SubApps.Inventory`) is the working
example.

## How it loads

1. The launcher lists the app from the user's manifest: `Route` `apps/{id}`, `Assembly`, plus a
   `BundleUri` and `Sha256` the API works out from the published file.
2. Opening it goes to `apps/{id}` (`Pages/SubAppHost.razor`). The shell downloads the bundle and
   checks its SHA-256 against the manifest. If they differ, it refuses to load the app.
3. It then lazy-loads the assembly, finds the component marked `[SubAppRoot("{id}")]`, and renders
   it with `<DynamicComponent>` inside an error boundary, so a crash stays inside the app.
4. The loaded app is cached for the rest of the session, so reopening it is instant.

The app gets a `SubAppContext` as a cascading parameter:

- **Who's signed in:** their user id and name.
- **An API client that's already signed in:** the shell adds and refreshes the token; the app never
  sees it.
- **A way back to the launcher.**

A sub-app never shows a sign-in of its own (SR-03-02).

## Adding one

1. **Create the library:** `dotnet new razorclasslib -n NdeipiChat.SubApps.{Name}`. Reference
   `NdeipiChat.SubApps.Sdk`, and `NdeipiChat.Contracts` if it shares DTOs with the API.
2. **Give it a root component** with `@attribute [SubAppRoot("{id}")]` and a
   `[CascadingParameter] public SubAppContext Context { get; set; }`. Use the shell's styles (`bar`,
   `page`, `list`, `row`, `form`, `field` and the button classes) rather than scoped CSS.
3. **Give it an API if it needs one.** Put its endpoints under `/api/{id}` in `NdeipiChat.Api`,
   with `.RequireAuthorization().RequireSubApp("{id}")` so the manifest's roles apply to them too.
4. **Add it to the web app** in `NdeipiChat.Web.csproj`:
   ```xml
   <ProjectReference Include="..\NdeipiChat.SubApps.{Name}\NdeipiChat.SubApps.{Name}.csproj" />
   <BlazorWebAssemblyLazyLoad Include="NdeipiChat.SubApps.{Name}.wasm" />
   <TrimmerRootAssembly Include="NdeipiChat.SubApps.{Name}" />
   ```
   `TrimmerRootAssembly` stops trimming from removing the root component, which the shell only
   finds by reflection.
5. **List it** in `appsettings.json` under `Launcher:Apps`:
   ```json
   "{id}": { "Title": "…", "Icon": "…", "Route": "apps/{id}", "Assembly": "NdeipiChat.SubApps.{Name}", "RequiredRoles": [ "…" ] }
   ```
6. **Publish.** The API finds the bundle and its hash itself. If the bundle is missing from a
   deployment, the app is left out of everyone's manifest rather than failing when opened.

## Limits, for now

- **Adding an app means republishing the site.** Blazor only lazy-loads assemblies the site was
  published with. Existing phone installs don't need an update: the phone app shows loaded
  sub-apps as "update to use it" until step 4.
- **The hash check isn't a signature yet.** It proves the bundle is the one the manifest names, and
  Blazor checks it again against its own boot manifest. Both come from the same server, though.
  Signing manifest entries with an enterprise key the shell trusts (the rest of NFR-02-01) is step 3.
