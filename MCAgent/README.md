# MCAgent

`MCAgent` is a .NET Worker Service that connects to the configured control server agent API:

- `POST /api/agent/heartbeat`
- `GET /api/agent/commands/pending`
- `POST /api/agent/commands/{id}/ack`
- `POST /api/agent/commands/{id}/complete`

## Run Locally

```powershell
dotnet run --project MCAgent/MCAgent.csproj
```

## Config

The agent reads config from:

- `appsettings.json`
- `appsettings.{Environment}.json`
- environment variables with prefix `MC_AGENT__`

### Key Settings

- `ApiBaseUrl` (required): MCModpackAutoUpdater base URL, default `http://localhost:9090`.
- `AuthToken` (required): raw token from the runner's `/agents` page.
- `PollIntervalSeconds`: default poll loop delay.
- `CommandBatchSize`: max commands fetched per poll.
- `ModpackSync.RestartModes`: optional restart hook templates keyed by `restartMode` (for example `amp`).
- `ModpackSync.FailIfRestartModeUnconfigured`: fail sync when mode is unknown/misconfigured instead of continuing.
- `ModpackSync.AmpApi.*`: optional legacy fallback credentials for direct instance API mode (`restartMode=amp` with `modpack.ampApiUrl`).
- `SelfUpdate.Enabled`: allow/disallow `self_update` command.
- `SelfUpdate.WorkDirectory`: where update archives/staging are stored.
- `SelfUpdate.ApplyCommandTemplate`: optional command to apply staged update.
- `SelfUpdate.AllowApplyCommandFromPayload`: if `true`, payload may override apply command.

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
- `self_update`: downloads and stages an update ZIP. Optional apply command hook.

### sync_modpack Behavior

- `modpack.serverPackUrl` (if provided) is used directly.
- For `provider=CurseForge`, `modpack.sourceReference` can be a CurseForge project ID (for example `1298402`).
- Agent resolves the latest matching file (or requested/version-locked file), then pulls the additional server-pack ZIP.
- For CurseForge packs without additional server packs, set `modpack.buildServerPackFromClientFiles=true`.
  The agent downloads the client pack ZIP, reads `manifest.json`, downloads required manifest files into `mods/`, copies `overrides/`, skips `modpack.serverPackExcludedCurseForgeProjectIds`, then deletes `modpack.serverPackExcludedPaths`.
  In this mode, `currentVersion` tracks the CurseForge parent/client file ID instead of a server-pack file ID.
