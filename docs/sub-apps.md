# Building a sub-app

A sub-app is a Razor Class Library the web shell downloads the first time someone opens it. That
covers SRS SR-02 (Razor packaging, loading on first use, `<DynamicComponent>`) and NFR-02-01
(checked before it runs). The Inventory app (`src/NdeipiChat.SubApps.Inventory`) is the working
example.

For the big picture (how the pieces fit, the limits today, and what's needed before separate teams
can ship on their own), see the [micro-app guide](micro-apps.md).

## How it loads

1. The launcher lists the app from the user's manifest: `Route` `apps/{id}`, `Assembly`, a
   `BundleUri` and `Sha256` the API works out from the published file, and the publisher's
   `Signature` and `SigningKeyId` for that exact bundle. See [Signing](#signing).
2. Opening it goes to `apps/{id}` (`Pages/SubAppHost.razor`). The shell downloads the bundle,
   checks its SHA-256 against the manifest, and checks the signature against the publisher keys
   built into the shell (`SubAppVerifier`). If either check fails, the app doesn't load.
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


## What the shell provides

`SubAppContext` carries, besides the user and the signed-in `Api` client:

- **`Realtime`** (`ISubAppRealtime`): live messages on topics such as `events:{id}`, over the
  shell's own SignalR connection. The API decides who may follow a topic. A module registers an
  `ITopicPolicy` for its prefix and publishes with `ChatNotifier.PublishAsync`. The shell
  re-subscribes after a reconnect.
- **`Device`** (`ISubAppDevice`): ECDSA P-256 keys that never leave the device, plus small stored
  values, both private to the sub-app and the user. The web shell uses Web Crypto and
  localStorage.

Both are null in a shell that doesn't offer them, so check before using them.

## Signing

Sub-app bundles are signed by a **publisher key** (SRS NFR-02-01): ECDSA P-256 over the assembly
name and the bundle's exact SHA-256 (`SubAppSigning.Payload`).

- **Where signing happens:** at release time, off the server. Publishing the API
  (`NdeipiChat.Api.csproj`, target `SignSubAppBundles`) runs `tools/NdeipiChat.SubAppSigner` on the
  publish folder, which writes `subapp-signatures.json`. That works for folder, FTP and Web Deploy
  publishes. The private key never goes into the publish output.
- **What the server does:** it passes a signature on in the manifest only while its hash matches
  the bundle being served. It never signs anything, so taking over the server doesn't let anyone
  sign a bundle.
- **What the shell does:** it checks the signature against the keys compiled into it
  (`src/NdeipiChat.Client.Core/PublisherKeys.cs`, shared with the phone app), never against keys
  the server sends. Signatures are required everywhere except Development, which serves bundles built on the fly
  that nothing has signed.

**The key.** The private key is `~/.ndeipi/subapp-publisher.key` on the publishing machine.
`NDEIPI_SUBAPP_KEY` or `-p:SubAppPublisherKey=…` can point elsewhere, such as a CI secret file.
**Back it up somewhere safe, and never commit it.** A Release publish without it fails, rather
than deploying sub-apps the shell would refuse. For an emergency unsigned publish, pass
`-p:AllowUnsignedSubApps=true`; the shell will still refuse the bundles.

- **New key:**
  ```bash
  dotnet run --project tools/NdeipiChat.SubAppSigner -- keygen --out ~/.ndeipi/subapp-publisher.key
  ```
  Add the printed public key to `PublisherKeys.All`.
- **Rotating:** add the new public key, publish, re-sign with the new key, publish again, then
  remove the old key.
- **Signing by hand** (for a folder published some other way):
  ```bash
  dotnet run --project tools/NdeipiChat.SubAppSigner -- sign --site <publish>/wwwroot --key <key file>
  ```

## On the phone

The phone app opens loaded sub-apps as **web micro-frontends**: the web shell's `apps/{id}` page,
in an in-app WebView (`WebSubAppPage`). The app stores don't allow downloading native code, which
rules out loading .NET DLLs at runtime. Google Play's Device and Network Abuse policy forbids it on
Android, and iOS apps can't run code they didn't ship with.

1. **The app checks the bundle itself.** `WebSubAppViewModel` downloads the bundle and runs it
   through `SubAppVerifier`, with the publisher keys compiled into the store-signed app
   (`PublisherKeys`, shared with the web shell). A bundle that fails the check doesn't open.
   Release builds require a signature; debug builds, which may use a local API, don't.
2. **It hands over its sign-in.** `AuthService.CreateWebHandoffAsync` asks the API for a one-time
   code for the phone's own Clerk session, through the existing PKCE code flow. It then opens
   `signin/callback?code=…&state=…&next=apps/{id}&embedded=1#verifier=…`. The verifier is in the
   fragment, which never leaves the device.
3. **The web shell redeems the code** for a WebView session of its own, whose refresh tokens are
   separate from the phone's. It goes straight to the sub-app in **embedded mode**: no navigation,
   and "back" navigates to `embed/close`, which the app catches to close the page.
4. **The WebView stays on the Ndeipi site.** Links elsewhere open in the system browser.

**Guarding the handoff:** the web shell reads a verifier from the fragment only when the user agent
has `NdeipiApp/1`, which the app's WebView adds. In an ordinary browser, a crafted callback link
can't sign someone into the link-maker's account ("login CSRF"): it just shows "Sign-in was
interrupted". Embedded mode never offers sign-out, which would end the phone's Clerk session too.

## Limits, for now

- **Adding an app means republishing the site.** Blazor only lazy-loads assemblies the site was
  published with. Phone installs need no update.
- **Signing guards the bundles, not the web shell.** The web shell is served by the site, so
  someone who controls the site could change the shell. On the phone, the app refuses to open a
  sub-app whose bundle fails its own check, done with keys that ship inside the store-signed app.
  What then runs in the WebView is still web content from the site, confined to the WebView's
  sandbox with no bridge to the phone's native features.
