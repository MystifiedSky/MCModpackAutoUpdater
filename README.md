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

## Requirements

- .NET 9 SDK for building and running from source.
- Network access from the runner to modpack provider APIs and download URLs.
- File-system access from the selected agent to each Minecraft server install root.
- Optional: AMP credentials if using AMP restart/config orchestration.
- Optional: Discord bot token if using update announcements.

For Linux remote agents, the included deploy script assumes:

- SSH access to the target host.
- `sudo` rights for the SSH user.
- `rsync` installed on the target.
- A systemd service created for the agent.

## AMP Template Install

If you run the updater itself inside AMP, install the bundled AMP template before creating the updater instance. This is the easiest production path for AMP-managed environments because AMP can download the published runner ZIP, set the web UI port, set the database path, and manage the updater process like any other AMP application.

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

If you prefer to install the template files manually, the same files are also kept in:

```text
MCModpackAutoUpdater/amp-template/
  mc-modpack-auto-updater.kvp
  mc-modpack-auto-updaterconfig.json
  mc-modpack-auto-updaterports.json
  mc-modpack-auto-updaterupdates.json
```

1. Copy all four files from `MCModpackAutoUpdater/amp-template/` into AMP's application template directory on the AMP controller or target ADS instance.
2. Restart AMP or refresh the application template list so AMP detects the new template.
3. Create a new instance using the `MCModpackAutoUpdater` application template.
4. In the instance settings, confirm:
   - `Release Repository`: `MystifiedSky/MCModpackAutoUpdater`
   - `Linux Release Asset`: `mc-modpack-auto-updater-linux-x64.zip`
   - `Windows Release Asset`: `mc-modpack-auto-updater-win-x64.zip`
   - `Web UI Port`: usually `9090`
   - `Web UI Database Path`: a persistent SQLite path, usually `mc-modpack-auto-updater.db`
5. Run AMP's update action for the instance so it downloads and extracts the release asset.
6. Start the instance.
7. Open the endpoint shown by AMP, usually:

```text
http://your-amp-host:9090/setup
```

On first start, check the AMP console or logs for the one-time setup token, then use it on `/setup` to create the first admin user.

The template sets these environment variables for the app:

```text
MC_UPDATER__WebUi__BindUrl=http://0.0.0.0:{{WebUIPort}}
MC_UPDATER__WebUi__DatabasePath={{WebUiDatabasePath}}
```

Use the source-based quick start below for development, testing, or running outside AMP.

### Release Assets

The `Release Repository`, `Linux Release Asset`, and `Windows Release Asset` fields tell AMP where to download the updater binaries when you run the AMP update action for the instance.

This repository includes a GitHub Actions workflow that builds self-contained `linux-x64` and `win-x64` packages on every push to the `main` branch. The workflow creates or updates the latest GitHub release with these assets:

```text
mc-modpack-auto-updater-linux-x64.zip
mc-modpack-auto-updater-win-x64.zip
```

AMP downloads the latest published release asset matching the configured platform.

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

## Quick Start

Run these commands from the repository root:

```powershell
dotnet restore .\MCModpackAutoUpdater.slnx
dotnet build .\MCModpackAutoUpdater.slnx
dotnet run --project .\MCModpackAutoUpdater\MCModpackAutoUpdater.csproj
```

Open:

```text
http://localhost:9090/setup
```

On first startup, the app logs a one-time setup token because no users exist yet:

```text
MCModpackAutoUpdater has no users. Open /setup and use first-run setup token: ...
```

Use that token to create the first admin account. After the first user exists, `/setup` redirects to `/login`.

## First-Time Setup Checklist

1. Start the web app.
2. Open `/setup` and create the first admin user with the setup token from the console logs.
3. Open `/agents` and decide whether to use the built-in `Local Runner` or create a remote agent.
4. Open `/settings` and configure runtime settings, AMP settings, Discord settings, and modpack profiles.
5. Open `/` to check profiles, queue updates, force syncs, and watch current state.
6. Open `/commandhistory` to inspect queued commands, results, payload JSON, retries, and cancellations.

## Configuration Sources

The web app reads:

- `MCModpackAutoUpdater/appsettings.json`
- `MCModpackAutoUpdater/appsettings.{Environment}.json`
- Environment variables prefixed with `MC_UPDATER__`
- Environment variables prefixed with `MC_AGENT__` for embedded agent settings

The remote agent reads:

- `MCAgent/appsettings.json`
- `MCAgent/appsettings.{Environment}.json`
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

`DatabasePath` may be relative to the app working directory or absolute. The app creates the SQLite database and tables automatically.

## Runtime Settings

Runtime settings are seeded from config on first database creation, then managed in the `/settings` page.

- `RunOnStartup`: run eligible profiles when the app starts.
- `ExitAfterStartupRun`: exit after startup work completes.
- `LoopDelaySeconds`: scheduler loop delay, from `5` to `3600`.
- `ScheduleTimeZone`: `Local`, `UTC`, or a host-supported time zone ID such as `America/New_York`.

Each profile can also define its own daily check time.

## Users and Roles

The first user created through `/setup` is an admin. Admins can create additional users from `/users`.

Roles:

- `Admin`: full settings, users, agents, and command access.
- `Operator`: can check, queue, force sync, and toggle profiles.
- `Viewer`: can view authenticated pages without operational actions.

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

Create a remote agent from `/agents`. The token is shown once; save it immediately and configure it on the remote machine.

