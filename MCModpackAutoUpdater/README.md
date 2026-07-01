# MCModpackAutoUpdater

`MCModpackAutoUpdater` is a standalone .NET app for Minecraft modpack update orchestration that runs through the shared `MCAgent` command handlers.

It owns its local SQLite database for users, modpack profiles, agent nodes, persistent command queue entries, and update audit history. It can execute commands locally through an embedded agent worker or queue them for remote `MCAgent` machines through the runner API.

The app also hosts a local web UI with ASP.NET Core Identity:

- first-run setup with a one-time token printed to the console/log
- local SQLite users and roles
- cookie login
- roles: `Admin`, `Operator`, `Viewer`
- dashboard with profile state, persisted dry-run checks, summary cards, check-and-queue, manual force-sync queueing, and profile enable toggles
- admin settings page for live runtime settings, AMP credentials, Discord announcements, and SQLite-backed modpack profile CRUD
- agent management for local/remote execution targets
- command history for queued/completed work, filtering, JSON payload/result inspection, retry, and AMP debug commands

## Run

```powershell
dotnet run --project MCModpackAutoUpdater/MCModpackAutoUpdater.csproj
```

Open the configured web UI URL, default `http://localhost:9090`. On first run, check the console/AMP log for:

```text
MCModpackAutoUpdater has no users. Open /setup and use first-run setup token: ...
```

Create the first admin at `/setup`. After that:

- use `/settings` to configure AMP credentials and modpack profiles
- use `/agents` to add remote machines or rotate agent tokens
- use `/history` to inspect or cancel queued commands
- use `/users` to create additional accounts
- use `/` for dashboard status and manual update actions

## Publish

```powershell
dotnet publish MCModpackAutoUpdater/MCModpackAutoUpdater.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o out/mc-modpack-auto-updater-linux-x64
```

## Configuration

The runner reads:

- `appsettings.json`
- `appsettings.{Environment}.json`
- environment variables prefixed with `MC_UPDATER__`
- environment variables prefixed with `MC_AGENT__` for reused sync-handler settings

Modpack profiles are created in the web UI. There is no appsettings or AMP example-profile import path.

AMP should only configure startup/deployment settings that must exist before the app runs:

- web bind port
- SQLite database path
- release repository and release asset names used by the AMP update template

Operational settings such as scheduler behavior, AMP credentials, Discord settings, agents, and modpack profiles are stored in SQLite and managed from the web UI.

Important fields:

- `WebUi:BindUrl`: web UI bind URL, default `http://0.0.0.0:9090`.
- `WebUi:DatabasePath`: local SQLite database used for UI users, roles, agents, profiles, commands, and audits.
- `Provider`: `CurseForge`, `FTB`, or direct/custom URL mode with `ServerPackUrl`.
- `SourceReference`: CurseForge project ID/URL or FTB pack ID/URL.
- `CurrentVersion`: installed file/version ID. After queued syncs complete, SQLite profile state is updated from the agent result.
- `ScheduleTime`: local time in `HH:mm` for the live runtime `ScheduleTimeZone` setting.
- `InstallRootPath`: absolute path to the Minecraft server files.
- `RestartMode`: use `amp` for AMP API orchestration, `none` to only apply files, or a configured shell restart mode.
- ADS controller and direct AMP API credentials are configured in `/settings`.
- Discord announcements are configured in `/settings`; profile channel/role IDs are stored per profile.

`ServerPackExcludedPathsText`, `ServerPackExcludedCurseForgeProjectIdsText`, and `PreservedPathsText` are semicolon/newline text fields saved from the web UI.

## AMP Template

The `amp-template` folder contains a draft Generic Module template. It is suitable as a starting point for a private AMP configuration repository. CubeCoders currently documents that public AMPTemplates submissions must not be AI-generated, so treat this as a local/shareable draft unless you rewrite and validate it manually.

The AMP template exposes `WebUIPort` and passes it to the app as:

```text
MC_UPDATER__WebUi__BindUrl=http://0.0.0.0:{WebUIPort}
```
