# AMP Template Setup

This guide installs the MCModpackAutoUpdater web app as an AMP Generic Module. It does not install or manage the Minecraft instances that the updater will later control.

## Install the Template Repository

In AMP, open **Configuration → Instance Deployment → Configuration Repositories**, add `MystifiedSky/MCModpackAutoUpdater:amp-templates`, and choose **Fetch Latest**. Then create an instance from the `MCModpackAutoUpdater` application template.

AMP reads `manifest.json` and the template files from the repository branch root. The checked-in `amp-templates` branch contains:

```text
manifest.json
mc-modpack-auto-updater.kvp
mc-modpack-auto-updaterconfig.json
mc-modpack-auto-updaterports.json
mc-modpack-auto-updaterupdates.json
```

If the template is missing after fetching, refresh the AMP page and search again. Check AMP's supported template repository format and version if fetching still fails.

## Configure and Start the Instance

The template settings are:

- **Release Repository:** `MystifiedSky/MCModpackAutoUpdater`
- **Linux Release Asset:** `mc-modpack-auto-updater-linux-x64.zip`
- **Windows Release Asset:** `mc-modpack-auto-updater-win-x64.zip`
- **Web UI Port:** usually `9090`; set it before starting the app and allow access to it through the host firewall/reverse proxy as needed.
- **Web UI Database Path:** a persistent SQLite file path. Keep the database on persistent storage and back it up with the Data Protection key directory.

Save the instance settings, run AMP's **Update** action to download and extract the package, then start the instance. The ZIP is self-contained and includes the .NET runtime. Open the endpoint AMP shows, usually `http://your-amp-host:9090/setup`.

Opening `/setup` generates the one-time setup token and writes it to the AMP console/log. Copy it from the log, create the first admin account, and sign in. The template configures:

```text
MC_UPDATER__WebUi__BindUrl=http://0.0.0.0:{{WebUIPort}}
MC_UPDATER__WebUi__DatabasePath={{WebUiDatabasePath}}
```

The app creates its built-in `Local Runner` automatically. Use it when the updater process can access the Minecraft server files; otherwise, create a remote agent in `/agents` and install [MCAgent](../../MCAgent/README.md) on the server host.

## Manual Template Install

If AMP cannot fetch a repository, copy the four template files from this directory into AMP's application template directory on the controller or target ADS instance, then refresh AMP's template list and create an instance. The repository `manifest.json` is needed for repository fetching; it is not one of the four manually installed application template files.

## Template Maintenance

The source template files live in this directory on `main`. AMP fetches a separate `amp-templates` branch whose root contains the manifest and four template files. The release workflow updates application ZIPs only and does not copy template changes between branches. When changing a template, update both copies and the branch-level manifest as appropriate, then verify the config fields, port references, update asset names, application readiness setting, and branch layout.

The repository's release workflow publishes a rolling `latest` release after pushes to `main`. AMP's configured repository and asset names must match the ZIPs in that release.