PowerShell example:

```powershell
$env:MC_AGENT__ApiBaseUrl = "http://your-runner.example.com:9090"
$env:MC_AGENT__AuthToken = "paste-token-here"
$env:MC_AGENT__PollIntervalSeconds = "30"
dotnet run --project .\MCAgent\MCAgent.csproj
```

Linux example:

```bash
export MC_AGENT__ApiBaseUrl="http://your-runner.example.com:9090"
export MC_AGENT__AuthToken="paste-token-here"
export MC_AGENT__PollIntervalSeconds="30"
dotnet run --project MCAgent/MCAgent.csproj
```

The runner stores only a hash of the remote agent token. Rotating a token immediately invalidates the old token.

## Modpack Profiles

Profiles are configured from `/settings`. Each profile defines where updates come from, where files are applied, how the server is restarted, and which agent should execute the work.

Important fields:

- `Assigned Agent`: local or remote agent that will run the update.
- `Provider`: `CurseForge`, `FTB`, `Direct`, or `Custom`.
- `Source Reference`: CurseForge project ID or FTB pack ID.
- `Server Pack URL`: direct ZIP URL. If set, the agent uses it directly.
- `Version Lock`: pin to a specific provider version or file ID instead of latest.
- `Current Version`: current applied version ID. The updater uses this to skip already-current syncs.
- `Requested Version`: optional one-off target version override.
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
- `Source Reference`: CurseForge project ID
- `Server Pack URL`: optional direct server-pack ZIP

If the CurseForge project does not publish a server pack, enable `Build from CurseForge client files`. The agent downloads the client pack, reads `manifest.json`, downloads required files into `mods/`, copies overrides, and applies configured exclusions.

Use `Excluded CurseForge Project IDs` to skip specific manifest projects and `Generated Pack Excluded Paths` to remove generated files after materialization.

### FTB Profiles

For FTB, set:

- `Provider`: `FTB`
- `Source Reference`: FTB pack ID
- `Version Lock` or `Requested Version`: optional FTB version ID or version name

FTB packs are materialized with the official FTB server installer in non-interactive mode. AMP remains responsible for Forge or NeoForge installation and updates when using AMP orchestration.

### Direct URL Profiles

For direct ZIPs, set:

- `Provider`: `Direct`
- `Server Pack URL`: ZIP URL
- `Install Root Path`: target server directory

This is useful for custom packs or privately hosted server-pack artifacts.

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

- `Check`: resolve the latest target version without queueing an update.
- `Check + Queue`: check and queue only when a newer version exists.
- `Force Sync`: queue a sync even when the current version appears up to date.
- `Disable` or `Enable`: toggle a profile.

`/commandhistory` shows queued commands, audit records, payload JSON, result JSON, retry actions, cancellation for pending work, and AMP console/config command tools.

## Publishing the Web App

Example framework-dependent publish:

```powershell
dotnet publish .\MCModpackAutoUpdater\MCModpackAutoUpdater.csproj -c Release -o .\publish\web
```

Run:

```powershell
dotnet .\publish\web\MCModpackAutoUpdater.dll
```

Set `MC_UPDATER__WebUi__DatabasePath` to a durable location before production use. Keep the database and any credential-bearing config outside source control.

## Publishing a Linux Remote Agent

Example:

```bash
dotnet publish MCAgent/MCAgent.csproj -c Release -o /opt/mc-agent
```

Example systemd unit:

```ini
[Unit]
Description=MC Agent
After=network.target

[Service]
WorkingDirectory=/opt/mc-agent
ExecStart=/usr/bin/dotnet /opt/mc-agent/MCAgent.dll
Restart=always
RestartSec=5
User=amp
Environment=DOTNET_ENVIRONMENT=Production
Environment=MC_AGENT__ApiBaseUrl=https://your-runner.example.com
Environment=MC_AGENT__AuthToken=PASTE_AGENT_TOKEN

[Install]
WantedBy=multi-user.target
```

Then:

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now mc-agent
sudo systemctl status mc-agent
```

## One-Command Agent Deploy

The repository includes a PowerShell deploy helper for Linux agents:

```powershell
Copy-Item .\MCAgent\scripts\deploy-targets.example.jsonc .\MCAgent\scripts\deploy-targets.json
# Edit deploy-targets.json with your hosts, SSH user, paths, and service name.
.\MCAgent\scripts\deploy-agent.ps1 -ConfigPath .\MCAgent\scripts\deploy-targets.json
```

The script publishes `MCAgent` for `linux-x64`, uploads files over SSH/SCP, syncs with `rsync --delete`, fixes ownership/permissions, restarts the service, and prints service status.

Do not commit your real `deploy-targets.json`.

## Common Development Commands

```powershell
dotnet restore .\MCModpackAutoUpdater.slnx
dotnet build .\MCModpackAutoUpdater.slnx
dotnet run --project .\MCModpackAutoUpdater\MCModpackAutoUpdater.csproj
dotnet run --project .\MCAgent\MCAgent.csproj
```

## Troubleshooting

### I cannot create the first user

Check the web app console logs for the first-run setup token, then open `/setup`. If a user already exists, `/setup` redirects to `/login`.

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

## License

MCModpackAutoUpdater is licensed under the GNU General Public License v3.0 or later.

Copyright (C) 2026 MystifiedSky.

See [LICENSE](LICENSE) for the full license text.
