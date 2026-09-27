# MCModpackAutoUpdater

MCModpackAutoUpdater is a standalone .NET web app and worker pair for keeping Minecraft server modpacks up to date. It provides a small authenticated web UI for configuring modpack profiles, checking for new versions, queueing updates, tracking command history, and coordinating one or more machines that apply those updates.

The project is designed for self-hosted Minecraft administration, especially AMP-managed servers, but it can also sync direct server-pack ZIPs and run custom restart hooks.

## What It Does

- Hosts a local web dashboard at `http://0.0.0.0:9090` by default.
- Stores users, settings, agents, profiles, queued commands, and audit history in SQLite.
- Supports a built-in local runner and separate remote agents.
- Checks and syncs CurseForge, FTB, direct URL, and custom modpack profiles.
- Can build a server pack from CurseForge client files when a pack has no server-pack download.
- Preserves server runtime data such as worlds, logs, backups, bans, ops, whitelist, `server.properties`, and configured extra paths.
- Integrates with AMP for warnings, stop/start, instance config updates, and application updates.
- Queues Discord announcements after successful non-skipped syncs.
- Provides role-based access for admins, operators, and viewers.

## Repository Layout

```text
MCModpackAutoUpdater/
  MCModpackAutoUpdater/       ASP.NET Core web UI and embedded local runner
  MCAgent/                    Remote worker service that polls the runner API
  MCModpackAutoUpdater.slnx   Solution file
```

