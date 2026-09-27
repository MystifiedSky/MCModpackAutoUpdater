# MCModpackAutoUpdater

`MCModpackAutoUpdater` is the standalone control app for scheduling and managing Minecraft modpack updates. It hosts the authenticated web UI, stores users/profiles/agents/commands in SQLite, and includes a local worker. A separate [MCAgent](../MCAgent/README.md) can apply updates on another machine.

The web UI supports first-run admin setup, Admin/Operator/Viewer roles, profile and agent management, update checks and queues, command history, AMP configuration, and Discord announcements.

## Run from Source

From the repository root with the .NET 10 SDK installed:

```powershell
$env:MC_UPDATER__WebUi__BindUrl = "http://127.0.0.1:9090"
dotnet run --project .\MCModpackAutoUpdater\MCModpackAutoUpdater.csproj
```

`dotnet run` restores and builds when needed. The local bind override above keeps this development instance reachable only from the same machine. The default app configuration listens on all network interfaces; set an appropriate bind address and network protection before exposing it.

Open `http://127.0.0.1:9090/setup`. That first page request generates and logs the one-time setup token. Copy it from the terminal, create the first admin account, and sign in. Once any account exists, `/setup` redirects to `/login`.

The app creates the SQLite database and built-in `Local Runner` automatically. Assign a profile to `Local Runner` when the web app process can access the Minecraft server directory. For a step-by-step first profile, follow [the root quick start](../README.md#create-a-first-test-profile).

## Publish

The GitHub Actions workflow creates self-contained Linux and Windows web app packages. These include the .NET runtime, so a separate runtime installation is not required. For a framework-dependent publish, use:

```powershell
dotnet publish .\MCModpackAutoUpdater\MCModpackAutoUpdater.csproj -c Release -o .\publish\web
Set-Location .\publish\web
dotnet .\MCModpackAutoUpdater.dll
```

Run from the publish directory so configuration loads from the publish output and relative database/key paths resolve there. The framework-dependent package requires the .NET 10 ASP.NET Core Runtime. See the root README for release asset names and AMP installation.

## Configuration

The runner reads, in order:

- `appsettings.json`
- `appsettings.{Environment}.json`
- optional private `appsettings.Local.json`
- environment variables prefixed with `MC_UPDATER__`
- environment variables prefixed with `MC_AGENT__` for embedded agent settings

For example, `MC_UPDATER__WebUi__BindUrl` overrides `WebUi:BindUrl`. The prefix is removed before configuration binding. Embedded agent settings such as `MC_AGENT__ModpackSync__AmpStateTimeoutSeconds` can be set the same way. `appsettings.Local.json` is excluded from publish output. Put a separately managed private copy beside the deployed app, or configure production values through environment variables.

Default startup settings are in [appsettings.json](appsettings.json). Important values include:

- `WebUi:BindUrl`: defaults to `http://0.0.0.0:9090`.
- `WebUi:DatabasePath`: SQLite database for UI accounts, roles, agents, profiles, commands, and audit history. Relative paths resolve from the process's current working directory.
- `WebUi:DataProtectionKeyPath`: durable key directory; defaults to `data-protection-keys` beside the database. Back up the key directory with the database to retain access to encrypted credentials.

Operational settings such as scheduling, AMP credentials, Discord, agents, and modpack profiles are managed in the web UI and stored in SQLite. Profiles are not imported from appsettings or an AMP template.

Run one web runner per database. The app holds a `.lock` file beside the database and refuses a second runner using the same path. The lock is released when the process exits; the file itself may remain and should not be removed while the runner is running.

## Profile Notes

Profiles are created in `/settings`. Choose an agent that can access the target install directory and use an absolute path as seen by that agent.

- `Provider`: `CurseForge`, `FTB`, or `Direct`.
- `SourceReference`: CurseForge project ID or FTB pack ID. IDs and URLs containing a numeric path segment are supported; human-readable project slugs are not resolved.
- `ServerPackUrl`: used directly in `Direct` mode. A valid CurseForge or FTB source takes precedence; the URL is a fallback when the source does not identify a provider project or pack.
- `VersionLock`: pins a provider version. For a direct URL, the resolver scans all path segments from right to left and uses the rightmost positive numeric segment before this field. Set this to a version label only when the URL path has no positive numeric segment; a URL such as `/2026/serverpack.zip` resolves to `2026` even if a lock is set. Direct mode does not inspect ZIP contents or detect changed bytes at the same URL.
- `CurrentVersion`: last version ID reported after a completed sync. The updater compares version IDs for equality; it does not semantically order arbitrary labels.
- `ScheduleTime`: daily local time in `HH:mm` using the configured `ScheduleTimeZone`.
- `RestartMode`: `amp` for AMP orchestration, `none` to apply files without stopping or starting the game process, or a configured shell hook.
- AMP controller/direct API settings are managed in `/settings`; Discord settings and per-profile channel/role IDs are there too.

`ServerPackExcludedPathsText`, `ServerPackExcludedCurseForgeProjectIdsText`, and `PreservedPathsText` are semicolon/newline text fields saved from the web UI. CurseForge profiles can be built from client files when a project has no server pack. FTB packs are materialized with the official FTB server installer, which intentionally skips Java and modloader installation; outside AMP, install and configure those separately.

## AMP Template

The `amp-template` folder contains an AMP Generic Module template. The template settings include `Release Repository`, Linux/Windows release asset names, and `Web UI Database Path`. The listener uses the **Web UI** port assigned through AMP's instance port configuration; there is no separate application port setting.

For AMP repository setup and template maintenance, see [the AMP template guide](amp-template/README.md) and the root [AMP Template Install](../README.md#amp-template-install) section. The `main` branch source files and AMP's `amp-templates` branch are maintained separately; pushing application changes to `main` does not update that branch.
