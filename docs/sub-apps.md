# Building a sub-app

A sub-app is a Razor Class Library the web shell downloads the first time someone opens it. That
covers SRS SR-02 (Razor packaging, loading on first use, `<DynamicComponent>`) and NFR-02-01
(checked before it runs). The Inventory app (`src/NdeipiChat.SubApps.Inventory`) is the working
example.

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
  (`src/NdeipiChat.Web/Platform/PublisherKeys.cs`), never against keys the server sends.
  Signatures are required everywhere except Development, which serves bundles built on the fly
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

## Limits, for now

- **Adding an app means republishing the site.** Blazor only lazy-loads assemblies the site was
  published with. Existing phone installs don't need an update: the phone app shows loaded
  sub-apps as "update to use it" until step 4.
- **On the web, signing guards the bundles, not the shell.** The web shell, trusted keys included,
  is itself served by the same site. Signing stops a bundle that was swapped in storage, a cache
  or a CDN from running, but someone who controls the site could change the shell too. On the
  phone app (step 4), the trusted keys ship inside a store-signed app, which closes that gap.