There is also a focused agent guide at [MCAgent/README.md](MCAgent/README.md).
For AMP installation and template maintenance, see [AMP Template Install](#amp-template-install). The template files live on the [`amp-templates` branch](https://github.com/MystifiedSky/MCModpackAutoUpdater/tree/amp-templates).

Start with **Choose a Setup Path** and **Quick Start** if you are new. The remainder of this file covers profiles, sync behavior, AMP template installation, publishing, and troubleshooting; the linked agent guide covers remote installation in detail.

## Requirements

- .NET 10 SDK for building and running from source.
- Network access from the runner to modpack provider APIs and download URLs.
- File-system access from the selected agent to each Minecraft server install root.
- A physical install-root directory: symlink/junction roots and update paths through links are rejected. Point the profile at the actual directory when using linked storage.
- Optional: AMP credentials if using AMP restart/config orchestration.
- Optional: Discord bot token if using update announcements.

For Linux remote agents, the included deploy script assumes:

- SSH access to the target host.
- `sudo` rights for the SSH user.
- `rsync` installed on the target.
- A systemd service created for the agent.

## Choose a Setup Path

- **Running the updater in AMP:** follow [AMP Template Install](#amp-template-install).
- **Trying it locally or running the web app yourself:** use the quick start below. It needs the .NET 10 SDK.
- **Running Minecraft on another machine:** first start the web app, then enroll and install [MCAgent](MCAgent/README.md) on the machine that can access the server files.
- **No .NET installation:** download the self-contained web app or agent ZIP from the [latest release](https://github.com/MystifiedSky/MCModpackAutoUpdater/releases/latest). These packages include the runtime. The `latest` release is an automatically updated build from `main`.

## Quick Start: Run Locally

From the repository root in PowerShell, run:

```powershell
$env:MC_UPDATER__WebUi__BindUrl = "http://127.0.0.1:9090"
dotnet run --project .\MCModpackAutoUpdater\MCModpackAutoUpdater.csproj
```

The first command keeps this development instance reachable only from the same machine. The app's default bind address listens on all network interfaces; choose an appropriate bind address and firewall/reverse-proxy setup before exposing it to a network.

Open `http://127.0.0.1:9090/setup`. **Opening this page generates and logs the one-time setup token**, so then copy the token from the terminal, create the first admin account, and sign in. The token is invalidated after the account is created.

The app creates its SQLite database and the built-in `Local Runner` automatically. If the web app process can access your Minecraft server directory, you do not need to install a separate agent.

### Run a Prebuilt Package

If you do not want to install the .NET SDK, download the matching self-contained web app ZIP from the [latest release](https://github.com/MystifiedSky/MCModpackAutoUpdater/releases/latest) and extract it into a persistent directory. From that directory, run:

```powershell
$env:MC_UPDATER__WebUi__BindUrl = "http://127.0.0.1:9090"
.\MCModpackAutoUpdater.exe
```

On Linux:

```bash
export MC_UPDATER__WebUi__BindUrl="http://127.0.0.1:9090"
./MCModpackAutoUpdater
```

Keep the process working directory stable: configuration, relative database paths, and key paths resolve from it.

## Create a First Test Profile

Use a disposable server directory or a backup for the first sync. In `/settings`, add a profile with:

1. **Assigned Agent:** `Local Runner` when the web app runs on the machine with the server files.
2. **Provider:** `Direct` for a server-pack ZIP URL.
3. **Server Pack URL:** the absolute HTTP/HTTPS URL for the ZIP.
4. **Version Lock:** for a stable URL with no positive numeric path segment, set a label such as `pack-1.0.0`. The resolver scans the whole URL path from right to left and uses the rightmost numeric segment before Version Lock; for example, `/2026/serverpack.zip` resolves as version `2026` even if a lock is set. Direct URLs do not inspect the ZIP, so use a path without numeric segments if you want the lock to identify new contents at the same URL.
5. **Install Root Path:** the absolute server directory, as seen by the selected agent.
6. **Current Version:** leave it blank for the initial test.
7. **Daily Check Time:** clear it until you have verified the manual workflow.
8. **Run On Startup:** choose **Do not run at startup** for this test.
9. **Restart Mode:** choose `none` for a disposable test directory with the Minecraft process stopped. New profiles default to `amp`, which requires AMP credentials and controller access. `none` applies files without stopping or starting the game process.

Save the profile, use **Check** to confirm the target version, then use **Check + Queue** to apply it and watch the result in `/history`. A full sync replaces matching pack-managed top-level entries. Runtime paths such as worlds and server properties are preserved automatically; configure **Preserved Paths** for other files that must survive replacement. For a live AMP-managed instance, configure AMP control and select its AMP restart mode before queueing an update.

## AMP Template Install

If you run the updater itself inside AMP, install its template from the `amp-templates` branch before creating the updater instance. AMP can then download the published runner ZIP, set the web UI port, set the database path, and manage the updater process like any other AMP application.

### Add the Template Repository

In AMP, open `Configuration` -> `Instance Deployment` -> `Configuration Repositories`, add this repository, then click `Fetch Latest`:

```text
MystifiedSky/MCModpackAutoUpdater:amp-templates
```

After the fetch completes, create a new instance using the `MCModpackAutoUpdater` application template.

AMP scans template repositories from the repository root, so the `amp-templates` branch contains only the deployment manifest and template files:

```text
manifest.json
mc-modpack-auto-updater.kvp
mc-modpack-auto-updaterconfig.json
mc-modpack-auto-updaterports.json
mc-modpack-auto-updaterupdates.json
```

If the template does not show up after `Fetch Latest`, refresh the browser and search for `MCModpackAutoUpdater` when creating a new instance. AMP also reads the repository `manifest.json`, so the template repository must be published with that file and the root-level template files on the selected branch.

### Manual Template Install

If you prefer to install the template files manually, check out the [`amp-templates` branch](https://github.com/MystifiedSky/MCModpackAutoUpdater/tree/amp-templates). Its repository root contains:

```text
mc-modpack-auto-updater.kvp
mc-modpack-auto-updaterconfig.json
mc-modpack-auto-updaterports.json
mc-modpack-auto-updaterupdates.json
```

1. Copy those four files from the `amp-templates` branch root into AMP's application template directory on the AMP controller or target ADS instance.
2. Restart AMP or refresh the application template list so AMP detects the new template.
3. Create a new instance using the `MCModpackAutoUpdater` application template.
4. In the instance settings, confirm:
   - `Release Repository`: `MystifiedSky/MCModpackAutoUpdater`
   - `Linux Release Asset`: `mc-modpack-auto-updater-linux-x64.zip`
   - `Windows Release Asset`: `mc-modpack-auto-updater-win-x64.zip`
   - AMP's assigned `Web UI` port in the instance port configuration: defaults to `9090`, but may be allocated differently if that port is in use.
   - `Web UI Database Path`: a persistent SQLite path, usually `mc-modpack-auto-updater.db`
5. Run AMP's update action for the instance so it downloads and extracts the release asset.
6. Start the instance.
7. Open the endpoint shown by AMP, usually:

```text
http://your-amp-host:9090/setup
```

On first start, open `/setup`, then check the AMP console or logs for the one-time setup token and enter it to create the first admin user.

The template sets these environment variables for the app:

```text
MC_UPDATER__WebUi__BindUrl=http://0.0.0.0:{{$WebUIPort}}
MC_UPDATER__WebUi__DatabasePath={{WebUiDatabasePath}}
```

Use the source-based quick start above for development or running outside AMP.

The app listener follows AMP's assigned Web UI port. When upgrading from template version 1, move any customized old `Web UI Port` application setting to AMP's instance port configuration before restarting; version 2 removes that duplicate setting.

AMP fetches templates only from the root of the `amp-templates` branch. The `main` branch contains the application and its setup instructions; it has no template copy. The release workflow updates application ZIPs only. Make future template changes on `amp-templates` and keep its `manifest.json` with the four template files.

### Release Assets

The `Release Repository`, `Linux Release Asset`, and `Windows Release Asset` fields tell AMP where to download the updater binaries when you run the AMP update action for the instance.

This repository includes a GitHub Actions workflow that builds self-contained `linux-x64` and `win-x64` packages on every push to the `main` branch. The workflow creates or updates the latest GitHub release with these assets:

```text
mc-modpack-auto-updater-linux-x64.zip
mc-modpack-auto-updater-win-x64.zip
mc-agent-linux-x64.zip
mc-agent-win-x64.zip
```

AMP downloads the latest published web runner asset matching the configured platform. These release packages include the .NET runtime, so the target machine does not need a separate .NET installation.

## Architecture

The system has two execution modes.

### Web UI and Local Runner

`MCModpackAutoUpdater` is the control application. It serves the dashboard, owns the SQLite database, schedules checks, queues commands, and includes an embedded local agent worker. The local runner is created automatically on first startup as `Local Runner`.

Use this mode when the web UI runs on the same machine that can safely access the Minecraft install directories.

### Remote Agent

`MCAgent` is a .NET Worker Service. It polls the web app API for commands, acknowledges work, executes updates locally on its host, and reports results back to the runner.

Use this mode when Minecraft servers live on another machine, or when you want the web UI and update execution separated.

The remote agent talks to:

- `POST /api/agent/heartbeat`
- `GET /api/agent/commands/pending`
- `POST /api/agent/commands/{id}/ack`
- `POST /api/agent/commands/{id}/complete`

## Configuration Sources

The web app reads:

- `MCModpackAutoUpdater/appsettings.json`
- `MCModpackAutoUpdater/appsettings.{Environment}.json`
- Optional private `MCModpackAutoUpdater/appsettings.Local.json`
- Environment variables prefixed with `MC_UPDATER__`
- Environment variables prefixed with `MC_AGENT__` for embedded agent settings

The remote agent reads:

- `MCAgent/appsettings.json`
- `MCAgent/appsettings.{Environment}.json`
- Optional private `MCAgent/appsettings.Local.json`
- Environment variables prefixed with `MC_AGENT__`

Do not commit real AMP credentials, agent tokens, Discord bot tokens, generated databases, or deployment-specific config.

## Web App Settings

Default web UI settings live in [MCModpackAutoUpdater/appsettings.json](MCModpackAutoUpdater/appsettings.json).

```json
{
  "WebUi": {
    "Enabled": true,
    "BindUrl": "http://0.0.0.0:9090",
    "DatabasePath": "mc-modpack-auto-updater.db",
    "SessionMinutes": 480
  }
}
```

Common overrides:

```powershell
$env:MC_UPDATER__WebUi__BindUrl = "http://0.0.0.0:9090"
$env:MC_UPDATER__WebUi__DatabasePath = "C:\mc-updater\mc-modpack-auto-updater.db"
$env:MC_UPDATER__WebUi__SessionMinutes = "480"
```

`DatabasePath` may be relative to the process's current working directory or absolute. The app creates the SQLite database and tables automatically.

## Runtime Settings

Runtime settings are seeded from config on first database creation, then managed in the `/settings` page.

- `RunOnStartup`: run eligible profiles when the app starts.
- `ExitAfterStartupRun`: queue startup syncs for every enabled profile with an enabled agent, bypass current-version skipping, wait for those commands to finish, then exit the app. Use only for intentional one-shot runs.
- `LoopDelaySeconds`: scheduler loop delay, from `5` to `3600`.
- `ScheduleTimeZone`: `Local`, `UTC`, or a host-supported time zone ID. The new-settings form defaults to `America/New_York`.

Each profile can also define its own daily check time; the form defaults to `03:00`. Leave **Daily Check Time** empty while validating a new profile manually. A scheduled profile can become due as soon as it is saved, depending on the current time and time zone. A saved **Requested Version** remains on the profile and is used by startup and force-sync operations; daily checks and **Check + Queue** resolve from the source and **Version Lock**, not the saved Requested Version. The force-sync form can supply a version for that command.

## Users and Roles

The first user created through `/setup` is an admin. Admins can create additional users from `/users`.

Roles:

- `Admin`: full settings, users, agents, and command access.
- `Operator`: can check, queue, force sync, toggle profiles, and view command history.
- `Viewer`: can view the dashboard only. Settings, agent management, users, and command history are unavailable.

Password policy:

- Minimum length: 10
- Requires at least one digit
- Requires at least one lowercase character
- Uppercase and non-alphanumeric characters are not required

## Agents

Agents are the machines that execute queued commands.

### Local Runner

The app creates a local runner automatically. Assign a profile to `Local Runner` when the web app process has access to the profile's `InstallRootPath`.

Local runner commands execute inside the web app host process, so run the web app under an account that has the necessary file and process permissions.

### Remote Agent

Create a remote agent from `/agents`; its token is shown once, so save it immediately. Follow the [MCAgent guide](MCAgent/README.md) to run it from source, install a self-contained release package, configure a Linux service, or use the deploy helper. The runner stores only a hash of the token; rotating it immediately invalidates the old token.

## Modpack Profiles

Profiles are configured from `/settings`. Each profile defines where updates come from, where files are applied, how the server is restarted, and which agent should execute the work.

Important fields:

- `Assigned Agent`: local or remote agent that will run the update.
- `Provider`: `CurseForge`, `FTB`, or `Direct`.
- `Source Reference`: CurseForge project ID or FTB pack ID.
- `Server Pack URL`: direct ZIP URL. Direct profiles use it; valid CurseForge or FTB source references take precedence, with the URL serving as a fallback when the source does not identify a provider project or pack.
- `Version Lock`: pin to a specific provider version or file ID instead of latest. For a direct URL, the rightmost positive numeric segment anywhere in the URL path takes precedence over this field.
- `Current Version`: current applied version ID. The updater uses this to skip already-current syncs.
- `Requested Version`: saved profile version selector used by startup and force-sync operations; it stays set until changed. Daily checks and **Check + Queue** resolve from the source and **Version Lock** instead. The Force Sync dialog can supply a version for one command.
- `Install Root Path`: server directory on the assigned agent machine.
- `Override Directory`: local directory copied over the generated/downloaded pack after the main sync.
- `Daily Check Time`: profile schedule time in the configured scheduler time zone.
- `Restart Mode`: usually `amp`, `none`, or a custom restart hook key.
- `Warning Minutes`: warning delay before stop/restart.
- `AMP Instance Name`: AMP instance name used with controller-level AMP orchestration.
- `AMP Instance API URL`: legacy direct AMP instance API fallback.
- `AMP Config JSON`: JSON object of AMP setting nodes and values to set before start.
- `Preserved Paths`: extra install-root-relative paths to snapshot and restore after sync.
- `Discord Channel ID` and `Discord Role ID`: optional announcement target.

### CurseForge Profiles

For CurseForge, set:

- `Provider`: `CurseForge`
- `Source Reference`: numeric CurseForge project ID. A URL is supported only when it contains a numeric path segment; human-readable project slugs are not resolved.
- `Server Pack URL`: optional direct server-pack ZIP

If the CurseForge project does not publish a server pack, enable `Build from CurseForge client files`. The agent downloads the client pack, reads `manifest.json`, downloads required files into `mods/`, copies overrides, and applies configured exclusions.

Use `Excluded CurseForge Project IDs` to skip specific manifest projects and `Generated Pack Excluded Paths` to remove generated files after materialization.

### FTB Profiles

For FTB, set:

- `Provider`: `FTB`
- `Source Reference`: FTB pack ID
- `Version Lock` or `Requested Version`: optional FTB version ID or version name

FTB packs are materialized with the official FTB server installer in non-interactive mode, with Java and modloader installation skipped. AMP installs the loader when using AMP orchestration; outside AMP, install and configure Java and the loader separately.

### Direct URL Profiles

For direct ZIPs, set:

- `Provider`: `Direct`
- `Server Pack URL`: ZIP URL
- `Version Lock`: used when no positive numeric segment exists anywhere in the URL path. The resolver scans path segments from right to left and uses the rightmost numeric segment before this field, so a URL like `/2026/serverpack.zip` resolves to `2026` even when Version Lock is set. The resolver does not inspect ZIP contents; for a stable URL with no numeric path segment, change Version Lock whenever you publish a new pack there.
- `Install Root Path`: target server directory

This is useful for custom packs or privately hosted server-pack artifacts. Version IDs are compared for equality; the updater does not infer that one arbitrary label is newer than another.

## Override Directory and Preserved Paths

The standalone updater supports an override workflow for files that should be re-applied on every update.

Use `Override Directory` for files you intentionally want to re-apply on every update. The directory can contain any install-root-relative structure, including `config/`, `mods/`, `defaultconfigs/`, `kubejs/`, `scripts/`, or individual files. After the updater downloads or generates the target pack and applies the main sync, it copies the override directory into the server install root with overwrite enabled.

If `Override Directory` is relative, it is resolved under `Install Root Path`. For example, with:

```text
Install Root Path: /home/amp/.ampdata/instances/MyServer
Override Directory: .a UPDATE Files
```

the agent reads overrides from:

```text
/home/amp/.ampdata/instances/MyServer/.a UPDATE Files
```

Example override layout:

```text
.a UPDATE Files/
  config/
    ftbquests.snbt
  defaultconfigs/
    serverconfig.toml
  kubejs/
    server_scripts/
      custom.js
  mods/
    required-admin-mod.jar
```

Use `Preserved Paths` for files or folders you want to keep exactly as they exist on the server during updates. Preserved paths are snapshotted before the main sync and restored after the sync and override pass. This is the right tool for runtime data that lives inside pack-managed folders, such as economy data, local configs edited by the server, or generated state that should not be replaced.

Examples:

```text
kubejs/AOFEconomy
config/server-specific.toml
mods/local-only-mod.jar
```

Preserved paths are install-root-relative and cannot use wildcards. If a path is listed in `Preserved Paths`, override copying and override delete markers skip it.

## Sync Behavior

Before applying files, the agent preserves common server runtime data, including:

- `world*`
- `logs*`
- `backups*`
- `crash-reports*`
- `server.properties*`
- `eula*`
- `ops*`
- `whitelist*`
- ban files
- `usercache*`

Additional profile-specific paths can be listed in `Preserved Paths`.

When `Force full sync` is enabled, top-level pack-managed entries from the new ZIP replace existing pack-managed entries. When disabled, overlay mode copies files without deleting existing pack-managed entries.

AMP-managed full sync uses AMP-aware apply rules: selected pack directories are replaced, while other files are overlaid and AMP-generated launch files are retained. See the sync behavior section in [the agent guide](MCAgent/README.md).

Override directories support delete markers. A file ending in `.DELETE` is treated as an instruction to delete the matching target path instead of copying that file.

Examples:

```text
mods/old-mod-name.jar.DELETE
mods/ftb-ranks-neoforge-*.jar.DELETE
```

On Windows filesystems, use safe wildcard tokens in marker filenames:

```text
mods/ftb-ranks-neoforge-__STAR__.jar.DELETE
```

`__STAR__` becomes `*`; `__Q__` becomes `?`.

## AMP Integration

The recommended AMP path is controller-level orchestration:

1. Open `/settings`.
2. Enable `AMP Controller Settings`.
3. Set the AMP controller API URL, username, password, and optional token.
4. Set profile `Restart Mode` to `amp`.
5. Set profile `AMP Instance Name` to the AMP instance name.

In this mode the agent asks the runner for runtime AMP config and can:

- Send a warning message.
- Stop the instance.
- Apply configured AMP setting values.
- Run AMP's update action.
- Start the instance.
- Auto-populate runtime settings such as Minecraft version, loader kind, loader version, release stream, and server JAR where supported.

There is also a legacy direct instance fallback. Use `Direct AMP API Fallback` plus the profile `AMP Instance API URL` only when you need per-instance API orchestration instead of the controller path.

## Custom Restart Hooks

Remote and embedded agents support restart hook templates keyed by restart mode. Configure them under `Agent:ModpackSync:RestartModes`.

Example:

```json
{
  "Agent": {
    "ModpackSync": {
      "RestartModes": {
        "shell": {
          "WarningCommandTemplate": "screen -S mc -p 0 -X stuff \"say Restarting in {warningMinutes} minutes for {targetVersionDisplay}^M\"",
          "StopCommandTemplate": "systemctl stop minecraft",
          "StartCommandTemplate": "systemctl start minecraft"
        }
      }
    }
  }
}
```

Supported template tokens:

- `{installRootPath}`
- `{modpackName}`
- `{commandId}`
- `{warningMinutes}`
- `{requestedVersion}`
- `{targetVersion}`
- `{targetVersionDisplay}`

## Discord Announcements

Discord announcements are configured from `/settings`.

1. Enable `Discord Announcements`.
2. Set a bot token.
3. Adjust the message template if needed.
4. Set each profile's `Discord Channel ID`.
5. Optionally set a profile `Discord Role ID`.

Announcements are queued after successful non-skipped syncs. Recent failures are shown on the settings page.

Default message template:

```text
{roleMention}{modpackName} updated to {version}.
```

## Command History and Manual Actions

The dashboard supports:

- `Check`: resolve the target version from the configured source and Version Lock without queueing an update.
- `Check + Queue`: check and queue only when the resolved target version ID differs from the current version ID.
- `Force Sync`: queue a sync even when the current version appears up to date.
- `Disable` or `Enable`: toggle a profile.

`/history` shows paginated command and audit records, payload/result JSON, and filters for agent, profile, status, and command type. Operators have read-only access. Admins can retry finalized commands, cancel pending work, and queue AMP console/config commands. A running update cannot be cancelled by changing its database status. If a command is stuck after its agent stopped or lost its recovery journal, stop the affected agent and inspect the target server first, then use **Mark Interrupted** to mark the command failed. That action does not stop execution; use **Retry** only after the server is safe to update.

`/agents` also provides agent details, heartbeat information, per-agent history, and arbitrary JSON command queueing for administrators. Deletion is refused while an agent or profile has active work.

The Force Sync form accepts a requested version and warning override for that run. Per-profile startup behavior can inherit the global setting or explicitly run/skip at startup.

## Publishing the Web App

Example framework-dependent publish:

```powershell
dotnet publish .\MCModpackAutoUpdater\MCModpackAutoUpdater.csproj -c Release -o .\publish\web
```

Run:

```powershell
Set-Location .\publish\web
dotnet .\MCModpackAutoUpdater.dll
```

Run from the publish directory so the app loads its published configuration and resolves relative database and key paths there.

This publishing mode requires the .NET 10 ASP.NET Core Runtime on the machine that runs the web app.

Set `MC_UPDATER__WebUi__DatabasePath` to a durable location before production use. Keep the database and any credential-bearing config outside source control.

## Remote Agent Installation

Remote agent installation, systemd setup, token handling, and the one-command deployment helper are documented in [MCAgent/README.md](MCAgent/README.md).

## Common Development Commands

```powershell
dotnet restore .\MCModpackAutoUpdater.slnx
dotnet build .\MCModpackAutoUpdater.slnx
dotnet run --project .\MCModpackAutoUpdater\MCModpackAutoUpdater.csproj
dotnet run --project .\MCAgent\MCAgent.csproj
```

## Troubleshooting

### I cannot create the first user

Open `/setup` first; that request generates and logs the first-run setup token. Then read it from the web app console logs. If a user already exists, `/setup` redirects to `/login`.

### A remote agent never checks in

Verify:

- The agent service is running.
- `MC_AGENT__ApiBaseUrl` points to the web app from the agent machine.
- The web app firewall allows the agent to connect.
- The token matches the one shown when the remote agent was created or rotated.
- The agent is enabled in `/agents`.

### A profile cannot queue updates

Verify:

- The profile is enabled.
- The assigned agent exists and is enabled.
- No sync command for that profile is already pending or running.
- The provider fields are valid.
- The agent machine can access `InstallRootPath`.

### AMP commands fail

Verify:

- AMP controller settings are enabled and correct.
- The profile uses `Restart Mode` value `amp`.
- `AMP Instance Name` matches the AMP instance name.
- The AMP user has permission to stop, start, update, and configure the instance.
- For legacy fallback, `AMP Instance API URL` and Direct AMP API settings are configured.

### Downloads or provider resolution fail

Verify:

- The runner and agent have outbound network access.
- CurseForge or FTB source IDs are correct.
- Direct server-pack URLs return a downloadable ZIP.
- Version locks and requested versions match provider IDs or names.

## Security Notes

- Bind the web UI carefully. The default `http://0.0.0.0:9090` listens on all interfaces.
- Put the app behind HTTPS or a trusted reverse proxy for remote access.
- Treat the SQLite database as sensitive; it contains local settings, command payloads, and credential-backed configuration.
- Store real credentials in environment variables, user secrets, or private deployment config.
- Agent tokens are shown once and stored as hashes by the runner.
- Run agents with the least privileges required to update the target server files and restart services.

AMP passwords/tokens and Discord bot tokens are encrypted in SQLite. Existing plaintext settings are upgraded on startup. Data Protection keys default to `data-protection-keys` beside the database; override this with `MC_UPDATER__WebUi__DataProtectionKeyPath`. Back up the database and key directory together and preserve their permissions. Windows keys are protected for the current OS account; moving to another account/machine may require re-entering credentials. On Linux, restrict access to the key directory to the runner account. Losing the keys makes saved secrets unreadable.

Use environment variables, user secrets in Development, or ignored `appsettings.Local.json` for private startup configuration. User secrets and `appsettings.Development.json` load only when the host environment is Development; set `DOTNET_ENVIRONMENT=Development` for the worker, or `DOTNET_ENVIRONMENT`/`ASPNETCORE_ENVIRONMENT` for the web app. No launch profile selects Development automatically. Local JSON overrides are loaded before the prefixed environment variables and are excluded from publish output. Operational settings already saved through the UI remain database-backed.

Run one web runner per database and one remote agent per token. The web runner locks its database path for its lifetime; a second runner using the same path refuses to start. Remote agents keep a durable completion journal (`Agent:CommandStatePath`, documented in the agent guide) containing their command ownership ID and hold an exclusive lock beside it. Keep the journal across upgrades and do not delete either lock file while its process is running. An interrupted execution with a matching checkpoint is marked failed with an inspection message; unknown in-progress work is left alone for an admin to inspect and mark interrupted. Embedded local execution uses database checkpoints for recovery. Upgrade the runner and remote agents together to benefit from command ownership.

Linux deployment and self-update preserve `updates/`, `state/`, `private/`, environment/local appsettings files, and default command journal names. Keep custom in-tree journals under `state/`, or list their relative paths in deployment `preservePaths` and the apply script's colon-separated `MC_AGENT_PRESERVE_PATHS`. The published `appsettings.json` remains replaceable; put credentials in private configuration or environment variables.

Releases include `mc-modpack-auto-updater-{win-x64|linux-x64}.zip` for the web runner and `mc-agent-{win-x64|linux-x64}.zip` for remote agents. GitHub Actions builds these self-contained packages on Ubuntu and publishes them as the latest release.

## License

MCModpackAutoUpdater is licensed under the GNU General Public License v3.0 or later.

Copyright (C) 2026 MystifiedSky.

See [LICENSE](LICENSE) for the full license text.
