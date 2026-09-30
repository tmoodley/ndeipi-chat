# Sample Title

A Ndeipi micro-app, with id `sample-id`.

- **`SubAppTemplate/`** is the app: a Razor Class Library. Its root component is `SampleNameApp.razor`,
  marked `[SubAppRoot("sample-id")]`.
- **`SubAppTemplate.DevHost/`** runs the app on its own, with a fake user, a mock API and live topics.
  It's for development only.

## Run it

```bash
dotnet watch --project SubAppTemplate.DevHost
```

The dev host's bar has these controls:

- **Width:** phone, tablet or desktop.
- **Phone app:** shows the app as the phone app does, with no shell around it.
- **Theme.**
- **Signed in as:** switch between people.
- **Opened with ?:** the query string the app gets.

The panels show every API call, the topics the app follows (you can send it messages there), and
any crash.

## Build it out

1. Change the app in `SubAppTemplate/`. Use the shell's classes (`bar`, `page`, `list`, `row`, `form`,
   `primary`, `empty`, `card`) and its CSS variables, and prefix your own classes with `sample-id-`.
2. Mock each API route the app calls in `SubAppTemplate.DevHost/Program.cs`. Keep the mocks close to
   what the real API will do, errors included (`DevResults.BadRequest(...)`, `Forbidden(...)`).
3. Keep the shared types in `SampleNameContract.cs`.

## Bring it into Ndeipi

The Ndeipi team adds it to the platform:

- It moves to `src/` in ndeipi-chat.
- The contract moves to `NdeipiChat.Contracts`.
- The mocks become an API module under `api/sample-id`, with `RequireSubApp("sample-id")`.
- It's added to the web shell and the launcher.

The checklist is in the guide, docs/micro-apps.md in the ndeipi-chat repo.