- For `provider=FTB`, `modpack.sourceReference` is an FTB pack ID and `requestedVersion`/`versionLock` can be an FTB version ID or version name.
- FTB packs are materialized with the official FTB server installer in non-interactive mode using `-auto -force -skip-modloader -no-java`, so AMP still owns Forge/NeoForge installation and updates.
- `modpack.currentVersion` is compared with the resolved target version and the sync is skipped when already current.
- If `restartMode` has configured restart hooks, the agent can run warning/stop/start shell commands around apply.
- If `restartMode=amp` and runner AMP controller settings are configured, agent fetches runtime AMP credentials from the runner and orchestrates restarts through ADS (`ADSModule/CallAPI`, `ADSModule/StopInstance`, `ADSModule/SetInstanceConfig`, `ADSModule/StartInstance`) using `modpack.ampInstanceName`.
- In AMP mode, after stop/config updates and before start, the agent also invokes `Core/UpdateApplication` (the AMP "Update" action) so loader/platform changes are applied.
- Legacy fallback remains available: if `modpack.ampApiUrl` is set and agent-local `ModpackSync.AmpApi.*` credentials are configured, direct instance API orchestration is used (`Core/Login`, `Core/SendConsoleMessage`, `Core/Stop`, `Core/SetConfigs`, `Core/Start`).
- `modpack.ampConfigValuesJson` (optional JSON object) is applied through `Core/SetConfigs` before start; token placeholders are supported in values.
- In AMP mode, auto-detected runtime metadata is also used to pre-populate startup settings such as `ServerType`, `ReleaseStream`, and `ServerJAR` before `Core/UpdateApplication`.
- For CurseForge packs, the agent reads the parent pack `manifest.json` and auto-detects `minecraft.version` + `minecraft.modLoaders[].id` (for example `neoforge-21.1.219`), then attempts to apply the matching Forge/NeoForge loader version in AMP before start.
- For FTB packs, the agent reads `targets[]` from the official FTB version metadata and auto-detects the Minecraft + Forge/NeoForge runtime the same way before AMP start.
- While an FTB installer run is in progress, its stdout/stderr is mirrored into `<workDirectory>/ftb-server-installer.log` and the work directory path is included in the agent log entry for the command start.
- `forceFullSync` defaults to `true` when omitted.
- `ignoreCurrentVersion=true` bypasses the already-current skip and reapplies the resolved pack version. The runner manual force-sync action uses this for reinstalling the latest files.
- Full sync replaces top-level pack-managed entries from the ZIP.
- Overlay mode (`forceFullSync=false`) copies files without deleting existing pack-managed entries.
- These paths are always preserved: `world*`, `logs*`, `backups*`, `crash-reports*`, `server.properties*`, `eula*`, `ops*`, `whitelist*`, bans, and `usercache*`.
- `modpack.preservedPaths` (optional array of install-root-relative paths) is snapshotted before apply and restored after sync/override work completes. Use this for runtime data that a pack incorrectly stores inside pack-managed folders like `kubejs/AOFEconomy`.
- Override directory supports delete markers: any file ending with `.DELETE` is treated as a delete instruction and is not copied.
  Example: `mods/ftb-ranks-neoforge-*.jar.DELETE` deletes matching entries from `<installRoot>/mods`.
  On Windows, use safe tokens in filenames: `__STAR__` -> `*`, `__Q__` -> `?`.
  Example on Windows: `mods/ftb-ranks-neoforge-__STAR__.jar.DELETE`.

### Restart Hook Template Tokens

Restart command templates can use:

- `{installRootPath}`
- `{modpackName}`
- `{commandId}`
- `{warningMinutes}`
- `{requestedVersion}`
- `{targetVersion}`
- `{targetVersionDisplay}`

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
  "expectedSha256": "ABCDEF0123456789...",
  "version": "0.1.0",
  "applyNow": true,
  "applyCommand": "/opt/mc-agent/scripts/apply-update-linux.sh \"{stagingDir}\" \"{baseDir}\" \"mc-agent\""
}
```

Payload fields:

- `packageUrl` (required)
- `expectedSha256` (optional, case-insensitive hex)
- `version` (optional)
- `applyNow` (optional, default `false`)
- `applyCommand` (optional; used only when `AllowApplyCommandFromPayload=true`)

## Linux Service (systemd)

Publish:

```bash
dotnet publish MCAgent/MCAgent.csproj -c Release -o /opt/mc-agent
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
Environment=MC_AGENT__ApiBaseUrl=https://your-site.example.com
Environment=MC_AGENT__AuthToken=PASTE_AGENT_TOKEN

[Install]
WantedBy=multi-user.target
```

## One-Command Deploy (PowerShell)

You can deploy the agent to one or more Linux servers with:

```powershell
Copy-Item .\MCAgent\scripts\deploy-targets.example.jsonc .\MCAgent\scripts\deploy-targets.json
# Edit deploy-targets.json with your server details.
.\MCAgent\scripts\deploy-agent.ps1 -ConfigPath .\MCAgent\scripts\deploy-targets.json
```

`sshKeyPath` is optional per target. If set, deploy uses `ssh/scp -i` and avoids SSH password prompts.

What the script does:

- publishes `MCAgent` for `linux-x64`
- copies publish output + `scripts/apply-update-linux.sh`
- uploads to each target over SSH/SCP
- syncs to `/opt/mc-agent` with `rsync --delete`
- fixes ownership/permissions
- restarts `mc-agent` service and prints status

Requirements on each target:

- your SSH user has `sudo` rights
- `rsync` installed
- `mc-agent` systemd service already created
