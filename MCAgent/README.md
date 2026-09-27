# MCAgent

`MCAgent` is a .NET Worker Service that connects to the configured control server agent API:

- `POST /api/agent/heartbeat`
- `GET /api/agent/commands/pending`
- `POST /api/agent/commands/{id}/ack`
- `POST /api/agent/commands/{id}/complete`

## Run Locally from Source

Use this for development from the repository root with the .NET 10 SDK. First start the control app and create a remote agent in `/agents`; its token is shown once, so save it before leaving the page. You do not need a separate agent on the same machine as the app's built-in `Local Runner`.

```powershell
$env:MC_AGENT__ApiBaseUrl = "http://localhost:9090"
$env:MC_AGENT__AuthToken = "paste-the-token-from-the-runner-here"
dotnet run --project .\MCAgent\MCAgent.csproj
```

Wait for the agent to appear online on the runner's `/agents` page. For a remote host, set `ApiBaseUrl` to a URL that host can reach; `localhost` only works when the runner is on the same machine.

For a host without the .NET runtime installed, download and extract the matching self-contained `mc-agent-linux-x64.zip` or `mc-agent-win-x64.zip` from the [latest release](https://github.com/MystifiedSky/MCModpackAutoUpdater/releases/latest), then configure the token as described below and run `./MCAgent` (Linux) or `./MCAgent.exe` (Windows). The packages include the .NET runtime.

## Config

The agent reads config from:

- `appsettings.json`
- `appsettings.{Environment}.json`
- optional private `appsettings.Local.json`
- environment variables with prefix `MC_AGENT__`

### Key Settings

- `ApiBaseUrl` (required): MCModpackAutoUpdater base URL, default `http://localhost:9090` (only appropriate when the runner is on the same machine).
- `AuthToken` (required): raw token from the runner's `/agents` page.
- `PollIntervalSeconds`: default poll loop delay.
- `CommandBatchSize`: max commands fetched per poll.
- `CommandStatePath` (optional): private local journal for runner commands. The default is a file under the current OS user's local application data directory, separated by a hash of the runner URL and agent token. An explicit absolute path is supported; a relative path is resolved from the agent application directory. Use a writable, private location and a different file for each runner/token pair.
- `ModpackSync.RestartModes`: optional shell restart hook templates keyed by `restartMode` (for example `shell`). AMP modes use API orchestration.
- `ModpackSync.AmpStateTimeoutSeconds`: maximum wait for AMP application stop/start/idle confirmation; defaults to 600 seconds and is clamped to 30–3600 seconds.
- `ModpackSync.FailIfRestartModeUnconfigured`: fail sync when a shell restart mode is unknown or misconfigured. Explicit AMP modes always require usable AMP control.
- `ModpackSync.AmpApi.*`: optional legacy fallback credentials for direct instance API mode (`restartMode=amp` with `modpack.ampApiUrl`).
- `SelfUpdate.Enabled`: allow/disallow `self_update` command.
- `SelfUpdate.WorkDirectory`: where update archives/staging are stored.
- `SelfUpdate.ApplyCommandTemplate`: optional command to apply staged update.
- `SelfUpdate.AllowApplyCommandFromPayload`: if `true`, payload may override apply command.

Configuration precedence is `appsettings.json`, environment-specific JSON, optional `appsettings.Local.json`, then `MC_AGENT__` environment variables. The prefix is removed during binding, so `MC_AGENT__AuthToken` maps to `AuthToken`. Keep real tokens and AMP credentials in the local file, user secrets, or environment variables; the local file is not copied into publish output. A deployed `appsettings.Local.json` must be placed beside the agent executable (the `WorkingDirectory` shown in the service example), because it is loaded from the app content root.

## Environment Variable Examples

Linux:

```bash
export MC_AGENT__ApiBaseUrl="http://your-runner.example.com:9090"
export MC_AGENT__AuthToken="paste-token-here"
export MC_AGENT__PollIntervalSeconds="20"
```

PowerShell:

```powershell
$env:MC_AGENT__ApiBaseUrl = "http://your-runner.example.com:9090"
$env:MC_AGENT__AuthToken = "paste-token-here"
$env:MC_AGENT__PollIntervalSeconds = "20"
```

## Supported Commands

- `noop`: completes immediately.
- `sync_modpack`: downloads and applies a server pack to the configured install path.
- `amp_console`: sends a direct AMP console command to a modpack's configured AMP instance.
- `amp_config`: reads or updates a setting on a modpack's configured AMP instance and verifies writes.
- `self_update`: downloads and stages an update ZIP. Optional apply command hook.

### sync_modpack Behavior

- Direct URL profiles use `modpack.serverPackUrl`. The resolver scans its URL path from right to left and uses the rightmost positive numeric segment before `versionLock`; otherwise set a version lock for a stable URL. CurseForge and FTB profiles resolve a valid `sourceReference` through their provider; the URL is used when the source does not identify a provider project or pack.
- For `provider=CurseForge`, `modpack.sourceReference` can be a CurseForge project ID (for example `1298402`).
- Agent resolves the latest matching file (or requested/version-locked file), then pulls the additional server-pack ZIP.
- For CurseForge packs without additional server packs, set `modpack.buildServerPackFromClientFiles=true`.
  The agent downloads the client pack ZIP, reads `manifest.json`, downloads required manifest files into `mods/`, copies `overrides/`, skips `modpack.serverPackExcludedCurseForgeProjectIds`, then deletes `modpack.serverPackExcludedPaths`.
  In this mode, `currentVersion` tracks the CurseForge parent/client file ID instead of a server-pack file ID.
- For `provider=FTB`, `modpack.sourceReference` is an FTB pack ID and `requestedVersion`/`versionLock` can be an FTB version ID or version name.
- FTB packs are materialized with the official FTB server installer in non-interactive mode using `-auto -force -skip-modloader -no-java`. AMP installs the loader when using AMP orchestration; outside AMP, install and configure Java and the loader separately.
- `modpack.currentVersion` is compared with the resolved target version and the sync is skipped when already current.
- If `restartMode` has configured restart hooks, the agent can run warning/stop/start shell commands around apply.
- If `restartMode=amp` or `amp_api`, the agent requires usable AMP control credentials and stops before changing files when AMP control is unavailable. With runner AMP controller settings, it fetches runtime credentials from the runner and orchestrates restarts through ADS (`ADSModule/CallAPI`, `ADSModule/StopInstance`, `ADSModule/SetInstanceConfig`, `ADSModule/StartInstance`) using `modpack.ampInstanceName`.
- If the runner provides per-modpack direct AMP API credentials, those are preferred over the older agent-local fallback settings. Keep these credentials in runner secrets and use HTTPS to the runner and AMP endpoints on untrusted networks.
- In AMP mode, after stop/config updates and before start, the agent also invokes `Core/UpdateApplication` (the AMP "Update" action) so loader/platform changes are applied.
- AMP `Core/Stop` and `Core/Start` responses mean the operation was accepted, not that the game process completed its transition. The agent polls `Core/GetStatus` and will not apply files until it confirms the game stopped or report success until the application is confirmed running. It fails closed on unknown state and observes `ModpackSync.AmpStateTimeoutSeconds` (10 minutes by default).
- Legacy fallback remains available: if `modpack.ampApiUrl` is set and agent-local `ModpackSync.AmpApi.*` credentials are configured, direct instance API orchestration is used (`Core/Login`, `Core/SendConsoleMessage`, `Core/Stop`, `Core/SetConfigs`, `Core/Start`).
- `modpack.ampConfigValuesJson` (optional JSON object) is applied through `Core/SetConfigs` before start; token placeholders are supported in values.
- In AMP mode, auto-detected runtime metadata is also used to pre-populate startup settings such as `ServerType`, `ReleaseStream`, and `ServerJAR` before `Core/UpdateApplication`.
- For CurseForge packs, the agent reads the parent pack `manifest.json` and auto-detects `minecraft.version` + `minecraft.modLoaders[].id` (for example `neoforge-21.1.219`), then attempts to apply the matching Forge/NeoForge loader version in AMP before start.
- For FTB packs, the agent reads `targets[]` from the official FTB version metadata and auto-detects the Minecraft + Forge/NeoForge runtime the same way before AMP start.
- While an FTB installer run is in progress, its stdout/stderr is mirrored into `<workDirectory>/ftb-server-installer.log` and the work directory path is included in the agent log entry for the command start.
- `forceFullSync` defaults to `true` when omitted.
- `ignoreCurrentVersion=true` bypasses the already-current skip and reapplies the resolved pack version. The runner manual force-sync action uses this for reinstalling the latest files.
- In a normal full sync, each top-level entry present in the new pack replaces the matching install-root entry. Entries absent from that archive are retained; use an explicit override `.DELETE` marker when an update must remove one.
- AMP-managed full sync uses AMP-aware apply rules: selected pack directories such as `mods`, `config`, `defaultconfigs`, and `kubejs` are replaced, while other paths are overlaid and AMP-generated launch files are retained. The resulting files can therefore differ from a normal full sync.
- Overlay mode (`forceFullSync=false`) copies files without deleting existing pack-managed entries.
- These paths are always preserved: `world*`, `logs*`, `backups*`, `crash-reports*`, `server.properties*`, `eula*`, `ops*`, `whitelist*`, bans, and `usercache*`.
- `modpack.preservedPaths` (optional array of install-root-relative paths) is snapshotted before apply and restored after sync/override work completes. Use this for runtime data that a pack incorrectly stores inside pack-managed folders like `kubejs/AOFEconomy`.
- Overrides are validated and staged before the stop phase. With restart orchestration enabled, a failure applying files or restoring preserved paths leaves the server stopped for inspection. Pack files do not have a full rollback; check the failed command and repair or reapply the pack before starting it. Without restart orchestration, stop and start the server yourself.
- Override directory supports delete markers: any file ending with `.DELETE` is treated as a delete instruction and is not copied.
  Example: `mods/ftb-ranks-neoforge-*.jar.DELETE` deletes matching entries from `<installRoot>/mods`.
  On Windows, use safe tokens in filenames: `__STAR__` -> `*`, `__Q__` -> `?`.
  Example on Windows: `mods/ftb-ranks-neoforge-__STAR__.jar.DELETE`.
- ZIP entries that traverse outside extraction or represent symbolic links are rejected. The configured install root must be a physical directory, and applying updates through an existing symbolic link/reparse point below it is also rejected. Override sources and their staging paths must use physical directories throughout their paths; junction/symlink aliases and overlapping source/staging directories are rejected before the stop phase.

### Command Recovery

The agent writes a local command journal before it acknowledges a command, before it enters a handler, and before it submits a final result. Its instance ID is stored in that journal and sent to the runner with requests, so another instance cannot claim or complete its active commands. If a completion request fails transiently, the journal retries that exact result on the next poll or after process restart rather than running the update again. After a restart, a matching journal entry lets the agent report an interrupted command without repeating its side effects. Check the target server and agent logs before manually retrying such a command: the filesystem or AMP side effects may already have happened.

Run only one MCAgent process for a runner/token pair. The agent holds an exclusive `.lock` file beside its journal for its lifetime; a second process using the same journal cannot start. Keep the journal private and persistent across service restarts, and do not copy a journal to another agent installation. If `CommandStatePath` is set explicitly, use a different file for each runner/token pair. Stop old workers before upgrading both runner and agent to use command ownership; older agents retain their legacy recovery behavior for commands without an owner.

If the journal is missing or damaged, the agent leaves unknown in-progress work alone. Stop the affected agent, inspect the server and logs, then use an admin account to choose **Mark Interrupted** on the command's history row. This marks it failed without stopping any process; use **Retry** only after the target is safe to update. A completed result that was accepted by the runner but whose response was lost can remain in the local journal if the runner no longer returns that command; the agent has no completed-command listing with which to prune it automatically.

`appsettings.Development.json` and `appsettings.Local.json` are not copied to publish output. User secrets and `appsettings.Development.json` load only when `DOTNET_ENVIRONMENT=Development`; no project launch profile selects that environment automatically. Supply deployment settings through environment variables or a private configuration file managed outside the publish package.

The publish output includes `scripts/apply-update-linux.sh` for the optional Linux self-update apply hook. Invoke it with `bash /path/to/scripts/apply-update-linux.sh <staging_dir> <target_dir> [service_name]`; this does not rely on an executable bit being retained by an archive.

### Restart Hook Template Tokens

Restart command templates can use:

- `{installRootPath}`
- `{modpackName}`
- `{commandId}`
- `{warningMinutes}`
- `{requestedVersion}`
- `{targetVersion}`
- `{targetVersionDisplay}`

Shell hook values containing shell syntax or control characters are rejected before warnings, stopping, or applying files. Only tokens used by a configured hook are checked. Spaces and Windows path separators are supported; quote path tokens in your templates. Hook templates themselves are trusted local commands, so keep them in private configuration controlled by an administrator.

AMP console commands are not automatically retried through alternate endpoints after a timeout or network error, because AMP may already have executed the command. Check the server console before resending. Alternate endpoints are tried only when AMP explicitly reports that an endpoint or method is unavailable.

AMP config JSON values support:

- `{requestedVersion}`
- `{currentVersion}`
- `{targetVersion}`
- `{targetVersionDisplay}`
- `{modpackName}`
- `{warningMinutes}`
- `{loaderId}`
- `{loaderKind}`
- `{loaderVersion}`
- `{minecraftVersion}`

### self_update Payload

```json
{
  "packageUrl": "https://example.com/mc-agent-update.zip",
  "version": "0.1.0",
  "applyNow": false
}
```

Payload fields:

- `packageUrl` (required)
- `expectedSha256` (exactly 64 hexadecimal characters; optional for staging, required before executing an apply hook)
- `version` (optional)
- `applyNow` (optional, default `false`)
- `applyCommand` (optional; ignored unless `AllowApplyCommandFromPayload=true`)

With the shipped defaults, this request stages the update but does not apply it. For unattended application, set `SelfUpdate.ApplyCommandTemplate` in trusted local agent configuration and send `applyNow: true` with the package's trusted `expectedSha256`. A fixed command can be `bash /opt/mc-agent/scripts/apply-update-linux.sh "{stagingDir}" "{baseDir}" "mc-agent"`. The service account needs write access to the target directory, `rsync`, and permission to restart that systemd service. Keep `AllowApplyCommandFromPayload=false` unless you explicitly want queued payloads to choose a shell command.

## Linux Service (systemd)

Install the self-contained agent release ZIP for your operating system, or publish from source. The example assumes an existing Linux service account named `amp`; replace it with an account that can access the target server files. For a source publish, run this from the repository root:

```bash
dotnet publish MCAgent/MCAgent.csproj -c Release -r linux-x64 --self-contained false -o ./publish/mc-agent
```

This framework-dependent Linux publish requires the .NET 10 Runtime on the target. The self-contained release packages do not.

On the target, create the service directory and a private environment file:

```bash
sudo install -d -o amp -g amp /opt/mc-agent
sudo install -d -m 0750 /etc/mc-agent
sudo install -m 0600 /dev/null /etc/mc-agent/agent.env
sudoedit /etc/mc-agent/agent.env
```

Set `MC_AGENT__ApiBaseUrl=https://your-site.example.com` and `MC_AGENT__AuthToken=PASTE_AGENT_TOKEN` in that file. If the agent is published from source, copy the contents of `./publish/mc-agent/` into `/opt/mc-agent` and ensure the files are owned by the `amp` service account. Extract release ZIP contents to that directory instead when using a self-contained package, then run:

```bash
sudo chmod 0755 /opt/mc-agent/MCAgent
```

Example unit:

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
EnvironmentFile=/etc/mc-agent/agent.env

[Install]
WantedBy=multi-user.target
```

Save the unit as `/etc/systemd/system/mc-agent.service`, then enable and start it:

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now mc-agent
sudo systemctl status mc-agent
```

This unit is for a framework-dependent publish or the one-command deploy helper and requires the .NET 10 Runtime. For a self-contained release ZIP, which does not need that runtime, replace `ExecStart` with `ExecStart=/opt/mc-agent/MCAgent` and make sure that file is executable. The `dotnet` form is also useful when the package was copied from Windows and lost its Linux executable bit.

## One-Command Deploy (PowerShell)

The example contains one target so you can get a single server running first; add another object to `targets` for each additional host. Deploy to one or more Linux servers with:

```powershell
Copy-Item .\MCAgent\scripts\deploy-targets.example.jsonc .\MCAgent\scripts\deploy-targets.json
# Edit deploy-targets.json with your server details.
.\MCAgent\scripts\deploy-agent.ps1 -ConfigPath .\MCAgent\scripts\deploy-targets.json
```

`sshKeyPath` is optional per target. If set, deploy uses `ssh/scp -i` to select that SSH identity. An encrypted key, SSH configuration, or `sudo` can still prompt for input.

What the script does:

- publishes `MCAgent` for `linux-x64`
- copies publish output + `scripts/apply-update-linux.sh`
- uploads to each target over SSH/SCP
- syncs to the configured `remotePath` (the example uses `/opt/mc-agent`) with `rsync --delete`
- fixes ownership/permissions
- restarts `mc-agent` service and prints status

Requirements on each target:

- your SSH user has `sudo` rights
- `rsync` installed
- `mc-agent` systemd service already created
- .NET 10 Runtime installed for the script's framework-dependent agent publish

The deploy helper checks for the required runtime over SSH before copying files to the target.
