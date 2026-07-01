using System.Diagnostics;
using System.IO.Enumeration;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Options;
using MCAgent.Models.AgentApi;
using MCAgent.Options;
using MCAgent.Services;

namespace MCAgent.Commands;

public sealed class SyncModpackCommandHandler : IAgentCommandHandler
{
    public const string DownloadHttpClientName = "MCAgent.ModpackDownloader";
    public const string AmpApiHttpClientName = "MCAgent.AmpApi";

    private const int CurseForgePublishedStatus = 4;
    private static readonly TimeSpan AmpStatePollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan AmpStateWaitTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan AmpUpdatePollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan AmpUpdateWaitTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan AmpUpdateAcceptanceWaitTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan AmpStartAcceptanceWaitTimeout = TimeSpan.FromSeconds(12);
    private static readonly string[] AmpUpdateKeywords =
    [
        "update",
        "upgrade",
        "download",
        "install",
        "forge",
        "neoforge",
        "loader",
        "modloader"
    ];
    private static readonly HashSet<string> KnownAmpConsoleCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "say",
        "tellraw",
        "title",
        "execute",
        "msg",
        "w",
        "me",
        "tm",
        "teammsg",
        "broadcast",
        "bc",
        "announce"
    };
    private static readonly Regex HumanVersionRegex = new(@"\d+(?:\.\d+)+", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly string[] PreservedTopLevelPatterns =
    [
        "world*",
        "logs*",
        "backups*",
        "crash-reports*",
        "eula*",
        "ops*",
        "whitelist*",
        "banned-ips*",
        "banned-players*",
        "usercache*",
        "server.properties*"
    ];
    private static readonly string[] AmpManagedReplaceTopLevelPatterns =
    [
        "mods",
        "config",
        "defaultconfigs",
        "kubejs",
        "scripts",
        "patchouli_books",
        "resourcepacks",
        "shaderpacks",
        "datapacks",
        "global_packs"
    ];

    private readonly ILogger<SyncModpackCommandHandler> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAgentApiClient _agentApiClient;
    private readonly AgentOptions _options;

    public SyncModpackCommandHandler(
        ILogger<SyncModpackCommandHandler> logger,
        IHttpClientFactory httpClientFactory,
        IAgentApiClient agentApiClient,
        IOptions<AgentOptions> options)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _agentApiClient = agentApiClient;
        _options = options.Value;
    }

    public string CommandType => "sync_modpack";

    public async Task<AgentCommandExecutionResult> ExecuteAsync(
        AgentCommandPayload command,
        CancellationToken cancellationToken)
    {
        if (!TryParsePayload(command.PayloadJson, out var payload, out var parseError))
        {
            return AgentCommandExecutionResult.Failed(parseError);
        }

        var modpack = payload.Modpack!;
        var installRootPath = modpack.InstallRootPath!.Trim();
        if (!Path.IsPathRooted(installRootPath))
        {
            return AgentCommandExecutionResult.Failed(
                "sync_modpack requires an absolute installRootPath.");
        }

        var installRoot = Path.GetFullPath(installRootPath);
        Directory.CreateDirectory(installRoot);

        var requestedVersion = Normalize(payload.Options?.RequestedVersion);
        var forceFullSync = payload.Options?.ForceFullSync ?? true;
        var skipWarnings = payload.Options?.SkipWarnings ?? false;
        var ignoreCurrentVersion = payload.Options?.IgnoreCurrentVersion ?? false;
        var syncMode = forceFullSync ? "full-sync" : "overlay";
        var restartMode = Normalize(modpack.RestartMode) ?? "none";
        var warningMinutes = ResolveWarningMinutes(modpack.WarningMinutes);
        var configuredPreservedPaths = NormalizeConfiguredPreservedPaths(modpack.PreservedPaths);

        var commandWorkDirectory = Path.Combine(
            ResolveWorkingRoot(),
            $"{DateTime.UtcNow:yyyyMMddHHmmss}-{command.Id}-{Guid.NewGuid():N}");
        var archivePath = Path.Combine(commandWorkDirectory, "serverpack.zip");
        var extractedPath = Path.Combine(commandWorkDirectory, "extracted");
        var preservedBackupRoot = Path.Combine(commandWorkDirectory, "preserved");

        Directory.CreateDirectory(commandWorkDirectory);
        Directory.CreateDirectory(extractedPath);

        var stopwatch = Stopwatch.StartNew();
        string? currentVersion = null;
        string? resolvedVersion = null;
        string? resolvedVersionDisplay = null;
        RestartExecutionState? restartExecution = null;
        DetectedMinecraftRuntime? detectedMinecraftRuntime = null;
        var autoAmpStartupConfigValues = new Dictionary<string, string>(StringComparer.Ordinal);
        var applyStats = new SyncApplyStats();
        string? appliedOverrideDirectory = null;
        IReadOnlyList<PreservedPathBackup> preservedPathBackups = [];

        try
        {
            var resolvedSource = await ResolveServerPackSourceAsync(payload, requestedVersion, cancellationToken);
            currentVersion = Normalize(modpack.CurrentVersion);
            resolvedVersion = ResolveVersionReference(resolvedSource);
            resolvedVersionDisplay = ResolveVersionDisplayReference(resolvedSource);
            var hasExplicitAmpConfigValues = !string.IsNullOrWhiteSpace(modpack.AmpConfigValuesJson);
            var ampRuntimeConfig = await TryGetAmpRuntimeConfigAsync(restartMode, modpack, cancellationToken);
            var ampApiRestartPlan = TryResolveAmpApiRestartPlan(restartMode, modpack, ampRuntimeConfig);
            var restartPlan = ampApiRestartPlan is null
                ? ResolveRestartPlan(restartMode)
                : RestartPlan.Disabled("restart_mode_amp_api");
            var restartOrchestrationEnabled = ampApiRestartPlan is not null || restartPlan.Enabled;
            restartExecution = RestartExecutionState.Create(
                restartMode,
                ampApiRestartPlan is not null
                    ? (ampApiRestartPlan.IsControllerMode ? "amp_ads_controller" : "amp_api")
                    : "shell",
                restartOrchestrationEnabled,
                restartOrchestrationEnabled ? null : restartPlan.Reason,
                skipWarnings,
                warningMinutes);

            _logger.LogInformation(
                "Executing sync_modpack #{CommandId}. Modpack={ModpackName}, Mode={Mode}, RestartMode={RestartMode}, InstallRoot={InstallRoot}, WorkDirectory={WorkDirectory}, Source={Source}, CurrentVersion={CurrentVersion}, TargetVersion={TargetVersion}.",
                command.Id,
                modpack.Name ?? "(unknown)",
                syncMode,
                restartMode,
                installRoot,
                commandWorkDirectory,
                resolvedSource.SourceKind,
                currentVersion ?? "(unknown)",
                resolvedVersion ?? "(unknown)");

            if (configuredPreservedPaths.Count > 0)
            {
                _logger.LogInformation(
                    "sync_modpack #{CommandId} will preserve {PreservedPathCount} configured paths for Modpack={ModpackName}: {PreservedPaths}.",
                    command.Id,
                    configuredPreservedPaths.Count,
                    modpack.Name ?? "(unknown)",
                    string.Join(", ", configuredPreservedPaths));
            }

            IReadOnlyList<AutoAmpLoaderConfigSelection> autoAmpLoaderConfigSelections = [];

            if (currentVersion is not null &&
                resolvedVersion is not null &&
                !ignoreCurrentVersion &&
                string.Equals(currentVersion, resolvedVersion, StringComparison.OrdinalIgnoreCase))
            {
                stopwatch.Stop();
                TryDeleteDirectory(commandWorkDirectory);

                var skippedPayloadJson = JsonSerializer.Serialize(new
                {
                    commandId = command.Id,
                    handler = CommandType,
                    mode = syncMode,
                    skipped = true,
                    reason = "already_up_to_date",
                    modpackName = modpack.Name,
                    requestedVersion,
                    ignoreCurrentVersion,
                    installRootPath = installRoot,
                    version = new
                    {
                        current = currentVersion,
                        target = resolvedVersion,
                        targetDisplay = resolvedVersionDisplay
                    },
                    source = new
                    {
                        resolvedSource.SourceKind,
                        resolvedSource.DownloadUrl,
                        resolvedSource.ProjectId,
                        resolvedSource.ParentFileId,
                        resolvedSource.ServerPackFileId,
                        resolvedSource.FtbPackId,
                        resolvedSource.FtbVersionId,
                        resolvedSource.SelectedVersion,
                        detectedMinecraftRuntime = (object?)null
                    },
                    restart = restartExecution.ToPayload(),
                    durationSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 2)
                });

                return AgentCommandExecutionResult.Completed(
                    $"sync_modpack skipped for '{modpack.Name ?? "modpack"}' (already on version {resolvedVersion}).",
                    skippedPayloadJson);
            }

            if (ampApiRestartPlan is not null)
            {
                try
                {
                    detectedMinecraftRuntime = await TryResolveMinecraftRuntimeAsync(
                        resolvedSource,
                        commandWorkDirectory,
                        cancellationToken);

                    if (detectedMinecraftRuntime is not null)
                    {
                        restartExecution.AmpAutoLoaderDetected = true;
                        restartExecution.AmpAutoLoaderKind = detectedMinecraftRuntime.LoaderKind;
                        restartExecution.AmpAutoLoaderVersion = detectedMinecraftRuntime.LoaderVersion;
                        restartExecution.AmpAutoLoaderId = detectedMinecraftRuntime.LoaderId;
                        restartExecution.AmpAutoMinecraftVersion = detectedMinecraftRuntime.MinecraftVersion;
                        autoAmpStartupConfigValues = await TryResolveAutoAmpStartupConfigValuesAsync(
                            ampApiRestartPlan,
                            detectedMinecraftRuntime,
                            cancellationToken);
                        if (autoAmpStartupConfigValues.Count > 0)
                        {
                            restartExecution.AmpAutoStartupSettings = autoAmpStartupConfigValues
                                .OrderBy(static entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                                .ToDictionary(static entry => entry.Key, static entry => entry.Value, StringComparer.Ordinal);
                        }

                        autoAmpLoaderConfigSelections = await TryResolveAutoAmpLoaderConfigSelectionsAsync(
                            ampApiRestartPlan,
                            detectedMinecraftRuntime,
                            cancellationToken);
                    }
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(
                        exception,
                        "sync_modpack #{CommandId} could not pre-resolve AMP loader config for Modpack={ModpackName}.",
                        command.Id,
                        modpack.Name ?? "(unknown)");
                    restartExecution.AmpAutoLoaderError = TruncateForLog(exception.Message, 300);
                }
            }

            var materializedSource = await MaterializeServerPackSourceAsync(
                resolvedSource,
                command.Id,
                commandWorkDirectory,
                archivePath,
                extractedPath,
                cancellationToken);
            var downloadedBytes = materializedSource.DownloadedBytes;
            var extractedFileCount = materializedSource.ExtractedFileCount;
            var contentRoot = materializedSource.ContentRoot;
            Exception? syncException = null;
            Exception? startException = null;
            var stopCommandExecuted = false;

            try
            {
                if (ampApiRestartPlan is not null)
                {
                    if (!skipWarnings && warningMinutes > 0)
                    {
                        await ExecuteAmpWarningSequenceAsync(
                            ampApiRestartPlan,
                            modpack.Name,
                            warningMinutes,
                            requestedVersion,
                            currentVersion,
                            resolvedVersion,
                            resolvedVersionDisplay,
                            restartExecution,
                            cancellationToken);
                    }
                }
                else if (restartPlan.Enabled)
                {
                    if (!skipWarnings &&
                        warningMinutes > 0 &&
                        !string.IsNullOrWhiteSpace(restartPlan.WarningCommandTemplate))
                    {
                        await ExecuteShellWarningSequenceAsync(
                            restartPlan.WarningCommandTemplate,
                            installRoot,
                            modpack.Name,
                            command.Id,
                            warningMinutes,
                            requestedVersion,
                            resolvedVersion,
                            resolvedVersionDisplay,
                            restartExecution,
                            cancellationToken);
                    }
                    else if (!skipWarnings && warningMinutes > 0)
                    {
                        _logger.LogWarning(
                            "sync_modpack #{CommandId} warning phase skipped because no warning command template is configured for restart mode '{RestartMode}'.",
                            command.Id,
                            restartMode);
                    }
                }

                if (ampApiRestartPlan is not null)
                {
                    await ExecuteAmpStopAsync(ampApiRestartPlan, cancellationToken);
                    if (ampApiRestartPlan.IsControllerMode)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
                    }
                    stopCommandExecuted = true;
                    restartExecution.StopCommandExecuted = true;
                }
                else if (restartPlan.Enabled)
                {
                    await ExecuteShellCommandAsync(
                        restartPlan.StopCommandTemplate!,
                        installRoot,
                        modpack.Name,
                        command.Id,
                        warningMinutes,
                        requestedVersion,
                        resolvedVersion,
                        resolvedVersionDisplay,
                        phase: "stop",
                        cancellationToken);
                    stopCommandExecuted = true;
                    restartExecution.StopCommandExecuted = true;
                }

                preservedPathBackups = BackupConfiguredPreservedPaths(
                    installRoot,
                    configuredPreservedPaths,
                    preservedBackupRoot);
                applyStats.BackedUpPreservedPaths = preservedPathBackups.Count;

                applyStats = forceFullSync
                    ? ampApiRestartPlan is not null
                        ? ApplyAmpManagedFullSync(contentRoot, installRoot, configuredPreservedPaths)
                        : ApplyFullSync(contentRoot, installRoot, configuredPreservedPaths)
                    : ApplyOverlay(contentRoot, installRoot, configuredPreservedPaths);
                applyStats.BackedUpPreservedPaths = preservedPathBackups.Count;

                var resolvedOverrideDirectory = ResolveOverrideDirectory(installRoot, Normalize(modpack.OverrideDirectory));
                if (resolvedOverrideDirectory is not null)
                {
                    if (!Directory.Exists(resolvedOverrideDirectory))
                    {
                        throw new InvalidOperationException(
                            $"Override directory does not exist: {resolvedOverrideDirectory}");
                    }

                    _logger.LogInformation(
                        "Applying override directory for sync_modpack #{CommandId}: {OverrideDirectory}.",
                        command.Id,
                        resolvedOverrideDirectory);

                    var overrideStats = ApplyOverrideWithDeletes(
                        resolvedOverrideDirectory,
                        installRoot,
                        configuredPreservedPaths);
                    applyStats.CopiedFiles += overrideStats.CopiedFiles;
                    applyStats.CopiedBytes += overrideStats.CopiedBytes;
                    applyStats.SkippedPreservedEntries += overrideStats.SkippedPreservedEntries;
                    applyStats.DeleteMarkersProcessed += overrideStats.DeleteMarkersProcessed;
                    applyStats.DeleteMarkerDeletedEntries += overrideStats.DeleteMarkerDeletedEntries;
                    applyStats.DeleteMarkersSkippedPreserved += overrideStats.DeleteMarkersSkippedPreserved;
                    applyStats.DeleteMarkersSkippedInvalid += overrideStats.DeleteMarkersSkippedInvalid;
                    appliedOverrideDirectory = resolvedOverrideDirectory;
                }
            }
            catch (Exception exception)
            {
                syncException = exception;
            }
            finally
            {
                if (preservedPathBackups.Count > 0)
                {
                    try
                    {
                        var restoredPreservedPathStats = RestoreConfiguredPreservedPaths(
                            installRoot,
                            preservedBackupRoot,
                            preservedPathBackups);
                        applyStats.CopiedFiles += restoredPreservedPathStats.CopiedFiles;
                        applyStats.CopiedBytes += restoredPreservedPathStats.CopiedBytes;
                        applyStats.RestoredPreservedPaths = preservedPathBackups.Count;
                    }
                    catch (Exception exception)
                    {
                        _logger.LogError(
                            exception,
                            "sync_modpack #{CommandId} failed while restoring configured preserved paths for Modpack={ModpackName}.",
                            command.Id,
                            modpack.Name ?? "(unknown)");

                        syncException = syncException is null
                            ? exception
                            : new InvalidOperationException(
                                $"Configured preserved path restore failed after sync apply error ({syncException.Message}): {exception.Message}",
                                exception);
                    }
                }

                if (stopCommandExecuted)
                {
                    try
                    {
                        if (ampApiRestartPlan is not null)
                        {
                            var explicitAmpConfigValues = ResolveAmpConfigValues(
                                modpack.AmpConfigValuesJson,
                                requestedVersion,
                                currentVersion,
                                resolvedVersion,
                                resolvedVersionDisplay,
                                modpack.Name,
                                warningMinutes,
                                detectedMinecraftRuntime);
                            var ampConfigValues = MergeAmpConfigValues(
                                autoAmpStartupConfigValues,
                                explicitAmpConfigValues);
                            var ampConfigValuesApplied = false;
                            var autoAmpLoaderApplied = false;
                            AutoAmpLoaderConfigApplyResult? autoAmpLoaderConfigResult = null;
                            Exception? ampConfigException = null;
                            try
                            {
                                if (ampConfigValues.Count > 0)
                                {
                                    await ExecuteAmpConfigUpdateAsync(
                                        ampApiRestartPlan,
                                        ampConfigValues,
                                        cancellationToken);
                                    restartExecution.AmpConfigValuesApplied = ampConfigValues.Count;
                                    ampConfigValuesApplied = true;
                                }

                                if (autoAmpLoaderConfigSelections.Count > 0)
                                {
                                    autoAmpLoaderConfigSelections = await TryRefreshAutoAmpLoaderConfigSelectionsAsync(
                                        ampApiRestartPlan,
                                        detectedMinecraftRuntime,
                                        autoAmpLoaderConfigSelections,
                                        cancellationToken);
                                    autoAmpLoaderConfigResult = await ApplyAutoAmpLoaderConfigSelectionAsync(
                                        ampApiRestartPlan,
                                        autoAmpLoaderConfigSelections,
                                        cancellationToken);
                                    restartExecution.AmpAutoLoaderConfigApplied = true;
                                    restartExecution.AmpAutoLoaderSettingNode = string.Join(
                                        ", ",
                                        autoAmpLoaderConfigResult.AppliedSettingNodes);
                                    restartExecution.AmpAutoLoaderSettingValue =
                                        autoAmpLoaderConfigResult.PrimarySelection.SettingValue;
                                    restartExecution.AmpConfigValuesApplied += autoAmpLoaderConfigResult.AppliedSettingNodes.Count;
                                    autoAmpLoaderApplied = true;
                                }
                            }
                            catch (Exception exception)
                            {
                                ampConfigException = exception;
                            }

                            if (ampConfigException is not null &&
                                !hasExplicitAmpConfigValues)
                            {
                                _logger.LogWarning(
                                    ampConfigException,
                                    "AMP auto loader config application failed but no explicit ampConfigValuesJson is configured; continuing with update/start.");
                                restartExecution.AmpAutoLoaderConfigApplied = false;
                                restartExecution.AmpAutoLoaderError ??=
                                    TruncateForLog(ampConfigException.Message, 300);
                                ampConfigException = null;
                            }

                            Exception? ampUpdateException = null;
                            try
                            {
                                await ExecuteAmpUpdateApplicationAndWaitAsync(
                                    ampApiRestartPlan,
                                    cancellationToken,
                                    operationDescription: "application update");
                                restartExecution.AmpUpdateApplicationExecuted = true;
                            }
                            catch (Exception exception)
                            {
                                if (ampConfigException is null &&
                                    autoAmpLoaderConfigResult?.PrimarySelection is not null &&
                                    IsAmpUpdateApplicationRecoverableException(exception))
                                {
                                    try
                                    {
                                        await RecoverAmpUpdateApplicationByNudgingLoaderVersionAsync(
                                            ampApiRestartPlan,
                                            autoAmpLoaderConfigResult.PrimarySelection,
                                            cancellationToken);
                                        restartExecution.AmpUpdateApplicationExecuted = true;
                                    }
                                    catch (Exception recoveryException)
                                    {
                                        ampUpdateException = new InvalidOperationException(
                                            $"AMP Core/UpdateApplication failed ({exception.Message}) and loader-version recovery failed ({recoveryException.Message}).",
                                            recoveryException);
                                    }
                                }
                                else if (ampConfigException is null)
                                {
                                    ampUpdateException = exception;
                                }
                                else
                                {
                                    ampConfigException = new InvalidOperationException(
                                        $"AMP configuration reapply after application update failed ({exception.Message}).",
                                        exception);
                                }
                            }

                            if (ampUpdateException is null &&
                                ampConfigException is null)
                            {
                                if (ampConfigValues.Count > 0)
                                {
                                    await ExecuteAmpConfigUpdateAsync(
                                        ampApiRestartPlan,
                                        ampConfigValues,
                                        cancellationToken);
                                    if (!ampConfigValuesApplied)
                                    {
                                        restartExecution.AmpConfigValuesApplied = ampConfigValues.Count;
                                        ampConfigValuesApplied = true;
                                    }
                                }

                                if (autoAmpLoaderConfigSelections.Count > 0)
                                {
                                    autoAmpLoaderConfigSelections = await TryRefreshAutoAmpLoaderConfigSelectionsAsync(
                                        ampApiRestartPlan,
                                        detectedMinecraftRuntime,
                                        autoAmpLoaderConfigSelections,
                                        cancellationToken);
                                    autoAmpLoaderConfigResult = await ApplyAutoAmpLoaderConfigSelectionAsync(
                                        ampApiRestartPlan,
                                        autoAmpLoaderConfigSelections,
                                        cancellationToken);
                                    restartExecution.AmpAutoLoaderConfigApplied = true;
                                    restartExecution.AmpAutoLoaderSettingNode = string.Join(
                                        ", ",
                                        autoAmpLoaderConfigResult.AppliedSettingNodes);
                                    restartExecution.AmpAutoLoaderSettingValue =
                                        autoAmpLoaderConfigResult.PrimarySelection.SettingValue;
                                    restartExecution.AmpAutoLoaderError = null;
                                    if (!autoAmpLoaderApplied)
                                    {
                                        restartExecution.AmpConfigValuesApplied += autoAmpLoaderConfigResult.AppliedSettingNodes.Count;
                                        autoAmpLoaderApplied = true;
                                    }
                                }
                            }

                            Exception? ampStartException = null;
                            if (ampUpdateException is null)
                            {
                                try
                                {
                                    if (restartExecution.AmpUpdateApplicationExecuted)
                                    {
                                        await WaitForAmpApplicationIdleBeforeStartAsync(
                                            ampApiRestartPlan,
                                            cancellationToken,
                                            operationDescription: "application update");
                                    }
                                    await ExecuteAmpStartAsync(ampApiRestartPlan, cancellationToken);
                                    restartExecution.StartCommandExecuted = true;
                                    if (ampApiRestartPlan.IsControllerMode)
                                    {
                                        await WaitForAmpApplicationReadyAfterStartAsync(
                                            ampApiRestartPlan,
                                            cancellationToken);
                                        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                                    }
                                }
                                catch (Exception exception)
                                {
                                    ampStartException = exception;
                                }
                            }

                            if (ampConfigException is not null &&
                                ampUpdateException is not null &&
                                ampStartException is not null)
                            {
                                throw new InvalidOperationException(
                                    $"AMP config step failed ({ampConfigException.Message}), AMP app update failed ({ampUpdateException.Message}), and AMP start failed ({ampStartException.Message}).",
                                    ampStartException);
                            }

                            if (ampConfigException is not null && ampUpdateException is not null)
                            {
                                throw new InvalidOperationException(
                                    $"AMP config step failed ({ampConfigException.Message}) and AMP app update failed ({ampUpdateException.Message}).",
                                    ampUpdateException);
                            }

                            if (ampConfigException is not null && ampStartException is not null)
                            {
                                throw new InvalidOperationException(
                                    $"AMP config step failed ({ampConfigException.Message}) and AMP start failed ({ampStartException.Message}).",
                                    ampStartException);
                            }

                            if (ampUpdateException is not null && ampStartException is not null)
                            {
                                throw new InvalidOperationException(
                                    $"AMP app update failed ({ampUpdateException.Message}) and AMP start failed ({ampStartException.Message}).",
                                    ampStartException);
                            }

                            if (ampStartException is not null)
                            {
                                throw ampStartException;
                            }

                            if (ampUpdateException is not null)
                            {
                                throw ampUpdateException;
                            }

                            if (ampConfigException is not null)
                            {
                                throw ampConfigException;
                            }
                        }
                        else if (restartPlan.Enabled)
                        {
                            await ExecuteShellCommandAsync(
                                restartPlan.StartCommandTemplate!,
                                installRoot,
                                    modpack.Name,
                                    command.Id,
                                    warningMinutes,
                                    requestedVersion,
                                    resolvedVersion,
                                    resolvedVersionDisplay,
                                    phase: "start",
                                    cancellationToken);

                            restartExecution.StartCommandExecuted = true;
                        }
                    }
                    catch (Exception exception)
                    {
                        startException = exception;
                    }
                }
            }

            if (syncException is not null)
            {
                if (startException is not null)
                {
                    throw new InvalidOperationException(
                        $"sync_modpack apply failed and restart start command also failed. ApplyError={syncException.Message}; StartError={startException.Message}",
                        syncException);
                }

                throw syncException;
            }

            if (startException is not null)
            {
                throw new InvalidOperationException(
                    $"sync_modpack applied files but restart start command failed: {startException.Message}",
                    startException);
            }

            stopwatch.Stop();
            TryDeleteDirectory(commandWorkDirectory);

            var resultPayloadJson = JsonSerializer.Serialize(new
            {
                commandId = command.Id,
                handler = CommandType,
                mode = syncMode,
                modpackName = modpack.Name,
                requestedVersion,
                ignoreCurrentVersion,
                installRootPath = installRoot,
                version = new
                {
                    current = currentVersion,
                    target = resolvedVersion,
                    targetDisplay = resolvedVersionDisplay
                },
                source = new
                {
                    resolvedSource.SourceKind,
                    resolvedSource.DownloadUrl,
                    resolvedSource.ProjectId,
                    resolvedSource.ParentFileId,
                    resolvedSource.ServerPackFileId,
                    resolvedSource.FtbPackId,
                    resolvedSource.FtbVersionId,
                    resolvedSource.SelectedVersion,
                    detectedMinecraftRuntime = detectedMinecraftRuntime is null
                        ? null
                        : new
                        {
                            detectedMinecraftRuntime.MinecraftVersion,
                            detectedMinecraftRuntime.LoaderId,
                            detectedMinecraftRuntime.LoaderKind,
                            detectedMinecraftRuntime.LoaderVersion
                        }
                },
                transfer = new
                {
                    downloadedBytes,
                    extractedFileCount
                },
                apply = new
                {
                    applyStats.CopiedFiles,
                    applyStats.CopiedBytes,
                    applyStats.ReplacedTopLevelEntries,
                    applyStats.SkippedPreservedEntries,
                    applyStats.DeleteMarkersProcessed,
                    applyStats.DeleteMarkerDeletedEntries,
                    applyStats.DeleteMarkersSkippedPreserved,
                    applyStats.DeleteMarkersSkippedInvalid,
                    applyStats.BackedUpPreservedPaths,
                    applyStats.RestoredPreservedPaths,
                    appliedOverrideDirectory
                },
                restart = restartExecution?.ToPayload(),
                durationSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 2)
            });

            return AgentCommandExecutionResult.Completed(
                $"sync_modpack {syncMode} completed for '{modpack.Name ?? "modpack"}'.",
                resultPayloadJson);
        }
        catch (Exception exception)
        {
            stopwatch.Stop();

            _logger.LogError(
                exception,
                "sync_modpack #{CommandId} failed for Modpack={ModpackName}.",
                command.Id,
                modpack.Name ?? "(unknown)");

            var failurePayloadJson = JsonSerializer.Serialize(new
            {
                commandId = command.Id,
                mode = syncMode,
                modpackName = modpack.Name,
                ignoreCurrentVersion,
                installRootPath = installRoot,
                workDirectory = commandWorkDirectory,
                version = new
                {
                    current = currentVersion,
                    target = resolvedVersion,
                    targetDisplay = resolvedVersionDisplay
                },
                source = detectedMinecraftRuntime is null
                    ? null
                    : new
                    {
                        detectedMinecraftRuntime.MinecraftVersion,
                        detectedMinecraftRuntime.LoaderId,
                        detectedMinecraftRuntime.LoaderKind,
                        detectedMinecraftRuntime.LoaderVersion
                    },
                apply = new
                {
                    applyStats.CopiedFiles,
                    applyStats.CopiedBytes,
                    applyStats.ReplacedTopLevelEntries,
                    applyStats.SkippedPreservedEntries,
                    applyStats.DeleteMarkersProcessed,
                    applyStats.DeleteMarkerDeletedEntries,
                    applyStats.DeleteMarkersSkippedPreserved,
                    applyStats.DeleteMarkersSkippedInvalid,
                    applyStats.BackedUpPreservedPaths,
                    applyStats.RestoredPreservedPaths,
                    appliedOverrideDirectory
                },
                restart = restartExecution?.ToPayload(),
                exception = exception.GetType().FullName,
                exception.Message
            });

            return AgentCommandExecutionResult.Failed(
                $"sync_modpack failed: {exception.Message}",
                failurePayloadJson);
        }
    }

    private async Task<ResolvedServerPackSource> ResolveServerPackSourceAsync(
        SyncModpackPayload payload,
        string? requestedVersion,
        CancellationToken cancellationToken)
    {
        var modpack = payload.Modpack!;
        var selectedVersion = NormalizeVersionSelector(requestedVersion ?? Normalize(modpack.VersionLock));
        var explicitServerPackUrl = Normalize(modpack.ServerPackUrl);
        var buildFromClientFiles = modpack.BuildServerPackFromClientFiles ?? false;

        if (string.Equals(modpack.Provider, "CurseForge", StringComparison.OrdinalIgnoreCase) &&
            TryParseCurseForgeProjectId(modpack.SourceReference, out var projectId))
        {
            if (!string.IsNullOrWhiteSpace(selectedVersion) &&
                int.TryParse(selectedVersion, NumberStyles.Integer, CultureInfo.InvariantCulture, out var selectedFileId))
            {
                if (buildFromClientFiles)
                {
                    var clientFile = await ResolveCurseForgeClientFileAsync(
                        projectId,
                        selectedVersion,
                        cancellationToken);
                    var clientDownloadUrl =
                        $"https://www.curseforge.com/api/v1/mods/{projectId}/files/{clientFile.Id}/download";

                    return new ResolvedServerPackSource(
                        clientDownloadUrl,
                        SourceKind: "curseforge_client_generated_server_pack",
                        ProjectId: projectId,
                        ParentFileId: clientFile.Id,
                        ServerPackFileId: null,
                        FtbPackId: null,
                        FtbVersionId: null,
                        DetectedRuntime: null,
                        DisplayVersion: ResolveCurseForgeClientDisplayVersion(clientFile, selectedVersion),
                        SelectedVersion: selectedVersion,
                        GeneratedServerPackExcludedPaths: modpack.ServerPackExcludedPaths,
                        GeneratedServerPackExcludedCurseForgeProjectIds: modpack.ServerPackExcludedCurseForgeProjectIds);
                }

                var explicitSelection = await TryResolveCurseForgeExplicitFileIdAsync(
                    projectId,
                    selectedFileId,
                    cancellationToken);
                if (explicitSelection is null)
                {
                    throw new InvalidOperationException(
                        $"CurseForge file ID {selectedFileId} was not found as a parent file or additional server-pack file.");
                }

                var explicitDownloadUrl =
                    $"https://www.curseforge.com/api/v1/mods/{projectId}/files/{explicitSelection.Value.ServerPackFile.Id}/download";
                return new ResolvedServerPackSource(
                    explicitDownloadUrl,
                    SourceKind: "curseforge_additional_server_pack",
                    ProjectId: projectId,
                    ParentFileId: explicitSelection.Value.ParentFile.Id,
                    ServerPackFileId: explicitSelection.Value.ServerPackFile.Id,
                    FtbPackId: null,
                    FtbVersionId: null,
                    DetectedRuntime: null,
                    DisplayVersion: ResolveCurseForgeDisplayVersion(
                        explicitSelection.Value.ParentFile,
                        explicitSelection.Value.ServerPackFile,
                        selectedVersion),
                    SelectedVersion: selectedVersion,
                    GeneratedServerPackExcludedPaths: null,
                    GeneratedServerPackExcludedCurseForgeProjectIds: null);
            }

            var parentFile = buildFromClientFiles
                ? await ResolveCurseForgeClientFileAsync(projectId, selectedVersion, cancellationToken)
                : await ResolveCurseForgeParentFileAsync(projectId, selectedVersion, cancellationToken);
            if (buildFromClientFiles)
            {
                var clientDownloadUrl =
                    $"https://www.curseforge.com/api/v1/mods/{projectId}/files/{parentFile.Id}/download";

                return new ResolvedServerPackSource(
                    clientDownloadUrl,
                    SourceKind: "curseforge_client_generated_server_pack",
                    ProjectId: projectId,
                    ParentFileId: parentFile.Id,
                    ServerPackFileId: null,
                    FtbPackId: null,
                    FtbVersionId: null,
                    DetectedRuntime: null,
                    DisplayVersion: ResolveCurseForgeClientDisplayVersion(parentFile, selectedVersion),
                    SelectedVersion: selectedVersion,
                    GeneratedServerPackExcludedPaths: modpack.ServerPackExcludedPaths,
                    GeneratedServerPackExcludedCurseForgeProjectIds: modpack.ServerPackExcludedCurseForgeProjectIds);
            }

            var serverPackFile = await ResolveCurseForgeServerPackFileAsync(projectId, parentFile.Id, cancellationToken);
            var downloadUrl = $"https://www.curseforge.com/api/v1/mods/{projectId}/files/{serverPackFile.Id}/download";

            return new ResolvedServerPackSource(
                downloadUrl,
                SourceKind: "curseforge_additional_server_pack",
                ProjectId: projectId,
                ParentFileId: parentFile.Id,
                ServerPackFileId: serverPackFile.Id,
                FtbPackId: null,
                FtbVersionId: null,
                DetectedRuntime: null,
                DisplayVersion: ResolveCurseForgeDisplayVersion(parentFile, serverPackFile, selectedVersion),
                SelectedVersion: selectedVersion,
                GeneratedServerPackExcludedPaths: null,
                GeneratedServerPackExcludedCurseForgeProjectIds: null);
        }

        if (string.Equals(modpack.Provider, "FTB", StringComparison.OrdinalIgnoreCase) &&
            TryParseFtbPackId(modpack.SourceReference, out var packId))
        {
            var version = await ResolveFtbVersionAsync(packId, selectedVersion, cancellationToken);
            return new ResolvedServerPackSource(
                DownloadUrl: null,
                SourceKind: "ftb_server_installer",
                ProjectId: null,
                ParentFileId: null,
                ServerPackFileId: null,
                FtbPackId: packId,
                FtbVersionId: version.Id,
                DetectedRuntime: TryResolveFtbMinecraftRuntime(version.Targets),
                DisplayVersion: Normalize(version.Name) ?? ResolveDisplayVersionFromSelector(selectedVersion),
                SelectedVersion: selectedVersion,
                GeneratedServerPackExcludedPaths: null,
                GeneratedServerPackExcludedCurseForgeProjectIds: null);
        }

        if (explicitServerPackUrl is not null)
        {
            EnsureAbsoluteHttpUrl(explicitServerPackUrl, "serverPackUrl");
            return new ResolvedServerPackSource(
                explicitServerPackUrl,
                SourceKind: "direct_url",
                ProjectId: null,
                ParentFileId: null,
                ServerPackFileId: null,
                FtbPackId: null,
                FtbVersionId: null,
                DetectedRuntime: null,
                DisplayVersion: ResolveDisplayVersionFromDirectUrl(explicitServerPackUrl, selectedVersion),
                SelectedVersion: selectedVersion,
                GeneratedServerPackExcludedPaths: null,
                GeneratedServerPackExcludedCurseForgeProjectIds: null);
        }

        if (string.Equals(modpack.Provider, "CurseForge", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "CurseForge sourceReference must be a numeric project ID, or provide serverPackUrl.");
        }

        if (string.Equals(modpack.Provider, "FTB", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "FTB sourceReference must be a numeric pack ID, or provide serverPackUrl.");
        }

        throw new InvalidOperationException(
            "serverPackUrl is required when provider is not CurseForge.");
    }

    private async Task<MaterializedServerPackSource> MaterializeServerPackSourceAsync(
        ResolvedServerPackSource resolvedSource,
        int commandId,
        string commandWorkDirectory,
        string archivePath,
        string extractedPath,
        CancellationToken cancellationToken)
    {
        if (string.Equals(resolvedSource.SourceKind, "ftb_server_installer", StringComparison.OrdinalIgnoreCase))
        {
            return await MaterializeFtbServerPackSourceAsync(
                resolvedSource,
                commandId,
                commandWorkDirectory,
                cancellationToken);
        }

        if (string.Equals(resolvedSource.SourceKind, "curseforge_client_generated_server_pack", StringComparison.OrdinalIgnoreCase))
        {
            return await MaterializeCurseForgeClientGeneratedServerPackAsync(
                resolvedSource,
                archivePath,
                extractedPath,
                commandWorkDirectory,
                cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(resolvedSource.DownloadUrl))
        {
            throw new InvalidOperationException("Resolved server pack source does not include a download URL.");
        }

        var downloadedBytes = await DownloadToFileAsync(resolvedSource.DownloadUrl, archivePath, cancellationToken);
        var extractedFileCount = ExtractZipArchive(archivePath, extractedPath);
        var contentRoot = ResolveExtractedContentRoot(extractedPath);
        return new MaterializedServerPackSource(contentRoot, downloadedBytes, extractedFileCount);
    }

    private async Task<MaterializedServerPackSource> MaterializeCurseForgeClientGeneratedServerPackAsync(
        ResolvedServerPackSource resolvedSource,
        string archivePath,
        string extractedPath,
        string commandWorkDirectory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(resolvedSource.DownloadUrl))
        {
            throw new InvalidOperationException("CurseForge client-generated source is missing a download URL.");
        }

        var downloadedBytes = await DownloadToFileAsync(resolvedSource.DownloadUrl, archivePath, cancellationToken);
        ExtractZipArchive(archivePath, extractedPath);
        var clientRoot = ResolveExtractedContentRoot(extractedPath);
        var manifestPath = Path.Combine(clientRoot, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException(
                "CurseForge client pack did not contain manifest.json; cannot generate server pack.");
        }

        await using var manifestStream = File.OpenRead(manifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<CurseForgeManifest>(
            manifestStream,
            JsonOptions,
            cancellationToken);
        if (manifest?.Files is null || manifest.Files.Count == 0)
        {
            throw new InvalidOperationException(
                "CurseForge client pack manifest did not contain any downloadable files.");
        }

        var generatedRoot = Path.Combine(commandWorkDirectory, "generated-server-pack");
        var generatedModsDirectory = Path.Combine(generatedRoot, "mods");
        Directory.CreateDirectory(generatedRoot);
        Directory.CreateDirectory(generatedModsDirectory);

        var overridesDirectoryName = Normalize(manifest.Overrides) ?? "overrides";
        var overridesDirectory = Path.Combine(clientRoot, overridesDirectoryName);
        if (Directory.Exists(overridesDirectory))
        {
            CopyDirectoryRecursive(overridesDirectory, generatedRoot, new SyncApplyStats());
        }

        var excludedProjectIds = new HashSet<int>(
            resolvedSource.GeneratedServerPackExcludedCurseForgeProjectIds ?? [],
            EqualityComparer<int>.Default);
        var manifestFiles = manifest.Files
            .Where(file => file.Required != false)
            .Where(file => file.ProjectId > 0 && file.FileId > 0)
            .Where(file => !excludedProjectIds.Contains(file.ProjectId))
            .ToList();

        foreach (var file in manifestFiles)
        {
            var downloadUrl = $"https://www.curseforge.com/api/v1/mods/{file.ProjectId}/files/{file.FileId}/download";
            var destinationPath = Path.Combine(
                generatedModsDirectory,
                $"{file.ProjectId}-{file.FileId}.jar");
            downloadedBytes += await DownloadToFileAsync(downloadUrl, destinationPath, cancellationToken);
        }

        foreach (var relativePath in resolvedSource.GeneratedServerPackExcludedPaths ?? [])
        {
            DeleteGeneratedServerPackPath(generatedRoot, relativePath);
        }

        var generatedFileCount = Directory.EnumerateFiles(generatedRoot, "*", SearchOption.AllDirectories).Count();
        return new MaterializedServerPackSource(generatedRoot, downloadedBytes, generatedFileCount);
    }

    private async Task<MaterializedServerPackSource> MaterializeFtbServerPackSourceAsync(
        ResolvedServerPackSource resolvedSource,
        int commandId,
        string commandWorkDirectory,
        CancellationToken cancellationToken)
    {
        if (!resolvedSource.FtbPackId.HasValue || !resolvedSource.FtbVersionId.HasValue)
        {
            throw new InvalidOperationException("FTB source resolution is missing pack/version metadata.");
        }

        var installerUrl = BuildFtbServerInstallerDownloadUrl(
            resolvedSource.FtbPackId.Value,
            resolvedSource.FtbVersionId.Value);
        var installerPath = Path.Combine(
            commandWorkDirectory,
            OperatingSystem.IsWindows() ? "ftb-server-installer.exe" : "ftb-server-installer");
        var installDirectory = Path.Combine(commandWorkDirectory, "ftb-installed");
        Directory.CreateDirectory(installDirectory);

        var downloadedBytes = await DownloadToFileAsync(installerUrl, installerPath, cancellationToken);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            EnsureExecutableFileMode(installerPath);
        }

        await ExecuteFtbServerInstallerAsync(
            installerPath,
            commandId,
            resolvedSource.FtbPackId.Value,
            resolvedSource.FtbVersionId.Value,
            installDirectory,
            cancellationToken);

        var contentRoot = ResolveExtractedContentRoot(installDirectory);
        var extractedFileCount = Directory.EnumerateFiles(contentRoot, "*", SearchOption.AllDirectories).Count();
        return new MaterializedServerPackSource(contentRoot, downloadedBytes, extractedFileCount);
    }

    private async Task<DetectedMinecraftRuntime?> TryResolveMinecraftRuntimeAsync(
        ResolvedServerPackSource resolvedSource,
        string commandWorkDirectory,
        CancellationToken cancellationToken)
    {
        if (resolvedSource.DetectedRuntime is not null)
        {
            return resolvedSource.DetectedRuntime;
        }

        return await TryResolveCurseForgeMinecraftRuntimeAsync(
            resolvedSource,
            commandWorkDirectory,
            cancellationToken);
    }

    private async Task<DetectedMinecraftRuntime?> TryResolveCurseForgeMinecraftRuntimeAsync(
        ResolvedServerPackSource resolvedSource,
        string commandWorkDirectory,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                resolvedSource.SourceKind,
                "curseforge_additional_server_pack",
                StringComparison.OrdinalIgnoreCase) ||
            !resolvedSource.ProjectId.HasValue ||
            !resolvedSource.ParentFileId.HasValue)
        {
            return null;
        }

        var parentArchivePath = Path.Combine(commandWorkDirectory, "curseforge-parent-file.zip");
        var parentDownloadUrl =
            $"https://www.curseforge.com/api/v1/mods/{resolvedSource.ProjectId.Value}/files/{resolvedSource.ParentFileId.Value}/download";
        await DownloadToFileAsync(parentDownloadUrl, parentArchivePath, cancellationToken);

        var detectedRuntime = TryReadCurseForgeManifestRuntime(parentArchivePath);
        if (detectedRuntime is not null)
        {
            _logger.LogInformation(
                "Detected mod loader metadata from CurseForge manifest. Loader={LoaderId}, LoaderKind={LoaderKind}, LoaderVersion={LoaderVersion}, MinecraftVersion={MinecraftVersion}.",
                detectedRuntime.LoaderId,
                detectedRuntime.LoaderKind,
                detectedRuntime.LoaderVersion,
                detectedRuntime.MinecraftVersion ?? "(unknown)");
        }

        return detectedRuntime;
    }

    private static DetectedMinecraftRuntime? TryReadCurseForgeManifestRuntime(string parentArchivePath)
    {
        using var archive = ZipFile.OpenRead(parentArchivePath);
        var manifestEntry = archive.Entries.FirstOrDefault(static entry =>
            string.Equals(entry.FullName, "manifest.json", StringComparison.OrdinalIgnoreCase));
        if (manifestEntry is null)
        {
            return null;
        }

        using var manifestStream = manifestEntry.Open();
        using var reader = new StreamReader(manifestStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var manifestJson = reader.ReadToEnd();
        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            return null;
        }

        using var document = JsonDocument.Parse(manifestJson);
        if (!TryGetPropertyIgnoreCase(document.RootElement, "minecraft", out var minecraftElement) ||
            minecraftElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var minecraftVersion = TryGetPropertyIgnoreCase(minecraftElement, "version", out var minecraftVersionElement)
            ? ReadJsonString(minecraftVersionElement)
            : null;

        if (!TryGetPropertyIgnoreCase(minecraftElement, "modLoaders", out var modLoadersElement) ||
            modLoadersElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? loaderId = null;
        foreach (var loaderElement in modLoadersElement.EnumerateArray())
        {
            if (loaderElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var candidateLoaderId = TryGetPropertyIgnoreCase(loaderElement, "id", out var idElement)
                ? ReadJsonString(idElement)
                : null;
            if (string.IsNullOrWhiteSpace(candidateLoaderId))
            {
                continue;
            }

            var isPrimary = TryGetPropertyIgnoreCase(loaderElement, "primary", out var primaryElement) &&
                            TryReadBooleanLoose(primaryElement, out var primaryValue) &&
                            primaryValue;

            if (isPrimary)
            {
                loaderId = candidateLoaderId;
                break;
            }

            loaderId ??= candidateLoaderId;
        }

        if (string.IsNullOrWhiteSpace(loaderId))
        {
            return null;
        }

        ParseLoaderId(loaderId, out var loaderKind, out var loaderVersion);
        return new DetectedMinecraftRuntime(
            minecraftVersion,
            loaderId.Trim(),
            loaderKind,
            loaderVersion);
    }

    private static void ParseLoaderId(string loaderId, out string loaderKind, out string loaderVersion)
    {
        var normalizedLoaderId = loaderId.Trim();
        var separatorIndex = normalizedLoaderId.IndexOf('-', StringComparison.Ordinal);
        if (separatorIndex <= 0 || separatorIndex >= normalizedLoaderId.Length - 1)
        {
            loaderKind = normalizedLoaderId.ToLowerInvariant();
            loaderVersion = normalizedLoaderId;
            return;
        }

        loaderKind = normalizedLoaderId[..separatorIndex].Trim().ToLowerInvariant();
        loaderVersion = normalizedLoaderId[(separatorIndex + 1)..].Trim();
    }

    private async Task<FtbVersionEntry> ResolveFtbVersionAsync(
        int packId,
        string? selectedVersion,
        CancellationToken cancellationToken)
    {
        var url = $"https://api.feed-the-beast.com/v1/modpacks/public/modpack/{packId}";
        var response = await GetJsonAsync<FtbPublicModpackResponse>(url, cancellationToken);
        var versions = response.Versions?
            .Where(version => !version.Private)
            .OrderByDescending(version => version.Released)
            .ThenByDescending(version => version.Updated)
            .ToList()
            ?? [];

        if (versions.Count == 0)
        {
            throw new InvalidOperationException($"No public versions found for FTB pack {packId}.");
        }

        if (string.IsNullOrWhiteSpace(selectedVersion))
        {
            return versions.FirstOrDefault(version =>
                       string.Equals(version.Type, "release", StringComparison.OrdinalIgnoreCase))
                   ?? versions[0];
        }

        var normalizedSelector = selectedVersion.Trim();
        if (TryParseFtbVersionId(normalizedSelector, out var exactVersionId))
        {
            var exactMatch = versions.FirstOrDefault(version => version.Id == exactVersionId);
            if (exactMatch is not null)
            {
                return exactMatch;
            }

            throw new InvalidOperationException($"FTB version ID {exactVersionId} was not found for pack {packId}.");
        }

        var matchedByName = versions.FirstOrDefault(version =>
            ContainsInvariant(version.Name, normalizedSelector));
        if (matchedByName is not null)
        {
            return matchedByName;
        }

        throw new InvalidOperationException(
            $"No FTB version matched requested version '{normalizedSelector}' for pack {packId}.");
    }

    private static DetectedMinecraftRuntime? TryResolveFtbMinecraftRuntime(
        IReadOnlyList<FtbTargetEntry>? targets)
    {
        if (targets is null || targets.Count == 0)
        {
            return null;
        }

        var minecraftVersion = targets.FirstOrDefault(target =>
            string.Equals(target.Name, "minecraft", StringComparison.OrdinalIgnoreCase))?.Version;
        var loaderTarget = targets.FirstOrDefault(target =>
            string.Equals(target.Name, "forge", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(target.Name, "neoforge", StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(loaderTarget?.Name) || string.IsNullOrWhiteSpace(loaderTarget.Version))
        {
            return null;
        }

        var loaderKind = loaderTarget.Name.Trim().ToLowerInvariant();
        var loaderVersion = loaderTarget.Version.Trim();
        return new DetectedMinecraftRuntime(
            minecraftVersion?.Trim(),
            $"{loaderKind}-{loaderVersion}",
            loaderKind,
            loaderVersion);
    }

    private static string BuildFtbServerInstallerDownloadUrl(int packId, int versionId)
    {
        var platformPath = GetFtbServerInstallerPlatformPath();
        return $"https://api.feed-the-beast.com/v1/modpacks/public/modpack/{packId}/{versionId}/server/{platformPath}";
    }

    private static string GetFtbServerInstallerPlatformPath()
    {
        if (OperatingSystem.IsWindows())
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "windows",
                _ => throw new InvalidOperationException(
                    $"FTB server installer is not supported on Windows {RuntimeInformation.ProcessArchitecture}.")
            };
        }

        if (OperatingSystem.IsLinux())
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "linux",
                Architecture.Arm64 => "arm/linux",
                _ => throw new InvalidOperationException(
                    $"FTB server installer is not supported on Linux {RuntimeInformation.ProcessArchitecture}.")
            };
        }

        if (OperatingSystem.IsMacOS())
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "macos",
                Architecture.Arm64 => "arm/macos",
                _ => throw new InvalidOperationException(
                    $"FTB server installer is not supported on macOS {RuntimeInformation.ProcessArchitecture}.")
            };
        }

        throw new InvalidOperationException($"FTB server installer is not supported on {RuntimeInformation.OSDescription}.");
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static void EnsureExecutableFileMode(string filePath)
    {
        try
        {
            File.SetUnixFileMode(
                filePath,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute |
                UnixFileMode.GroupRead |
                UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead |
                UnixFileMode.OtherExecute);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Failed to mark FTB server installer as executable: {exception.Message}",
                exception);
        }
    }

    private async Task ExecuteFtbServerInstallerAsync(
        string installerPath,
        int commandId,
        int packId,
        int versionId,
        string installDirectory,
        CancellationToken cancellationToken)
    {
        var logPath = Path.Combine(
            Path.GetDirectoryName(installerPath) ?? AppContext.BaseDirectory,
            "ftb-server-installer.log");
        var startInfo = new ProcessStartInfo
        {
            FileName = installerPath,
            WorkingDirectory = Path.GetDirectoryName(installerPath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        // The official FTB server installer uses Go's standard flag parser.
        // It expects single-dash flags and does not support an "install" subcommand.
        startInfo.ArgumentList.Add("-pack");
        startInfo.ArgumentList.Add(packId.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-version");
        startInfo.ArgumentList.Add(versionId.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-dir");
        startInfo.ArgumentList.Add(installDirectory);
        startInfo.ArgumentList.Add("-auto");
        startInfo.ArgumentList.Add("-force");
        startInfo.ArgumentList.Add("-skip-modloader");
        startInfo.ArgumentList.Add("-no-java");
        startInfo.ArgumentList.Add("-no-colours");

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var logSync = new object();
        using var logWriter = new StreamWriter(
            new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };

        void WriteInstallerLogLine(string streamName, string? line, bool isError)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            var normalizedLine = line.TrimEnd();
            lock (logSync)
            {
                logWriter.WriteLine($"[{DateTime.UtcNow:O}] [{streamName}] {normalizedLine}");
            }

            if (isError)
            {
                _logger.LogWarning(
                    "FTB installer #{CommandId} [{Stream}] {Line}",
                    commandId,
                    streamName,
                    TruncateForLog(normalizedLine, 1200));
            }
            else
            {
                _logger.LogInformation(
                    "FTB installer #{CommandId} [{Stream}] {Line}",
                    commandId,
                    streamName,
                    TruncateForLog(normalizedLine, 1200));
            }
        }

        _logger.LogInformation(
            "Starting FTB server installer for sync_modpack #{CommandId}. PackId={PackId}, VersionId={VersionId}, InstallDirectory={InstallDirectory}, LogPath={LogPath}.",
            commandId,
            packId,
            versionId,
            installDirectory,
            logPath);

        using var process = new Process
        {
            StartInfo = startInfo
        };

        process.OutputDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                stdout.AppendLine(args.Data);
                WriteInstallerLogLine("stdout", args.Data, isError: false);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                stderr.AppendLine(args.Data);
                WriteInstallerLogLine("stderr", args.Data, isError: true);
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start the FTB server installer.");
        }

        lock (logSync)
        {
            logWriter.WriteLine($"[{DateTime.UtcNow:O}] [meta] process started (pid={process.Id})");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            throw;
        }

        if (process.ExitCode != 0)
        {
            var stderrText = TruncateForLog(stderr.ToString().Trim(), 400);
            var stdoutText = TruncateForLog(stdout.ToString().Trim(), 400);
            throw new InvalidOperationException(
                $"FTB server installer exited with code {process.ExitCode}. Log: {logPath}. Error: {stderrText}. Output: {stdoutText}");
        }

        _logger.LogInformation(
            "FTB server installer for sync_modpack #{CommandId} completed successfully. LogPath={LogPath}.",
            commandId,
            logPath);
    }

    private async Task<IReadOnlyList<AutoAmpLoaderConfigSelection>> TryResolveAutoAmpLoaderConfigSelectionsAsync(
        AmpApiRestartPlan plan,
        DetectedMinecraftRuntime runtime,
        CancellationToken cancellationToken)
    {
        if (!IsAutoLoaderKindSupported(runtime.LoaderKind) ||
            string.IsNullOrWhiteSpace(runtime.LoaderVersion))
        {
            return [];
        }

        var settingNodes = await ResolveAmpLoaderSettingNodesAsync(plan, runtime.LoaderKind, cancellationToken);
        if (settingNodes.Count == 0)
        {
            return [];
        }

        var selections = new List<AutoAmpLoaderConfigSelection>();
        foreach (var settingNode in settingNodes)
        {
            var settingValues = await TryGetAmpSettingValuesAsync(plan, settingNode, cancellationToken);
            var candidateValues = ResolveAutoLoaderSettingValues(settingNode, runtime, settingValues);
            foreach (var candidateValue in candidateValues)
            {
                AddAutoAmpLoaderConfigSelection(
                    selections,
                    new AutoAmpLoaderConfigSelection(
                        settingNode,
                        candidateValue,
                        runtime.LoaderKind,
                        runtime.LoaderVersion,
                        runtime.LoaderId));
            }
        }

        return selections;
    }

    private async Task<Dictionary<string, string>> TryResolveAutoAmpStartupConfigValuesAsync(
        AmpApiRestartPlan plan,
        DetectedMinecraftRuntime? runtime,
        CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (runtime is null ||
            !IsAutoLoaderKindSupported(runtime.LoaderKind))
        {
            return values;
        }

        await TryAddAutoAmpStartupConfigValueAsync(
            values,
            plan,
            ["MinecraftModule.Minecraft.ServerType"],
            settingValues => ResolveAmpServerTypeSettingValue(runtime.LoaderKind, settingValues),
            cancellationToken);

        await TryAddAutoAmpStartupConfigValueAsync(
            values,
            plan,
            ["MinecraftModule.Minecraft.ReleaseStream"],
            ResolveAmpReleaseStreamSettingValue,
            cancellationToken);

        await TryAddAutoAmpStartupConfigValueAsync(
            values,
            plan,
            ["MinecraftModule.Minecraft.ServerJAR", "MinecraftModule.Minecraft.ServerJar"],
            ResolveAmpServerJarSettingValue,
            cancellationToken);

        if (values.Count > 0)
        {
            _logger.LogInformation(
                "Resolved AMP auto startup settings for {InstanceName}: {Settings}",
                plan.InstanceName ?? "(direct)",
                string.Join(", ", values.Select(static entry => $"{entry.Key}={entry.Value}")));
        }

        return values;
    }

    private async Task TryAddAutoAmpStartupConfigValueAsync(
        IDictionary<string, string> values,
        AmpApiRestartPlan plan,
        IReadOnlyList<string> settingNodes,
        Func<IReadOnlyList<string>, string?> resolveValue,
        CancellationToken cancellationToken)
    {
        foreach (var settingNode in settingNodes)
        {
            var settingValues = await TryGetAmpSettingValuesAsync(plan, settingNode, cancellationToken);
            var resolvedValue = resolveValue(settingValues);
            if (string.IsNullOrWhiteSpace(resolvedValue))
            {
                continue;
            }

            values[settingNode] = resolvedValue.Trim();
            return;
        }
    }

    private static void AddAutoAmpLoaderConfigSelection(
        ICollection<AutoAmpLoaderConfigSelection> selections,
        AutoAmpLoaderConfigSelection selection)
    {
        if (selections.Any(existing =>
                string.Equals(existing.SettingNode, selection.SettingNode, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.SettingValue, selection.SettingValue, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        selections.Add(selection);
    }

    private async Task<AutoAmpLoaderConfigApplyResult> ApplyAutoAmpLoaderConfigSelectionAsync(
        AmpApiRestartPlan plan,
        IReadOnlyList<AutoAmpLoaderConfigSelection> selections,
        CancellationToken cancellationToken)
    {
        if (selections.Count == 0)
        {
            throw new InvalidOperationException("No AMP loader configuration candidates were available.");
        }

        var errors = new List<string>();
        AutoAmpLoaderConfigSelection? primaryAppliedSelection = null;
        var appliedSettingNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var selectionGroup in selections
                     .GroupBy(selection => selection.SettingNode, StringComparer.OrdinalIgnoreCase))
        {
            var nodeApplied = false;
            var groupErrors = new List<string>();
            var useControllerOnlyStrategies = plan.IsControllerMode;

            foreach (var selection in selectionGroup)
            {
                var strategyErrors = new List<string>();
                var currentValue = await TryGetAmpConfigValueAsync(plan, selection.SettingNode, cancellationToken);

                if (TryResolveEffectiveAmpLoaderSelection(currentValue, selection, out var effectiveSelection))
                {
                    if (!string.Equals(
                            effectiveSelection.SettingValue,
                            selection.SettingValue,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation(
                            "Keeping newer AMP {LoaderKind} version for setting {SettingNode}. Current='{CurrentValue}', Target='{TargetValue}'.",
                            selection.LoaderKind,
                            selection.SettingNode,
                            effectiveSelection.SettingValue,
                            selection.SettingValue);
                    }

                    primaryAppliedSelection ??= effectiveSelection;
                    appliedSettingNodes.Add(selection.SettingNode);
                    nodeApplied = true;
                    break;
                }

                if (plan.IsControllerMode &&
                    await TryApplyAutoAmpLoaderSelectionWithStrategyAsync(
                        plan,
                        selection,
                        strategyName: "ADSModule/ApplyInstanceConfiguration",
                        strategyErrors,
                        () => ExecuteAmpControllerApplySingleConfigAsync(
                            plan,
                            selection.SettingNode,
                            selection.SettingValue,
                            cancellationToken),
                        cancellationToken))
                {
                    primaryAppliedSelection ??= selection;
                    appliedSettingNodes.Add(selection.SettingNode);
                    nodeApplied = true;
                    break;
                }

                if (plan.IsControllerMode &&
                    await TryApplyAutoAmpLoaderSelectionWithStrategyAsync(
                        plan,
                        selection,
                        strategyName: "ADSModule/SetInstanceConfig",
                        strategyErrors,
                        () => ExecuteAmpControllerSetInstanceConfigAsync(
                            plan,
                            selection.SettingNode,
                            selection.SettingValue,
                            cancellationToken),
                        cancellationToken))
                {
                    primaryAppliedSelection ??= selection;
                    appliedSettingNodes.Add(selection.SettingNode);
                    nodeApplied = true;
                    break;
                }

                if (!useControllerOnlyStrategies &&
                    await TryApplyAutoAmpLoaderSelectionWithStrategyAsync(
                        plan,
                        selection,
                        strategyName: "Core/SetConfigs",
                        strategyErrors,
                        () => ExecuteAmpInstanceSetConfigsAsync(
                            plan,
                            selection.SettingNode,
                            selection.SettingValue,
                            cancellationToken),
                        cancellationToken))
                {
                    primaryAppliedSelection ??= selection;
                    appliedSettingNodes.Add(selection.SettingNode);
                    nodeApplied = true;
                    break;
                }

                if (!useControllerOnlyStrategies &&
                    await TryApplyAutoAmpLoaderSelectionWithStrategyAsync(
                        plan,
                        selection,
                        strategyName: "Core/SetConfig",
                        strategyErrors,
                        () => ExecuteAmpInstanceSetConfigAsync(
                            plan,
                            selection.SettingNode,
                            selection.SettingValue,
                            cancellationToken),
                        cancellationToken))
                {
                    primaryAppliedSelection ??= selection;
                    appliedSettingNodes.Add(selection.SettingNode);
                    nodeApplied = true;
                    break;
                }

                groupErrors.Add($"{selection.SettingValue}: {string.Join(" | ", strategyErrors)}");
            }

            if (!nodeApplied)
            {
                errors.Add($"{selectionGroup.Key}: {string.Join(" || ", groupErrors)}");
            }
        }

        if (primaryAppliedSelection is not null)
        {
            _logger.LogInformation(
                "AMP auto loader configuration applied for {AppliedNodeCount} setting node(s): {SettingNodes}",
                appliedSettingNodes.Count,
                string.Join(", ", appliedSettingNodes));
            return new AutoAmpLoaderConfigApplyResult(
                primaryAppliedSelection,
                appliedSettingNodes.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase).ToList());
        }

        throw new InvalidOperationException(
            $"AMP auto loader config could not be verified. {string.Join(" | ", errors)}");
    }

    private async Task<IReadOnlyList<AutoAmpLoaderConfigSelection>> TryRefreshAutoAmpLoaderConfigSelectionsAsync(
        AmpApiRestartPlan plan,
        DetectedMinecraftRuntime? runtime,
        IReadOnlyList<AutoAmpLoaderConfigSelection> existingSelections,
        CancellationToken cancellationToken)
    {
        if (runtime is null)
        {
            return existingSelections;
        }

        try
        {
            var refreshedSelections = await TryResolveAutoAmpLoaderConfigSelectionsAsync(
                plan,
                runtime,
                cancellationToken);
            if (refreshedSelections.Count > 0)
            {
                _logger.LogInformation(
                    "Refreshed AMP loader selections for {InstanceName}: {SettingNodes}",
                    plan.InstanceName ?? "(direct)",
                    string.Join(
                        ", ",
                        refreshedSelections
                            .Select(static selection => selection.SettingNode)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)));
                return refreshedSelections;
            }
        }
        catch (Exception exception)
        {
            _logger.LogDebug(
                exception,
                "Refreshing AMP loader selections failed for instance {InstanceName}; continuing with previous selections.",
                plan.InstanceName ?? "(direct)");
        }

        return existingSelections;
    }

    private async Task<bool> TryApplyAutoAmpLoaderSelectionWithStrategyAsync(
        AmpApiRestartPlan plan,
        AutoAmpLoaderConfigSelection selection,
        string strategyName,
        ICollection<string> strategyErrors,
        Func<Task> applyAsync,
        CancellationToken cancellationToken)
    {
        try
        {
            await applyAsync();

            var appliedValue = await TryGetAmpConfigValueAsync(plan, selection.SettingNode, cancellationToken);
            if (IsAmpLoaderSettingValueMatch(appliedValue, selection))
            {
                return true;
            }

            strategyErrors.Add(
                $"{strategyName} wrote '{selection.SettingValue}' but observed '{appliedValue ?? "(unknown)"}'");
            return false;
        }
        catch (Exception exception)
        {
            strategyErrors.Add($"{strategyName}: {exception.Message}");
            return false;
        }
    }

    private async Task<string?> TryGetAmpConfigValueAsync(
        AmpApiRestartPlan plan,
        string settingNode,
        CancellationToken cancellationToken)
    {
        try
        {
            var configElement = await InvokeAmpInstanceCoreMethodAsync(
                plan,
                module: "Core",
                method: "GetConfig",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["node"] = settingNode
                },
                cancellationToken);

            var configValue = TryExtractAmpConfigValue(configElement, settingNode);
            if (!string.IsNullOrWhiteSpace(configValue))
            {
                return configValue;
            }
        }
        catch (Exception exception)
        {
            _logger.LogDebug(
                exception,
                "AMP GetConfig failed for node {SettingNode}.",
                settingNode);
        }

        try
        {
            var configsElement = await InvokeAmpInstanceCoreMethodAsync(
                plan,
                module: "Core",
                method: "GetConfigs",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["nodes"] = new[] { settingNode }
                },
                cancellationToken);

            return TryExtractAmpConfigValue(configsElement, settingNode);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(
                exception,
                "AMP GetConfigs failed for node {SettingNode}.",
                settingNode);
            return null;
        }
    }

    private async Task RecoverAmpUpdateApplicationByNudgingLoaderVersionAsync(
        AmpApiRestartPlan plan,
        AutoAmpLoaderConfigSelection targetSelection,
        CancellationToken cancellationToken)
    {
        Exception? manifestCacheRecoveryException = null;
        try
        {
            await TryClearAmpMinecraftVersionManifestCacheAsync(plan, cancellationToken);
            await ExecuteAmpLoaderConfigUpdateAsync(
                plan,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [targetSelection.SettingNode] = targetSelection.SettingValue
                },
                cancellationToken);
            await TryRefreshAmpSettingsSourceCacheAsync(plan, cancellationToken);
            await TryRefreshAmpSettingValueListAsync(plan, targetSelection.SettingNode, cancellationToken);
            await ExecuteAmpUpdateApplicationAndWaitAsync(
                plan,
                cancellationToken,
                operationDescription: "manifest cache refresh");
            return;
        }
        catch (Exception exception)
        {
            manifestCacheRecoveryException = exception;
            _logger.LogWarning(
                exception,
                "AMP manifest-cache recovery failed for {InstanceName}. Falling back to loader-version recovery.",
                plan.InstanceName ?? "(direct)");
        }

        var alternateSettingValue = await TryResolveAlternateAmpLoaderSettingValueAsync(
            plan,
            targetSelection,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(alternateSettingValue))
        {
            throw manifestCacheRecoveryException is null
                ? new InvalidOperationException(
                    $"No alternate AMP loader version was available for node '{targetSelection.SettingNode}'.")
                : new InvalidOperationException(
                    $"AMP manifest-cache recovery failed ({manifestCacheRecoveryException.Message}) and no alternate AMP loader version was available for node '{targetSelection.SettingNode}'.",
                    manifestCacheRecoveryException);
        }

        _logger.LogWarning(
            "AMP application update failed for {InstanceName}. Attempting loader-version recovery by switching {SettingNode} to '{AlternateValue}', updating, then restoring '{TargetValue}'.",
            plan.InstanceName ?? "(direct)",
            targetSelection.SettingNode,
            alternateSettingValue,
            targetSelection.SettingValue);

        await ExecuteAmpLoaderConfigUpdateAsync(
            plan,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [targetSelection.SettingNode] = alternateSettingValue
            },
            cancellationToken);
        await ExecuteAmpUpdateApplicationAndWaitAsync(
            plan,
            cancellationToken,
            operationDescription: "loader-version refresh");

        await ExecuteAmpLoaderConfigUpdateAsync(
            plan,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [targetSelection.SettingNode] = targetSelection.SettingValue
            },
            cancellationToken);
        await ExecuteAmpUpdateApplicationAndWaitAsync(
            plan,
            cancellationToken,
            operationDescription: "application update");
    }

    private async Task TryClearAmpMinecraftVersionManifestCacheAsync(
        AmpApiRestartPlan plan,
        CancellationToken cancellationToken)
    {
        var clearedFiles = new List<string>();
        foreach (var fileName in new[] { "ForgeVersionManifest.xml", "NeoForgeVersionManifest.xml" })
        {
            if (await TryTrashAmpInstanceFileAsync(plan, fileName, cancellationToken))
            {
                clearedFiles.Add(fileName);
            }
        }

        if (clearedFiles.Count == 0)
        {
            throw new InvalidOperationException("AMP manifest-cache files were not accessible through FileManagerPlugin.");
        }

        _logger.LogWarning(
            "Cleared AMP manifest-cache file(s) for {InstanceName}: {Files}",
            plan.InstanceName ?? "(direct)",
            string.Join(", ", clearedFiles));
    }

    private async Task<bool> TryTrashAmpInstanceFileAsync(
        AmpApiRestartPlan plan,
        string fileName,
        CancellationToken cancellationToken)
    {
        foreach (var candidatePath in new[] { fileName, $"/{fileName}", $"./{fileName}" })
        {
            try
            {
                await InvokeAmpInstanceCoreMethodAsync(
                    plan,
                    module: "FileManagerPlugin",
                    method: "TrashFile",
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["Filename"] = candidatePath
                    },
                    cancellationToken);
                return true;
            }
            catch (Exception exception)
            {
                _logger.LogDebug(
                    exception,
                    "AMP FileManagerPlugin/TrashFile failed for candidate path {CandidatePath}.",
                    candidatePath);
            }
        }

        return false;
    }

    private async Task<string?> TryResolveAlternateAmpLoaderSettingValueAsync(
        AmpApiRestartPlan plan,
        AutoAmpLoaderConfigSelection targetSelection,
        CancellationToken cancellationToken)
    {
        var settingValues = await TryGetAmpSettingValuesAsync(plan, targetSelection.SettingNode, cancellationToken);
        var targetMinecraftVersion = TryExtractAmpMinecraftVersionFromSettingValue(targetSelection.SettingValue);

        return settingValues
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Where(value => !string.Equals(value, targetSelection.SettingValue, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(value => ScoreAlternateAmpLoaderSettingValue(value, targetMinecraftVersion))
            .FirstOrDefault();
    }

    private static string? TryExtractAmpMinecraftVersionFromSettingValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        const string marker = "(mc ";
        var markerIndex = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return null;
        }

        var versionStart = markerIndex + marker.Length;
        var versionEnd = value.IndexOf(')', versionStart);
        if (versionEnd <= versionStart)
        {
            return null;
        }

        var minecraftVersion = value[versionStart..versionEnd].Trim();
        return string.IsNullOrWhiteSpace(minecraftVersion)
            ? null
            : minecraftVersion;
    }

    private static int ScoreAlternateAmpLoaderSettingValue(string value, string? targetMinecraftVersion)
    {
        var score = 0;
        if (ContainsInvariant(value, "forge"))
        {
            score += 50;
        }

        var candidateMinecraftVersion = TryExtractAmpMinecraftVersionFromSettingValue(value);
        if (!string.IsNullOrWhiteSpace(candidateMinecraftVersion))
        {
            score += 25;
            if (!string.IsNullOrWhiteSpace(targetMinecraftVersion) &&
                !string.Equals(candidateMinecraftVersion, targetMinecraftVersion, StringComparison.OrdinalIgnoreCase))
            {
                score += 500;
            }
        }

        return score;
    }

    private async Task ExecuteAmpControllerSetInstanceConfigAsync(
        AmpApiRestartPlan plan,
        string settingNode,
        string settingValue,
        CancellationToken cancellationToken)
    {
        if (!plan.IsControllerMode)
        {
            throw new InvalidOperationException("AMP controller SetInstanceConfig requires controller mode.");
        }

        var instanceReference = await TryResolveAmpControllerInstanceReferenceAsync(plan, cancellationToken);
        var instanceName = instanceReference?.InstanceName ?? plan.InstanceName;
        if (string.IsNullOrWhiteSpace(instanceName))
        {
            throw new InvalidOperationException("AMP instance name is required for SetInstanceConfig.");
        }

        await ExecuteAmpApiMethodAsync(
            plan,
            module: "ADSModule",
            method: "SetInstanceConfig",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["InstanceName"] = instanceName,
                ["SettingNode"] = settingNode,
                ["Value"] = settingValue
            },
            cancellationToken);

        await RefreshAmpControllerInstanceConfigAsync(plan, instanceReference, cancellationToken);
        await WaitForAmpInstanceApiAvailabilityAsync(plan, cancellationToken);
    }

    private async Task ExecuteAmpControllerApplySingleConfigAsync(
        AmpApiRestartPlan plan,
        string settingNode,
        string settingValue,
        CancellationToken cancellationToken)
    {
        await ExecuteAmpControllerConfigUpdateAsync(
            plan,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [settingNode] = settingValue
            },
            cancellationToken);
        await WaitForAmpInstanceApiAvailabilityAsync(plan, cancellationToken);
    }

    private async Task ExecuteAmpInstanceSetConfigsAsync(
        AmpApiRestartPlan plan,
        string settingNode,
        string settingValue,
        CancellationToken cancellationToken)
    {
        await InvokeAmpInstanceCoreMethodAsync(
            plan,
            module: "Core",
            method: "SetConfigs",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["data"] = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [settingNode] = settingValue
                }
            },
            cancellationToken);
    }

    private async Task ExecuteAmpInstanceSetConfigAsync(
        AmpApiRestartPlan plan,
        string settingNode,
        string settingValue,
        CancellationToken cancellationToken)
    {
        await InvokeAmpInstanceCoreMethodAsync(
            plan,
            module: "Core",
            method: "SetConfig",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["node"] = settingNode,
                ["value"] = settingValue
            },
            cancellationToken);
    }

    private static string? TryExtractAmpConfigValue(JsonElement element, string settingNode)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return Normalize(element.GetString());
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var itemValue = TryExtractAmpConfigValue(item, settingNode);
                if (!string.IsNullOrWhiteSpace(itemValue))
                {
                    return itemValue;
                }
            }

            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (TryGetPropertyIgnoreCase(element, settingNode, out var nodeValue))
        {
            var exactNodeValue = ReadJsonString(nodeValue);
            if (!string.IsNullOrWhiteSpace(exactNodeValue))
            {
                return exactNodeValue.Trim();
            }
        }

        foreach (var propertyName in new[]
                 {
                     "Value",
                     "value",
                     "CurrentValue",
                     "currentValue",
                     "SettingValue",
                     "settingValue",
                     "Result",
                     "result",
                     "Data",
                     "data"
                 })
        {
            if (TryGetPropertyIgnoreCase(element, propertyName, out var propertyValue))
            {
                var resolvedValue = ReadJsonString(propertyValue);
                if (!string.IsNullOrWhiteSpace(resolvedValue))
                {
                    return resolvedValue.Trim();
                }
            }
        }

        var nestedValue = TryFindStringByPropertyNames(
            element,
            "CurrentValue",
            "currentValue",
            "SettingValue",
            "settingValue",
            "SelectedValue",
            "selectedValue",
            "Value",
            "value");
        if (!string.IsNullOrWhiteSpace(nestedValue))
        {
            return nestedValue.Trim();
        }

        JsonProperty? singleProperty = null;
        foreach (var property in element.EnumerateObject())
        {
            if (singleProperty is not null)
            {
                return null;
            }

            singleProperty = property;
        }

        if (singleProperty is not null)
        {
            var singleValue = ReadJsonString(singleProperty.Value.Value);
            if (!string.IsNullOrWhiteSpace(singleValue))
            {
                return singleValue.Trim();
            }
        }

        return null;
    }

    private static bool IsAmpLoaderSettingValueMatch(
        string? currentValue,
        AutoAmpLoaderConfigSelection selection)
    {
        var normalizedCurrentValue = Normalize(currentValue);
        if (string.IsNullOrWhiteSpace(normalizedCurrentValue))
        {
            return false;
        }

        if (string.Equals(normalizedCurrentValue, selection.SettingValue, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedCurrentValue, selection.LoaderVersion, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedCurrentValue, selection.LoaderId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (TryCompareAmpLoaderSettingValueVersion(normalizedCurrentValue, selection, out var comparison) &&
            comparison >= 0)
        {
            return true;
        }

        return ContainsInvariant(normalizedCurrentValue, selection.LoaderVersion) &&
               (ContainsInvariant(normalizedCurrentValue, selection.LoaderKind) ||
                ContainsInvariant(normalizedCurrentValue, "forge"));
    }

    private static bool TryResolveEffectiveAmpLoaderSelection(
        string? currentValue,
        AutoAmpLoaderConfigSelection selection,
        out AutoAmpLoaderConfigSelection effectiveSelection)
    {
        effectiveSelection = selection;

        var normalizedCurrentValue = Normalize(currentValue);
        if (string.IsNullOrWhiteSpace(normalizedCurrentValue))
        {
            return false;
        }

        if (string.Equals(normalizedCurrentValue, selection.SettingValue, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedCurrentValue, selection.LoaderVersion, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedCurrentValue, selection.LoaderId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!TryCompareAmpLoaderSettingValueVersion(normalizedCurrentValue, selection, out var comparison) ||
            comparison <= 0)
        {
            return false;
        }

        var resolvedLoaderVersion = TryResolveComparableAmpLoaderVersionString(normalizedCurrentValue, selection)
            ?? selection.LoaderVersion;
        effectiveSelection = new AutoAmpLoaderConfigSelection(
            selection.SettingNode,
            normalizedCurrentValue,
            selection.LoaderKind,
            resolvedLoaderVersion,
            selection.LoaderId);
        return true;
    }

    private static bool TryCompareAmpLoaderSettingValueVersion(
        string? currentValue,
        AutoAmpLoaderConfigSelection selection,
        out int comparison)
    {
        comparison = 0;

        if (string.IsNullOrWhiteSpace(currentValue) ||
            string.IsNullOrWhiteSpace(selection.LoaderVersion) ||
            !TryParseVersionParts(selection.LoaderVersion, out var targetVersionParts) ||
            !TryResolveComparableAmpLoaderVersionParts(currentValue, selection, out var currentVersionParts))
        {
            return false;
        }

        var targetMinecraftVersion = TryExtractAmpMinecraftVersionFromSettingValue(selection.SettingValue);
        var currentMinecraftVersion = TryExtractAmpMinecraftVersionFromSettingValue(currentValue);
        if (!IsComparableAmpLoaderBranch(
                selection.LoaderKind,
                currentVersionParts,
                targetVersionParts,
                currentMinecraftVersion,
                targetMinecraftVersion))
        {
            return false;
        }

        comparison = CompareVersionParts(currentVersionParts, targetVersionParts);
        return true;
    }

    private static string? TryResolveComparableAmpLoaderVersionString(
        string currentValue,
        AutoAmpLoaderConfigSelection selection)
    {
        if (TryParseVersionParts(currentValue, out _))
        {
            return currentValue;
        }

        if (string.IsNullOrWhiteSpace(selection.LoaderVersion))
        {
            return null;
        }

        if (!TryParseVersionParts(selection.LoaderVersion, out var targetVersionParts))
        {
            return null;
        }

        string? bestCandidate = null;
        var bestScore = int.MinValue;
        foreach (Match match in HumanVersionRegex.Matches(currentValue))
        {
            if (!TryParseVersionParts(match.Value, out var candidateParts))
            {
                continue;
            }

            var score = ScoreComparableAmpLoaderVersionCandidate(
                candidateParts,
                targetVersionParts,
                selection.LoaderKind);
            if (score <= bestScore)
            {
                continue;
            }

            bestScore = score;
            bestCandidate = match.Value;
        }

        return bestScore > 0 ? bestCandidate : null;
    }

    private static bool TryResolveComparableAmpLoaderVersionParts(
        string currentValue,
        AutoAmpLoaderConfigSelection selection,
        out int[] versionParts)
    {
        versionParts = [];

        if (TryParseVersionParts(currentValue, out var directParts))
        {
            versionParts = directParts;
            return true;
        }

        if (string.IsNullOrWhiteSpace(selection.LoaderVersion) ||
            !TryParseVersionParts(selection.LoaderVersion, out var targetVersionParts))
        {
            return false;
        }

        var bestScore = int.MinValue;
        int[]? bestParts = null;
        foreach (Match match in HumanVersionRegex.Matches(currentValue))
        {
            if (!TryParseVersionParts(match.Value, out var candidateParts))
            {
                continue;
            }

            var score = ScoreComparableAmpLoaderVersionCandidate(
                candidateParts,
                targetVersionParts,
                selection.LoaderKind);
            if (score <= bestScore)
            {
                continue;
            }

            bestScore = score;
            bestParts = candidateParts;
        }

        if (bestParts is null || bestScore <= 0)
        {
            return false;
        }

        versionParts = bestParts;
        return true;
    }

    private static int ScoreComparableAmpLoaderVersionCandidate(
        IReadOnlyList<int> candidateParts,
        IReadOnlyList<int> targetParts,
        string loaderKind)
    {
        if (candidateParts.Count == 0 || targetParts.Count == 0)
        {
            return int.MinValue;
        }

        var score = 0;
        var sharedPrefix = 0;
        while (sharedPrefix < candidateParts.Count &&
               sharedPrefix < targetParts.Count &&
               candidateParts[sharedPrefix] == targetParts[sharedPrefix])
        {
            sharedPrefix++;
        }

        score += sharedPrefix * 100;

        if (string.Equals(loaderKind, "neoforge", StringComparison.OrdinalIgnoreCase))
        {
            if (candidateParts.Count >= 2 &&
                targetParts.Count >= 2 &&
                candidateParts[0] == targetParts[0] &&
                candidateParts[1] == targetParts[1])
            {
                score += 500;
            }
        }
        else if (string.Equals(loaderKind, "forge", StringComparison.OrdinalIgnoreCase) &&
                 candidateParts[0] == targetParts[0])
        {
            score += 400;
        }

        if (candidateParts[0] == targetParts[0])
        {
            score += 50;
        }

        return score;
    }

    private static bool IsComparableAmpLoaderBranch(
        string loaderKind,
        IReadOnlyList<int> currentParts,
        IReadOnlyList<int> targetParts,
        string? currentMinecraftVersion,
        string? targetMinecraftVersion)
    {
        if (!string.IsNullOrWhiteSpace(currentMinecraftVersion) &&
            !string.IsNullOrWhiteSpace(targetMinecraftVersion))
        {
            return string.Equals(
                currentMinecraftVersion,
                targetMinecraftVersion,
                StringComparison.OrdinalIgnoreCase);
        }

        if (currentParts.Count == 0 || targetParts.Count == 0)
        {
            return false;
        }

        if (string.Equals(loaderKind, "neoforge", StringComparison.OrdinalIgnoreCase))
        {
            return currentParts.Count >= 2 &&
                   targetParts.Count >= 2 &&
                   currentParts[0] == targetParts[0] &&
                   currentParts[1] == targetParts[1];
        }

        if (string.Equals(loaderKind, "forge", StringComparison.OrdinalIgnoreCase))
        {
            return currentParts[0] == targetParts[0];
        }

        return false;
    }

    private static bool TryParseVersionParts(string? value, out int[] versionParts)
    {
        versionParts = [];
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var segments = value.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            return false;
        }

        var parsedParts = new List<int>(segments.Length);
        foreach (var segment in segments)
        {
            if (!int.TryParse(segment, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPart))
            {
                return false;
            }

            parsedParts.Add(parsedPart);
        }

        versionParts = parsedParts.ToArray();
        return true;
    }

    private static int CompareVersionParts(
        IReadOnlyList<int> left,
        IReadOnlyList<int> right)
    {
        var maxLength = Math.Max(left.Count, right.Count);
        for (var index = 0; index < maxLength; index++)
        {
            var leftPart = index < left.Count ? left[index] : 0;
            var rightPart = index < right.Count ? right[index] : 0;
            if (leftPart == rightPart)
            {
                continue;
            }

            return leftPart.CompareTo(rightPart);
        }

        return 0;
    }

    private async Task<IReadOnlyList<string>> ResolveAmpLoaderSettingNodesAsync(
        AmpApiRestartPlan plan,
        string loaderKind,
        CancellationToken cancellationToken)
    {
        var preferredNodes = GetFallbackLoaderSettingNodes(loaderKind);
        if (preferredNodes.Count == 0)
        {
            return [];
        }

        try
        {
            await TryRefreshAmpSettingsSourceCacheAsync(plan, cancellationToken);
            var settingsSpec = await InvokeAmpInstanceCoreMethodAsync(
                plan,
                module: "Core",
                "GetSettingsSpec",
                parameters: new Dictionary<string, object?>(StringComparer.Ordinal),
                cancellationToken);

            var discoveredValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectJsonStringTokens(settingsSpec, discoveredValues);

            var discoveredNodes = discoveredValues
                .Where(LooksLikeAmpSettingNode)
                .Select(static value => value.Trim())
                .Where(value => IsAutoLoaderSettingNodeCandidate(value, loaderKind))
                .OrderByDescending(value => ScoreLoaderSettingNode(value, loaderKind))
                .ToList();

            var combinedNodes = preferredNodes
                .Concat(discoveredNodes)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(value => ScoreLoaderSettingNode(value, loaderKind))
                .ToList();

            if (combinedNodes.Count > 0)
            {
                return combinedNodes;
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Could not inspect AMP settings spec for loader node discovery.");
        }

        return preferredNodes;
    }

    private static bool IsAutoLoaderSettingNodeCandidate(string settingNode, string loaderKind)
    {
        if (string.IsNullOrWhiteSpace(settingNode) ||
            !settingNode.StartsWith("MinecraftModule.Minecraft.", StringComparison.OrdinalIgnoreCase) ||
            !ContainsInvariant(settingNode, "version"))
        {
            return false;
        }

        if (string.Equals(loaderKind, "neoforge", StringComparison.OrdinalIgnoreCase))
        {
            return ContainsInvariant(settingNode, "neoforge") ||
                   string.Equals(
                       settingNode,
                       "MinecraftModule.Minecraft.ForgeVersion",
                       StringComparison.OrdinalIgnoreCase);
        }

        return ContainsInvariant(settingNode, "forge") &&
               !ContainsInvariant(settingNode, "neoforge");
    }

    private async Task<IReadOnlyList<string>> TryGetAmpSettingValuesAsync(
        AmpApiRestartPlan plan,
        string settingNode,
        CancellationToken cancellationToken)
    {
        try
        {
            await TryRefreshAmpSettingValueListAsync(plan, settingNode, cancellationToken);
            var valuesDocument = await InvokeAmpInstanceCoreMethodAsync(
                plan,
                module: "Core",
                "GetSettingValues",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["SettingNode"] = settingNode,
                    ["WithRefresh"] = true
                },
                cancellationToken);

            var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectJsonStringTokens(valuesDocument, values);
            if (values.Count == 0)
            {
                await TryRefreshAmpSettingsSourceCacheAsync(plan, cancellationToken);
                await TryRefreshAmpSettingValueListAsync(plan, settingNode, cancellationToken);
                var refreshedValuesDocument = await InvokeAmpInstanceCoreMethodAsync(
                    plan,
                    module: "Core",
                    "GetSettingValues",
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["SettingNode"] = settingNode,
                        ["WithRefresh"] = true
                    },
                    cancellationToken);
                CollectJsonStringTokens(refreshedValuesDocument, values);
            }

            return values
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim())
                .ToList();
        }
        catch (Exception exception)
        {
            _logger.LogDebug(
                exception,
                "GetSettingValues failed for AMP node {SettingNode}.",
                settingNode);
            return [];
        }
    }

    private async Task TryRefreshAmpSettingsSourceCacheAsync(
        AmpApiRestartPlan plan,
        CancellationToken cancellationToken)
    {
        try
        {
            await InvokeAmpInstanceCoreMethodAsync(
                plan,
                module: "Core",
                method: "RefreshSettingsSourceCache",
                parameters: new Dictionary<string, object?>(StringComparer.Ordinal),
                cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(
                exception,
                "AMP RefreshSettingsSourceCache failed for instance {InstanceName}.",
                plan.InstanceName ?? "(direct)");
        }
    }

    private async Task TryRefreshAmpSettingValueListAsync(
        AmpApiRestartPlan plan,
        string settingNode,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settingNode))
        {
            return;
        }

        try
        {
            await InvokeAmpInstanceCoreMethodAsync(
                plan,
                module: "Core",
                method: "RefreshSettingValueList",
                parameters: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Node"] = settingNode
                },
                cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(
                exception,
                "AMP RefreshSettingValueList failed for node {SettingNode}.",
                settingNode);
        }
    }

    private async Task<JsonElement> InvokeAmpInstanceCoreMethodAsync(
        AmpApiRestartPlan plan,
        string module,
        string method,
        IDictionary<string, object?> parameters,
        CancellationToken cancellationToken)
    {
        if (plan.IsControllerMode)
        {
            var proxyKeys = await ResolveAmpProxyServerKeysAsync(plan, cancellationToken);
            var proxyErrors = new List<string>();
            foreach (var proxyKey in proxyKeys)
            {
                try
                {
                    var proxySessionId = await EnsureAmpProxySessionIdAsync(plan, proxyKey, cancellationToken);
                    var proxyResult = await ExecuteAmpProxyMethodWithResultAsync(
                        plan,
                        proxyKey,
                        module,
                        method,
                        parameters,
                        proxySessionId,
                        cancellationToken);
                    return UnwrapAmpApiResultElement(proxyResult);
                }
                catch (Exception exception)
                {
                    proxyErrors.Add($"{proxyKey}: {exception.Message}");
                    _logger.LogDebug(
                        exception,
                        "Proxy instance method failed via key {ProxyKey} for {Module}/{Method}.",
                        proxyKey,
                        module,
                        method);
                }
            }

            var controllerSessionId = await EnsureAmpApiSessionIdAsync(plan, cancellationToken);
            foreach (var proxyKey in proxyKeys)
            {
                try
                {
                    var proxyResult = await ExecuteAmpProxyMethodWithResultAsync(
                        plan,
                        proxyKey,
                        module,
                        method,
                        parameters,
                        controllerSessionId,
                        cancellationToken);
                    return UnwrapAmpApiResultElement(proxyResult);
                }
                catch (Exception exception)
                {
                    proxyErrors.Add($"{proxyKey} (controller-session): {exception.Message}");
                    _logger.LogDebug(
                        exception,
                        "Proxy instance method failed via controller session and key {ProxyKey} for {Module}/{Method}.",
                        proxyKey,
                        module,
                        method);
                }
            }

            var proxyErrorSummary = proxyErrors.Count == 0
                ? "No proxy keys were available."
                : string.Join(" | ", proxyErrors);
            throw new InvalidOperationException(
                $"AMP instance API {module}/{method} failed via proxy ({proxyErrorSummary}).");
        }

        var directResult = await ExecuteAmpApiMethodWithResultAsync(
            plan,
            module,
            method,
            parameters,
            cancellationToken);
        return UnwrapAmpApiResultElement(directResult);
    }

    private static JsonElement UnwrapAmpApiResultElement(JsonElement rootElement)
    {
        if (rootElement.ValueKind != JsonValueKind.Object)
        {
            return rootElement.Clone();
        }

        if (TryGetPropertyIgnoreCase(rootElement, "result", out var resultElement))
        {
            if (resultElement.ValueKind == JsonValueKind.String)
            {
                var rawResult = resultElement.GetString();
                if (!string.IsNullOrWhiteSpace(rawResult) &&
                    TryParseEmbeddedJson(rawResult, out var parsedResult))
                {
                    return parsedResult;
                }
            }

            return resultElement.Clone();
        }

        if (TryGetPropertyIgnoreCase(rootElement, "data", out var dataElement))
        {
            if (dataElement.ValueKind == JsonValueKind.String)
            {
                var rawData = dataElement.GetString();
                if (!string.IsNullOrWhiteSpace(rawData) &&
                    TryParseEmbeddedJson(rawData, out var parsedData))
                {
                    return parsedData;
                }
            }

            return dataElement.Clone();
        }

        return rootElement.Clone();
    }

    private static bool TryParseEmbeddedJson(string value, out JsonElement parsedElement)
    {
        parsedElement = default;
        var trimmed = value.Trim();
        if (trimmed.Length < 2 ||
            (trimmed[0] is not '{' and not '['))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            parsedElement = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<JsonElement> ExecuteAmpApiMethodWithResultAsync(
        AmpApiRestartPlan plan,
        string module,
        string method,
        IDictionary<string, object?> parameters,
        CancellationToken cancellationToken)
    {
        var sessionId = await EnsureAmpApiSessionIdAsync(plan, cancellationToken);
        var payload = new Dictionary<string, object?>(parameters, StringComparer.Ordinal)
        {
            ["SESSIONID"] = sessionId
        };

        using var responseDocument = await PostAmpApiRequestAsync(
            plan.ApiBaseUrl,
            module,
            method,
            payload,
            cancellationToken);

        EnsureAmpApiResponseSucceeded(responseDocument.RootElement, module, method);
        return responseDocument.RootElement.Clone();
    }

    private async Task<IReadOnlyList<string>> ResolveAmpProxyServerKeysAsync(
        AmpApiRestartPlan plan,
        CancellationToken cancellationToken)
    {
        if (!plan.IsControllerMode)
        {
            return [];
        }

        var keys = new List<string>();
        var instanceReference = await TryResolveAmpControllerInstanceReferenceAsync(plan, cancellationToken);
        AddUniqueProxyKey(keys, instanceReference?.InstanceId);
        AddUniqueProxyKey(keys, instanceReference?.InstanceName);
        AddUniqueProxyKey(keys, instanceReference?.FriendlyName);
        AddUniqueProxyKey(keys, plan.InstanceName);

        return keys;
    }

    private static void AddUniqueProxyKey(ICollection<string> keys, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return;
        }

        if (keys.Any(existing => string.Equals(existing, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        keys.Add(candidate.Trim());
    }

    private async Task<JsonElement> ExecuteAmpProxyMethodWithResultAsync(
        AmpApiRestartPlan plan,
        string proxyServerKey,
        string module,
        string method,
        IDictionary<string, object?> parameters,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>(parameters, StringComparer.Ordinal)
        {
            ["SESSIONID"] = sessionId
        };

        using var responseDocument = await PostAmpProxyApiRequestAsync(
            plan.ApiBaseUrl,
            proxyServerKey,
            module,
            method,
            payload,
            cancellationToken);

        EnsureAmpApiResponseSucceeded(
            responseDocument.RootElement,
            $"ADSModule/Servers/{proxyServerKey}/API/{module}",
            method);
        return responseDocument.RootElement.Clone();
    }

    private async Task<string> EnsureAmpProxySessionIdAsync(
        AmpApiRestartPlan plan,
        string proxyServerKey,
        CancellationToken cancellationToken)
    {
        if (plan.ProxySessionIds.TryGetValue(proxyServerKey, out var proxySessionId) &&
            !string.IsNullOrWhiteSpace(proxySessionId))
        {
            return proxySessionId;
        }

        using var responseDocument = await PostAmpProxyApiRequestAsync(
            plan.ApiBaseUrl,
            proxyServerKey,
            module: "Core",
            method: "Login",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["username"] = plan.Username,
                ["password"] = plan.Password,
                ["token"] = plan.Token,
                ["rememberMe"] = plan.RememberMe
            },
            cancellationToken);

        EnsureAmpApiResponseSucceeded(
            responseDocument.RootElement,
            $"ADSModule/Servers/{proxyServerKey}/API/Core",
            "Login");

        proxySessionId = TryFindStringByPropertyNames(
            responseDocument.RootElement,
            "sessionID",
            "sessionId",
            "session_id",
            "SESSIONID");
        proxySessionId ??= TryExtractAmpSessionId(responseDocument.RootElement);
        if (string.IsNullOrWhiteSpace(proxySessionId))
        {
            throw new InvalidOperationException(
                $"AMP proxy login succeeded but no session ID was returned for '{proxyServerKey}'.");
        }

        plan.ProxySessionIds[proxyServerKey] = proxySessionId;
        return proxySessionId;
    }

    private async Task<JsonDocument> PostAmpProxyApiRequestAsync(
        string ampApiBaseUrl,
        string proxyServerKey,
        string module,
        string method,
        IDictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        var encodedProxyKey = Uri.EscapeDataString(proxyServerKey);
        var requestUrl = $"{ampApiBaseUrl}/API/ADSModule/Servers/{encodedProxyKey}/API/{module}/{method}";
        using var client = _httpClientFactory.CreateClient(AmpApiHttpClientName);
        if (!client.DefaultRequestHeaders.Accept.Any())
        {
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.cubecoders-ampapi"));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUrl);
        if (TryGetAmpRequestSessionId(payload, out var sessionId))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sessionId);
        }

        request.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");
        using var response = await client.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var responseSummary = string.IsNullOrWhiteSpace(responseBody)
                ? $"HTTP {(int)response.StatusCode}"
                : TruncateForLog(responseBody.Trim(), 500);
            throw new InvalidOperationException(
                $"AMP API call ADSModule/Servers/{proxyServerKey}/API/{module}/{method} failed: {responseSummary}");
        }

        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return JsonDocument.Parse("{}");
        }

        return JsonDocument.Parse(responseBody);
    }

    private static bool IsAutoLoaderKindSupported(string loaderKind)
    {
        return string.Equals(loaderKind, "forge", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(loaderKind, "neoforge", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> GetFallbackLoaderSettingNodes(string loaderKind)
    {
        if (string.Equals(loaderKind, "neoforge", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                "MinecraftModule.Minecraft.SpecificNeoForgeVersion",
                "MinecraftModule.Minecraft.ForgeVersion"
            ];
        }

        return
        [
            "MinecraftModule.Minecraft.SpecificForgeVersion",
            "MinecraftModule.Minecraft.ForgeVersion"
        ];
    }

    private static int ScoreLoaderSettingNode(string settingNode, string loaderKind)
    {
        var score = 0;
        if (settingNode.StartsWith("MinecraftModule.Minecraft.", StringComparison.OrdinalIgnoreCase))
        {
            score += 100;
        }

        if (ContainsInvariant(settingNode, "version"))
        {
            score += 50;
        }

        if (ContainsInvariant(settingNode, "loader"))
        {
            score += 20;
        }

        if (string.Equals(loaderKind, "neoforge", StringComparison.OrdinalIgnoreCase))
        {
            if (ContainsInvariant(settingNode, "neoforge"))
            {
                score += 250;
            }

            if (string.Equals(
                    settingNode,
                    "MinecraftModule.Minecraft.ForgeVersion",
                    StringComparison.OrdinalIgnoreCase))
            {
                score += 150;
            }
        }
        else if (string.Equals(loaderKind, "forge", StringComparison.OrdinalIgnoreCase))
        {
            if (ContainsInvariant(settingNode, "forge"))
            {
                score += 220;
            }

            if (ContainsInvariant(settingNode, "neoforge"))
            {
                score -= 120;
            }
        }

        return score;
    }

    private static bool LooksLikeAmpSettingNode(string value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.Length <= 250 &&
               value.Contains('.', StringComparison.Ordinal) &&
               !value.Contains(' ', StringComparison.Ordinal) &&
               !value.Contains('/', StringComparison.Ordinal) &&
               !value.Contains('\\', StringComparison.Ordinal);
    }

    private static void CollectJsonStringTokens(JsonElement element, ISet<string> values)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (!string.IsNullOrWhiteSpace(property.Name))
                    {
                        values.Add(property.Name);
                    }

                    CollectJsonStringTokens(property.Value, values);
                }

                break;
            }
            case JsonValueKind.Array:
            {
                foreach (var item in element.EnumerateArray())
                {
                    CollectJsonStringTokens(item, values);
                }

                break;
            }
            case JsonValueKind.String:
            {
                var value = element.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    values.Add(value);
                }

                break;
            }
        }
    }

    private static IReadOnlyList<string> ResolveAutoLoaderSettingValues(
        string settingNode,
        DetectedMinecraftRuntime runtime,
        IReadOnlyList<string> settingValues)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(runtime.LoaderVersion))
        {
            var exactVersion = settingValues.FirstOrDefault(value =>
                string.Equals(value, runtime.LoaderVersion, StringComparison.OrdinalIgnoreCase));
            if (exactVersion is not null)
            {
                AddCandidateValue(candidates, exactVersion);
            }
        }

        if (candidates.Count == 0)
        {
            foreach (var matchedValue in settingValues.Where(value => IsAmpLoaderSettingValueCandidate(value, runtime)))
            {
                AddCandidateValue(candidates, matchedValue);
            }
        }

        if (candidates.Count == 0 &&
            IsSpecificAmpLoaderSettingNode(settingNode))
        {
            AddCandidateValue(candidates, runtime.LoaderVersion);
            return candidates;
        }

        AddCandidateValue(candidates, runtime.LoaderVersion);
        return candidates;
    }

    private static bool IsAmpLoaderSettingValueCandidate(string? value, DetectedMinecraftRuntime runtime)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            string.IsNullOrWhiteSpace(runtime.LoaderVersion))
        {
            return false;
        }

        if (!ContainsInvariant(value, runtime.LoaderVersion))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(runtime.MinecraftVersion) &&
            ContainsInvariant(value, runtime.MinecraftVersion))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(runtime.LoaderId) &&
            ContainsInvariant(value, runtime.LoaderId))
        {
            return true;
        }

        return ContainsInvariant(value, runtime.LoaderKind);
    }

    private static bool IsSpecificAmpLoaderSettingNode(string settingNode)
    {
        return string.Equals(
                   settingNode,
                   "MinecraftModule.Minecraft.SpecificNeoForgeVersion",
                   StringComparison.OrdinalIgnoreCase) ||
               string.Equals(
                   settingNode,
                   "MinecraftModule.Minecraft.SpecificForgeVersion",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static void AddCandidateValue(ICollection<string> values, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var normalizedValue = value.Trim();
        if (values.Any(existing => string.Equals(existing, normalizedValue, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        values.Add(normalizedValue);
    }

    private static string? ResolveAmpServerTypeSettingValue(
        string loaderKind,
        IReadOnlyList<string> settingValues)
    {
        var expectedServerType = string.Equals(loaderKind, "neoforge", StringComparison.OrdinalIgnoreCase)
            ? "NeoForge"
            : "Forge";

        var match = settingValues
            .Where(value => IsAmpServerTypeSettingValueCandidate(value, loaderKind))
            .OrderByDescending(value => ScoreAmpServerTypeSettingValue(value, loaderKind))
            .FirstOrDefault();

        return !string.IsNullOrWhiteSpace(match)
            ? match.Trim()
            : expectedServerType;
    }

    private static bool IsAmpServerTypeSettingValueCandidate(string? value, string loaderKind)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (string.Equals(loaderKind, "neoforge", StringComparison.OrdinalIgnoreCase))
        {
            return ContainsInvariant(value, "neoforge");
        }

        return ContainsInvariant(value, "forge") &&
               !ContainsInvariant(value, "neoforge");
    }

    private static int ScoreAmpServerTypeSettingValue(string? value, string loaderKind)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return int.MinValue;
        }

        var score = 0;
        if (string.Equals(loaderKind, "neoforge", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(value.Trim(), "NeoForge", StringComparison.OrdinalIgnoreCase))
            {
                score += 200;
            }

            if (ContainsInvariant(value, "neoforge"))
            {
                score += 100;
            }
        }
        else
        {
            if (string.Equals(value.Trim(), "Forge", StringComparison.OrdinalIgnoreCase))
            {
                score += 200;
            }

            if (ContainsInvariant(value, "forge"))
            {
                score += 100;
            }

            if (ContainsInvariant(value, "neoforge"))
            {
                score -= 150;
            }
        }

        return score;
    }

    private static string ResolveAmpReleaseStreamSettingValue(IReadOnlyList<string> settingValues)
    {
        var match = settingValues
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .OrderByDescending(ScoreAmpReleaseStreamSettingValue)
            .FirstOrDefault(static value => ScoreAmpReleaseStreamSettingValue(value) > 0);

        return !string.IsNullOrWhiteSpace(match)
            ? match.Trim()
            : "SpecificVersion";
    }

    private static int ScoreAmpReleaseStreamSettingValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return int.MinValue;
        }

        var normalized = NormalizeAlphaNumeric(value);
        if (string.Equals(normalized, "specificversion", StringComparison.OrdinalIgnoreCase))
        {
            return 300;
        }

        if (ContainsInvariant(value, "specific") &&
            ContainsInvariant(value, "version"))
        {
            return 200;
        }

        return 0;
    }

    private static string ResolveAmpServerJarSettingValue(IReadOnlyList<string> settingValues)
    {
        var match = settingValues
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .OrderByDescending(ScoreAmpServerJarSettingValue)
            .FirstOrDefault(static value => ScoreAmpServerJarSettingValue(value) > 0);

        return !string.IsNullOrWhiteSpace(match)
            ? match.Trim()
            : "[Autoselect]";
    }

    private static int ScoreAmpServerJarSettingValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return int.MinValue;
        }

        var score = 0;
        if (ContainsInvariant(value, "autoselect"))
        {
            score += 250;
        }

        if (value.Contains('[', StringComparison.Ordinal) &&
            value.Contains(']', StringComparison.Ordinal))
        {
            score += 50;
        }

        if (ContainsInvariant(value, "automatic"))
        {
            score += 25;
        }

        return score;
    }

    private static bool TryParseMinecraftVersion(
        string? minecraftVersion,
        out int major,
        out int minor,
        out int patch)
    {
        major = 0;
        minor = 0;
        patch = 0;

        if (string.IsNullOrWhiteSpace(minecraftVersion))
        {
            return false;
        }

        var parts = minecraftVersion
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2 ||
            !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out major) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out minor))
        {
            return false;
        }

        if (parts.Length >= 3)
        {
            var patchToken = new string(parts[2].TakeWhile(char.IsDigit).ToArray());
            if (!string.IsNullOrWhiteSpace(patchToken) &&
                int.TryParse(patchToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPatch))
            {
                patch = parsedPatch;
            }
        }

        return true;
    }

    private static string NormalizeAlphaNumeric(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private async Task<CurseForgeFileEntry> ResolveCurseForgeParentFileAsync(
        int projectId,
        string? selectedVersion,
        CancellationToken cancellationToken)
    {
        var publishedFiles = await GetCurseForgePublishedFilesAsync(projectId, cancellationToken);

        if (!string.IsNullOrWhiteSpace(selectedVersion))
        {
            var normalizedSelector = selectedVersion.Trim();

            if (int.TryParse(normalizedSelector, NumberStyles.Integer, CultureInfo.InvariantCulture, out var exactFileId))
            {
                var exactFile = publishedFiles.FirstOrDefault(file => file.Id == exactFileId);
                if (exactFile is not null &&
                    await HasDownloadableCurseForgeServerPackAsync(projectId, exactFile, cancellationToken))
                {
                    return exactFile;
                }

                throw new InvalidOperationException(
                    $"CurseForge file ID {exactFileId} was not found or has no downloadable additional server pack.");
            }

            foreach (var file in publishedFiles.Where(file =>
                         ContainsInvariant(file.DisplayName, normalizedSelector) ||
                         ContainsInvariant(file.FileName, normalizedSelector)))
            {
                if (await HasDownloadableCurseForgeServerPackAsync(projectId, file, cancellationToken))
                {
                    return file;
                }
            }

            throw new InvalidOperationException(
                $"No CurseForge file matched requested version '{normalizedSelector}' with a downloadable additional server pack.");
        }

        foreach (var file in publishedFiles)
        {
            if (await HasDownloadableCurseForgeServerPackAsync(projectId, file, cancellationToken))
            {
                return file;
            }
        }

        throw new InvalidOperationException(
            $"No files with downloadable additional server packs were found for CurseForge project {projectId}.");
    }

    private async Task<CurseForgeFileEntry> ResolveCurseForgeClientFileAsync(
        int projectId,
        string? selectedVersion,
        CancellationToken cancellationToken)
    {
        var publishedFiles = await GetCurseForgePublishedFilesAsync(projectId, cancellationToken);

        if (string.IsNullOrWhiteSpace(selectedVersion))
        {
            return publishedFiles[0];
        }

        var normalizedSelector = selectedVersion.Trim();
        if (int.TryParse(normalizedSelector, NumberStyles.Integer, CultureInfo.InvariantCulture, out var exactFileId))
        {
            return publishedFiles.FirstOrDefault(file => file.Id == exactFileId)
                   ?? throw new InvalidOperationException(
                       $"CurseForge client file ID {exactFileId} was not found.");
        }

        return publishedFiles.FirstOrDefault(file =>
                   ContainsInvariant(file.DisplayName, normalizedSelector) ||
                   ContainsInvariant(file.FileName, normalizedSelector))
               ?? throw new InvalidOperationException(
                   $"No CurseForge client file matched requested version '{normalizedSelector}'.");
    }

    private async Task<CurseForgeFileEntry> ResolveCurseForgeServerPackFileAsync(
        int projectId,
        int parentFileId,
        CancellationToken cancellationToken)
    {
        var additionalFiles = await GetCurseForgeAdditionalFilesAsync(projectId, parentFileId, cancellationToken);
        if (additionalFiles.Count == 0)
        {
            throw new InvalidOperationException(
                $"No downloadable additional ZIP files were found for CurseForge file {parentFileId}.");
        }

        return additionalFiles[0];
    }

    private async Task<(CurseForgeFileEntry ParentFile, CurseForgeFileEntry ServerPackFile)?> TryResolveCurseForgeExplicitFileIdAsync(
        int projectId,
        int selectedFileId,
        CancellationToken cancellationToken)
    {
        var publishedFiles = await GetCurseForgePublishedFilesAsync(projectId, cancellationToken);

        var matchingParent = publishedFiles.FirstOrDefault(file => file.Id == selectedFileId);
        if (matchingParent is not null)
        {
            var additionalFiles = await GetCurseForgeAdditionalFilesAsync(projectId, matchingParent.Id, cancellationToken);
            if (additionalFiles.Count > 0)
            {
                return (matchingParent, additionalFiles[0]);
            }
        }

        foreach (var parentFile in publishedFiles)
        {
            var additionalFiles = await GetCurseForgeAdditionalFilesAsync(projectId, parentFile.Id, cancellationToken);
            var matchingAdditional = additionalFiles.FirstOrDefault(file => file.Id == selectedFileId);
            if (matchingAdditional is not null)
            {
                return (parentFile, matchingAdditional);
            }
        }

        return null;
    }

    private async Task<List<CurseForgeFileEntry>> GetCurseForgePublishedFilesAsync(
        int projectId,
        CancellationToken cancellationToken)
    {
        var url = $"https://www.curseforge.com/api/v1/mods/{projectId}/files?pageSize=50";
        var response = await GetJsonAsync<CurseForgeListResponse<CurseForgeFileEntry>>(url, cancellationToken);
        var files = response.Data?
            .Where(file => file.Status == CurseForgePublishedStatus)
            .OrderByDescending(file => file.DateCreated)
            .ToList()
            ?? [];

        if (files.Count == 0)
        {
            throw new InvalidOperationException($"No published files found for CurseForge project {projectId}.");
        }

        return files;
    }

    private async Task<bool> HasDownloadableCurseForgeServerPackAsync(
        int projectId,
        CurseForgeFileEntry parentFile,
        CancellationToken cancellationToken)
    {
        if (parentFile.HasServerPack || parentFile.AdditionalServerPackFilesCount > 0)
        {
            return true;
        }

        var additionalFiles = await GetCurseForgeAdditionalFilesAsync(projectId, parentFile.Id, cancellationToken);
        return additionalFiles.Count > 0;
    }

    private async Task<List<CurseForgeFileEntry>> GetCurseForgeAdditionalFilesAsync(
        int projectId,
        int parentFileId,
        CancellationToken cancellationToken)
    {
        var url = $"https://www.curseforge.com/api/v1/mods/{projectId}/files/{parentFileId}/additional-files";
        var response = await GetJsonAsync<CurseForgeListResponse<CurseForgeFileEntry>>(url, cancellationToken);
        return response.Data?
            .Where(file => file.Status == CurseForgePublishedStatus)
            .Where(file => file.FileName?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true)
            .OrderByDescending(LooksLikeServerPack)
            .ThenByDescending(file => file.DateCreated)
            .ToList()
            ?? [];
    }

    private async Task<T> GetJsonAsync<T>(string requestUrl, CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient(DownloadHttpClientName);
        EnsureDownloadClientHeaders(client);

        using var response = await client.GetAsync(requestUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
        return payload ?? throw new InvalidOperationException($"Unexpected empty response for {requestUrl}.");
    }

    private async Task<long> DownloadToFileAsync(
        string downloadUrl,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient(DownloadHttpClientName);
        EnsureDownloadClientHeaders(client);

        using var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destinationStream = File.Create(destinationPath);
        await sourceStream.CopyToAsync(destinationStream, cancellationToken);
        await destinationStream.FlushAsync(cancellationToken);

        return new FileInfo(destinationPath).Length;
    }

    private int ResolveWarningMinutes(int? warningMinutes)
    {
        var maxWarningMinutes = Math.Clamp(_options.ModpackSync.MaxWarningMinutes, 0, 1440);
        return Math.Clamp(warningMinutes ?? 0, 0, maxWarningMinutes);
    }

    private RestartPlan ResolveRestartPlan(string restartMode)
    {
        if (string.Equals(restartMode, "none", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(restartMode, "off", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(restartMode, "disabled", StringComparison.OrdinalIgnoreCase))
        {
            return RestartPlan.Disabled("restart_mode_disabled");
        }

        var modeEntry = _options.ModpackSync.RestartModes
            .FirstOrDefault(entry => string.Equals(entry.Key, restartMode, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(modeEntry.Key))
        {
            if (_options.ModpackSync.FailIfRestartModeUnconfigured)
            {
                throw new InvalidOperationException(
                    $"No restart command set is configured for restartMode '{restartMode}'.");
            }

            _logger.LogWarning(
                "sync_modpack restart mode '{RestartMode}' has no configured command set. Continuing without restart orchestration.",
                restartMode);
            return RestartPlan.Disabled("restart_mode_unconfigured");
        }

        var commandSet = modeEntry.Value ?? new ModpackRestartModeCommandSet();
        var stopCommandTemplate = Normalize(commandSet.StopCommandTemplate);
        var startCommandTemplate = Normalize(commandSet.StartCommandTemplate);
        if (stopCommandTemplate is null || startCommandTemplate is null)
        {
            if (_options.ModpackSync.FailIfRestartModeUnconfigured)
            {
                throw new InvalidOperationException(
                    $"Restart mode '{restartMode}' must define both stop/start command templates.");
            }

            _logger.LogWarning(
                "sync_modpack restart mode '{RestartMode}' is missing stop/start command templates. Continuing without restart orchestration.",
                restartMode);
            return RestartPlan.Disabled("restart_mode_incomplete");
        }

        return RestartPlan.CreateEnabled(
            Normalize(commandSet.WarningCommandTemplate),
            stopCommandTemplate,
            startCommandTemplate);
    }

    private async Task<AgentAmpRuntimeConfigResponse?> TryGetAmpRuntimeConfigAsync(
        string restartMode,
        SyncModpackDetails modpack,
        CancellationToken cancellationToken)
    {
        if (!IsAmpRestartMode(restartMode) ||
            !modpack.Id.HasValue ||
            modpack.Id.Value <= 0)
        {
            return null;
        }

        try
        {
            return await _agentApiClient.GetAmpRuntimeConfigAsync(modpack.Id.Value, cancellationToken);
        }
        catch (AgentApiException exception) when (
            exception.StatusCode == System.Net.HttpStatusCode.NotFound ||
            exception.StatusCode == System.Net.HttpStatusCode.BadRequest ||
            exception.StatusCode == System.Net.HttpStatusCode.Conflict ||
            exception.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            _logger.LogInformation(
                "AMP runtime settings lookup skipped for modpack {ModpackId}: {Message}",
                modpack.Id.Value,
                exception.Message);
            return null;
        }
    }

    private AmpApiRestartPlan? TryResolveAmpApiRestartPlan(
        string restartMode,
        SyncModpackDetails modpack,
        AgentAmpRuntimeConfigResponse? runtimeConfig)
    {
        if (!IsAmpRestartMode(restartMode))
        {
            return null;
        }

        var runtimeInstanceName = Normalize(runtimeConfig?.InstanceName) ?? Normalize(modpack.AmpInstanceName);
        if (runtimeConfig is not null &&
            !string.IsNullOrWhiteSpace(runtimeConfig.ControllerApiUrl) &&
            !string.IsNullOrWhiteSpace(runtimeConfig.Username) &&
            !string.IsNullOrWhiteSpace(runtimeConfig.Password) &&
            !string.IsNullOrWhiteSpace(runtimeInstanceName))
        {
            EnsureAbsoluteHttpUrl(runtimeConfig.ControllerApiUrl, "amp.controllerApiUrl");
            return new AmpApiRestartPlan(
                NormalizeAmpApiBaseUrl(runtimeConfig.ControllerApiUrl),
                runtimeConfig.Username,
                runtimeConfig.Password,
                runtimeConfig.Token ?? string.Empty,
                runtimeConfig.RememberMe,
                instanceName: runtimeInstanceName);
        }

        if (runtimeConfig is not null &&
            runtimeConfig.DirectAmpApiEnabled &&
            !string.IsNullOrWhiteSpace(runtimeConfig.DirectAmpApiUrl) &&
            !string.IsNullOrWhiteSpace(runtimeConfig.DirectAmpApiUsername) &&
            !string.IsNullOrWhiteSpace(runtimeConfig.DirectAmpApiPassword))
        {
            EnsureAbsoluteHttpUrl(runtimeConfig.DirectAmpApiUrl, "amp.directAmpApiUrl");
            return new AmpApiRestartPlan(
                NormalizeAmpApiBaseUrl(runtimeConfig.DirectAmpApiUrl),
                runtimeConfig.DirectAmpApiUsername,
                runtimeConfig.DirectAmpApiPassword,
                runtimeConfig.DirectAmpApiToken ?? string.Empty,
                runtimeConfig.DirectAmpApiRememberMe)
            {
                WarningMessageTemplate = runtimeConfig.DirectAmpApiWarningMessageTemplate
            };
        }

        var ampApiOptions = _options.ModpackSync.AmpApi;
        if (!ampApiOptions.Enabled)
        {
            return null;
        }

        var ampApiUrl = Normalize(modpack.AmpApiUrl);
        if (ampApiUrl is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(ampApiOptions.Username) ||
            string.IsNullOrWhiteSpace(ampApiOptions.Password))
        {
            return null;
        }

        EnsureAbsoluteHttpUrl(ampApiUrl, "modpack.ampApiUrl");
        return new AmpApiRestartPlan(
            NormalizeAmpApiBaseUrl(ampApiUrl),
            ampApiOptions.Username,
            ampApiOptions.Password,
            ampApiOptions.Token ?? string.Empty,
            ampApiOptions.RememberMe)
        {
            WarningMessageTemplate = ampApiOptions.WarningMessageTemplate
        };
    }

    private static bool IsAmpRestartMode(string restartMode)
    {
        return string.Equals(restartMode, "amp", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(restartMode, "amp_api", StringComparison.OrdinalIgnoreCase);
    }

    private string BuildAmpWarningMessage(
        AmpApiRestartPlan plan,
        string? modpackName,
        int warningMinutes,
        string? requestedVersion,
        string? currentVersion,
        string? targetVersion,
        string? targetVersionDisplay)
    {
        var template = string.IsNullOrWhiteSpace(plan.WarningMessageTemplate)
            ? "say Server will restart in {warningMinutes} minute(s) for an automatic modpack update to {targetVersionDisplay}. Please update your clients."
            : plan.WarningMessageTemplate;

        var expandedMessage = ExpandAmpConfigValueTokens(
            template,
            requestedVersion,
            currentVersion,
            targetVersion,
            targetVersionDisplay,
            modpackName,
            warningMinutes);

        return NormalizeAmpWarningCommand(expandedMessage);
    }

    private static string NormalizeAmpWarningCommand(string? warningMessage)
    {
        var trimmed = Normalize(warningMessage);
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return "say Server will restart soon for an automatic modpack update. Please update your clients.";
        }

        if (trimmed.StartsWith("/", StringComparison.Ordinal))
        {
            return trimmed.TrimStart('/').TrimStart();
        }

        var firstToken = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0];
        if (KnownAmpConsoleCommands.Contains(firstToken) || firstToken.Contains(':', StringComparison.Ordinal))
        {
            return trimmed;
        }

        return $"say {trimmed}";
    }

    private async Task ExecuteAmpWarningSequenceAsync(
        AmpApiRestartPlan plan,
        string? modpackName,
        int warningMinutes,
        string? requestedVersion,
        string? currentVersion,
        string? targetVersion,
        string? targetVersionDisplay,
        RestartExecutionState restartExecution,
        CancellationToken cancellationToken)
    {
        var warningCommandExecuted = false;

        if (warningMinutes > 1)
        {
            var initialWarningMessage = BuildAmpWarningMessage(
                plan,
                modpackName,
                warningMinutes,
                requestedVersion,
                currentVersion,
                targetVersion,
                targetVersionDisplay);
            warningCommandExecuted |= await TrySendAmpWarningAsync(
                plan,
                initialWarningMessage,
                cancellationToken);
            await Task.Delay(TimeSpan.FromMinutes(warningMinutes - 1), cancellationToken);
        }

        var oneMinuteWarningMessage = BuildAmpWarningMessage(
            plan,
            modpackName,
            warningMinutes: 1,
            requestedVersion,
            currentVersion,
            targetVersion,
            targetVersionDisplay);
        warningCommandExecuted |= await TrySendAmpWarningAsync(
            plan,
            oneMinuteWarningMessage,
            cancellationToken);
        restartExecution.WarningCommandExecuted = warningCommandExecuted;
        restartExecution.WarningWaitSeconds = warningMinutes * 60;
        await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
    }

    private async Task ExecuteShellWarningSequenceAsync(
        string warningCommandTemplate,
        string installRoot,
        string? modpackName,
        int commandId,
        int warningMinutes,
        string? requestedVersion,
        string? resolvedVersion,
        string? resolvedVersionDisplay,
        RestartExecutionState restartExecution,
        CancellationToken cancellationToken)
    {
        if (warningMinutes > 1)
        {
            await ExecuteShellCommandAsync(
                warningCommandTemplate,
                installRoot,
                modpackName,
                commandId,
                warningMinutes,
                requestedVersion,
                resolvedVersion,
                resolvedVersionDisplay,
                phase: "warning",
                cancellationToken);
            await Task.Delay(TimeSpan.FromMinutes(warningMinutes - 1), cancellationToken);
        }

        await ExecuteShellCommandAsync(
            warningCommandTemplate,
            installRoot,
            modpackName,
            commandId,
            warningMinutes: 1,
            requestedVersion,
            resolvedVersion,
            resolvedVersionDisplay,
            phase: "warning",
            cancellationToken);
        restartExecution.WarningCommandExecuted = true;
        restartExecution.WarningWaitSeconds = warningMinutes * 60;
        await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
    }

    private async Task<bool> TrySendAmpWarningAsync(
        AmpApiRestartPlan plan,
        string warningMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            var candidates = new[]
            {
                new { Module = "Core", Method = "SendConsoleMessage", ParameterKey = "message" },
                new { Module = "Core", Method = "SendConsoleCommand", ParameterKey = "command" },
                new { Module = "Core", Method = "SendConsoleCommand", ParameterKey = "message" },
                new { Module = "MinecraftModule", Method = "SendConsoleMessage", ParameterKey = "message" },
                new { Module = "MinecraftModule", Method = "SendConsoleCommand", ParameterKey = "command" },
                new { Module = "MinecraftModule", Method = "SendConsoleCommand", ParameterKey = "message" }
            };

            foreach (var candidate in candidates)
            {
                try
                {
                    await InvokeAmpInstanceCoreMethodAsync(
                        plan,
                        module: candidate.Module,
                        method: candidate.Method,
                        new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            [candidate.ParameterKey] = warningMessage
                        },
                        cancellationToken);
                    return true;
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(
                        exception,
                        "AMP warning failed via {ApiModule}/{ApiMethod} ({ParameterKey}).",
                        candidate.Module,
                        candidate.Method,
                        candidate.ParameterKey);
                }
            }

            return false;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "AMP warning message could not be sent. Continuing without warning delay.");
            return false;
        }
    }

    private async Task ExecuteAmpStopAsync(AmpApiRestartPlan plan, CancellationToken cancellationToken)
    {
        if (plan.IsControllerMode)
        {
            await InvokeAmpInstanceCoreMethodAsync(
                plan,
                module: "Core",
                method: "Stop",
                parameters: new Dictionary<string, object?>(StringComparer.Ordinal),
                cancellationToken);
            return;
        }

        await ExecuteAmpApiMethodAsync(
            plan,
            module: "Core",
            method: "Stop",
            parameters: new Dictionary<string, object?>(StringComparer.Ordinal),
            cancellationToken);
    }

    private async Task ExecuteAmpStartAsync(AmpApiRestartPlan plan, CancellationToken cancellationToken)
    {
        if (plan.IsControllerMode)
        {
            await ExecuteAmpControllerStartAsync(plan, cancellationToken);
            await WaitForAmpControllerRunningStateAsync(plan, expectedRunning: true, cancellationToken);
            return;
        }

        await ExecuteAmpApiMethodAsync(
            plan,
            module: "Core",
            method: "Start",
            parameters: new Dictionary<string, object?>(StringComparer.Ordinal),
            cancellationToken);
    }

    private async Task ExecuteAmpControllerStartAsync(
        AmpApiRestartPlan plan,
        CancellationToken cancellationToken)
    {
        var proxyKeys = await ResolveAmpProxyServerKeysAsync(plan, cancellationToken);
        var proxyErrors = new List<string>();

        foreach (var proxyKey in proxyKeys)
        {
            try
            {
                var proxySessionId = await EnsureAmpProxySessionIdAsync(plan, proxyKey, cancellationToken);
                await ExecuteAmpProxyMethodWithResultAsync(
                    plan,
                    proxyKey,
                    module: "Core",
                    method: "Start",
                    parameters: new Dictionary<string, object?>(StringComparer.Ordinal),
                    proxySessionId,
                    cancellationToken);
                return;
            }
            catch (Exception exception)
            {
                if (await TryWaitForAmpStartAcceptanceAsync(plan, cancellationToken))
                {
                    _logger.LogInformation(
                        "AMP Core/Start returned a failure for instance {InstanceName} via proxy key {ProxyKey}, but AMP reported a start transition; continuing.",
                        plan.InstanceName,
                        proxyKey);
                    return;
                }

                proxyErrors.Add($"{proxyKey}: {exception.Message}");
                _logger.LogDebug(
                    exception,
                    "Proxy instance start failed via key {ProxyKey}.",
                    proxyKey);
            }
        }

        var controllerSessionId = await EnsureAmpApiSessionIdAsync(plan, cancellationToken);
        foreach (var proxyKey in proxyKeys)
        {
            try
            {
                await ExecuteAmpProxyMethodWithResultAsync(
                    plan,
                    proxyKey,
                    module: "Core",
                    method: "Start",
                    parameters: new Dictionary<string, object?>(StringComparer.Ordinal),
                    controllerSessionId,
                    cancellationToken);
                return;
            }
            catch (Exception exception)
            {
                if (await TryWaitForAmpStartAcceptanceAsync(plan, cancellationToken))
                {
                    _logger.LogInformation(
                        "AMP Core/Start returned a failure for instance {InstanceName} via controller session and proxy key {ProxyKey}, but AMP reported a start transition; continuing.",
                        plan.InstanceName,
                        proxyKey);
                    return;
                }

                proxyErrors.Add($"{proxyKey} (controller-session): {exception.Message}");
                _logger.LogDebug(
                    exception,
                    "Proxy instance start failed via controller session and key {ProxyKey}.",
                    proxyKey);
            }
        }

        var proxyErrorSummary = proxyErrors.Count == 0
            ? "No proxy keys were available."
            : string.Join(" | ", proxyErrors);
        throw new InvalidOperationException(
            $"AMP instance API Core/Start failed via proxy ({proxyErrorSummary}).");
    }

    private async Task ExecuteAmpUpdateApplicationAsync(
        AmpApiRestartPlan plan,
        CancellationToken cancellationToken)
    {
        await InvokeAmpInstanceCoreMethodAsync(
            plan,
            module: "Core",
            method: "UpdateApplication",
            parameters: new Dictionary<string, object?>(StringComparer.Ordinal),
            cancellationToken);
    }

    private async Task ExecuteAmpUpdateApplicationAndWaitAsync(
        AmpApiRestartPlan plan,
        CancellationToken cancellationToken,
        string operationDescription)
    {
        try
        {
            await ExecuteAmpUpdateApplicationAsync(plan, cancellationToken);
        }
        catch (Exception exception)
        {
            if (!await TryWaitForAmpUpdateAcceptanceAsync(plan, cancellationToken))
            {
                throw;
            }

            _logger.LogInformation(
                exception,
                "AMP Core/UpdateApplication returned a failure for instance {InstanceName}, but AMP reported update activity; continuing.",
                plan.InstanceName ?? "(direct)");
        }

        await WaitForAmpUpdateCompletionAsync(plan, cancellationToken);
        await WaitForAmpInstanceApiAvailabilityAsync(
            plan,
            cancellationToken,
            operationDescription);
    }

    private enum AmpUpdateProbeState
    {
        Inactive,
        Active,
        InstanceUnavailable
    }

    private async Task WaitForAmpUpdateCompletionAsync(
        AmpApiRestartPlan plan,
        CancellationToken cancellationToken)
    {
        var timeoutAt = DateTime.UtcNow + AmpUpdateWaitTimeout;
        var idlePolls = 0;
        var sawUpdateActivity = false;
        var loggedWait = false;

        while (DateTime.UtcNow < timeoutAt)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var taskState = await TryGetAmpUpdateTaskStateAsync(plan, cancellationToken);
            var updateInfoState = await TryGetAmpUpdateInfoStateAsync(plan, cancellationToken);
            var updateActive = taskState == AmpUpdateProbeState.Active ||
                               updateInfoState == AmpUpdateProbeState.Active;
            var instanceUnavailable = taskState == AmpUpdateProbeState.InstanceUnavailable ||
                                      updateInfoState == AmpUpdateProbeState.InstanceUnavailable;

            if (updateActive || instanceUnavailable)
            {
                if (updateActive)
                {
                    sawUpdateActivity = true;
                }

                idlePolls = 0;
                if (!loggedWait)
                {
                    _logger.LogInformation(
                        "Waiting for AMP application update to finish for instance {InstanceName}.",
                        plan.InstanceName ?? "(direct)");
                    loggedWait = true;
                }
                await Task.Delay(AmpUpdatePollInterval, cancellationToken);
                continue;
            }

            idlePolls++;
            if (idlePolls >= 2)
            {
                if (sawUpdateActivity)
                {
                    _logger.LogInformation(
                        "AMP application update completed for instance {InstanceName}.",
                        plan.InstanceName ?? "(direct)");
                }

                return;
            }

            await Task.Delay(AmpUpdatePollInterval, cancellationToken);
        }

        throw new InvalidOperationException(
            $"AMP application update did not finish within {AmpUpdateWaitTimeout.TotalMinutes:0} minutes.");
    }

    private async Task<AmpUpdateProbeState> TryGetAmpUpdateTaskStateAsync(
        AmpApiRestartPlan plan,
        CancellationToken cancellationToken)
    {
        try
        {
            var tasksElement = await InvokeAmpInstanceCoreMethodAsync(
                plan,
                module: "Core",
                method: "GetTasks",
                parameters: new Dictionary<string, object?>(StringComparer.Ordinal),
                cancellationToken);

            return HasActiveAmpUpdateTask(tasksElement)
                ? AmpUpdateProbeState.Active
                : AmpUpdateProbeState.Inactive;
        }
        catch (Exception exception)
        {
            if (IsAmpInstanceUnavailableException(exception))
            {
                _logger.LogDebug(
                    exception,
                    "AMP GetTasks polling saw temporary instance unavailability while waiting for application update.");
                return AmpUpdateProbeState.InstanceUnavailable;
            }

            _logger.LogDebug(
                exception,
                "AMP GetTasks polling failed while waiting for application update.");
            return AmpUpdateProbeState.Inactive;
        }
    }

    private async Task<bool> TryWaitForAmpUpdateAcceptanceAsync(
        AmpApiRestartPlan plan,
        CancellationToken cancellationToken)
    {
        var timeoutAt = DateTime.UtcNow + AmpUpdateAcceptanceWaitTimeout;
        while (DateTime.UtcNow < timeoutAt)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var taskState = await TryGetAmpUpdateTaskStateAsync(plan, cancellationToken);
            if (taskState is AmpUpdateProbeState.Active or AmpUpdateProbeState.InstanceUnavailable)
            {
                return true;
            }

            var updateInfoState = await TryGetAmpUpdateInfoStateAsync(plan, cancellationToken);
            if (updateInfoState is AmpUpdateProbeState.Active or AmpUpdateProbeState.InstanceUnavailable)
            {
                return true;
            }

            await Task.Delay(AmpStatePollInterval, cancellationToken);
        }

        return false;
    }

    private async Task<AmpUpdateProbeState> TryGetAmpUpdateInfoStateAsync(
        AmpApiRestartPlan plan,
        CancellationToken cancellationToken)
    {
        try
        {
            var updateInfoElement = await InvokeAmpInstanceCoreMethodAsync(
                plan,
                module: "Core",
                method: "GetUpdateInfo",
                parameters: new Dictionary<string, object?>(StringComparer.Ordinal),
                cancellationToken);

            return HasActiveAmpUpdateInfo(updateInfoElement)
                ? AmpUpdateProbeState.Active
                : AmpUpdateProbeState.Inactive;
        }
        catch (Exception exception)
        {
            if (IsAmpInstanceUnavailableException(exception))
            {
                _logger.LogDebug(
                    exception,
                    "AMP GetUpdateInfo polling saw temporary instance unavailability while waiting for application update.");
                return AmpUpdateProbeState.InstanceUnavailable;
            }

            _logger.LogDebug(
                exception,
                "AMP GetUpdateInfo polling failed while waiting for application update.");
            return AmpUpdateProbeState.Inactive;
        }
    }

    private static bool HasActiveAmpUpdateTask(JsonElement rootElement)
    {
        foreach (var element in EnumerateJsonObjects(rootElement))
        {
            if (!ContainsAmpUpdateKeyword(element.GetRawText()))
            {
                continue;
            }

            if (IsAmpTaskTerminal(element))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool HasActiveAmpUpdateInfo(JsonElement rootElement)
    {
        foreach (var element in EnumerateJsonObjects(rootElement))
        {
            foreach (var property in element.EnumerateObject())
            {
                if (!IsAmpUpdateActivityProperty(property.Name))
                {
                    continue;
                }

                if (TryReadBooleanLoose(property.Value, out var booleanValue))
                {
                    if (booleanValue)
                    {
                        return true;
                    }

                    continue;
                }

                var rawValue = ReadJsonString(property.Value);
                if (IsAmpUpdateStatusTextActive(rawValue))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsAmpTaskTerminal(JsonElement element)
    {
        foreach (var propertyName in new[]
                 {
                     "IsComplete",
                     "Complete",
                     "Completed",
                     "IsCompleted",
                     "Finished",
                     "IsFinished",
                     "Done",
                     "Cancelled",
                     "Canceled",
                     "Failed",
                     "Succeeded",
                     "Success"
                 })
        {
            if (TryReadBooleanPropertyLoose(element, propertyName, out var value) && value)
            {
                return true;
            }
        }

        var statusText = TryFindStringByPropertyNames(
            element,
            "Status",
            "status",
            "State",
            "state",
            "CurrentState",
            "TaskState",
            "Description",
            "description");

        return ContainsInvariant(statusText, "complete") ||
               ContainsInvariant(statusText, "completed") ||
               ContainsInvariant(statusText, "cancel") ||
               ContainsInvariant(statusText, "fail") ||
               ContainsInvariant(statusText, "done") ||
               ContainsInvariant(statusText, "finished") ||
               ContainsInvariant(statusText, "success");
    }

    private static bool IsAmpUpdateActivityProperty(string propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            return false;
        }

        if (ContainsInvariant(propertyName, "available") ||
            ContainsInvariant(propertyName, "version") ||
            ContainsInvariant(propertyName, "build"))
        {
            return false;
        }

        return ContainsInvariant(propertyName, "updating") ||
               ContainsInvariant(propertyName, "updateinprogress") ||
               ContainsInvariant(propertyName, "upgradeinprogress") ||
               ContainsInvariant(propertyName, "installing") ||
               ContainsInvariant(propertyName, "downloading");
    }

    private static bool IsAmpUpdateStatusTextActive(string? value)
    {
        return ContainsInvariant(value, "updating") ||
               ContainsInvariant(value, "installing") ||
               ContainsInvariant(value, "downloading") ||
               ContainsInvariant(value, "in progress") ||
               ContainsInvariant(value, "pending");
    }

    private static bool ContainsAmpUpdateKeyword(string? value)
    {
        return AmpUpdateKeywords.Any(keyword => ContainsInvariant(value, keyword));
    }

    private async Task<bool> TryWaitForAmpStartAcceptanceAsync(
        AmpApiRestartPlan plan,
        CancellationToken cancellationToken)
    {
        if (!plan.IsControllerMode)
        {
            return false;
        }

        var timeoutAt = DateTime.UtcNow + AmpStartAcceptanceWaitTimeout;
        while (DateTime.UtcNow < timeoutAt)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var running = await TryGetAmpControllerInstanceRunningAsync(plan, cancellationToken);
            if (running == true)
            {
                return true;
            }

            var controllerStateText = await TryGetAmpControllerInstanceStateTextAsync(plan, cancellationToken);
            if (IsAmpStartStateTextActive(controllerStateText))
            {
                return true;
            }

            await Task.Delay(AmpStatePollInterval, cancellationToken);
        }

        return false;
    }

    private async Task<string?> TryGetAmpControllerInstanceStateTextAsync(
        AmpApiRestartPlan plan,
        CancellationToken cancellationToken)
    {
        if (!plan.IsControllerMode)
        {
            return null;
        }

        var instanceReference = await TryResolveAmpControllerInstanceReferenceAsync(plan, cancellationToken);
        var sessionId = await EnsureAmpApiSessionIdAsync(plan, cancellationToken);

        try
        {
            using var instancesDocument = await PostAmpApiRequestAsync(
                plan.ApiBaseUrl,
                module: "ADSModule",
                method: "GetInstances",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["SESSIONID"] = sessionId
                },
                cancellationToken);

            if (TryFindAmpControllerStateText(instancesDocument.RootElement, plan, instanceReference, out var stateText))
            {
                return stateText;
            }
        }
        catch (Exception exception)
        {
            _logger.LogDebug(
                exception,
                "AMP GetInstances failed while checking controller start state text.");
        }

        try
        {
            using var statusDocument = await PostAmpApiRequestAsync(
                plan.ApiBaseUrl,
                module: "ADSModule",
                method: "GetInstanceStatuses",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["SESSIONID"] = sessionId
                },
                cancellationToken);

            return TryFindAmpControllerStateText(statusDocument.RootElement, plan, instanceReference, out var stateText)
                ? stateText
                : null;
        }
        catch (Exception exception)
        {
            _logger.LogDebug(
                exception,
                "AMP GetInstanceStatuses failed while checking controller start state text.");
            return null;
        }
    }

    private static bool IsAmpStartStateTextActive(string? value)
    {
        if (TryParseAmpApplicationStateCode(value, out var stateCode) &&
            stateCode == 5)
        {
            return true;
        }

        if (IsAmpStartedStateText(value))
        {
            return false;
        }

        return ContainsInvariant(value, "prepar") ||
               ContainsInvariant(value, "start") ||
               ContainsInvariant(value, "initializ") ||
               ContainsInvariant(value, "launch") ||
               ContainsInvariant(value, "boot") ||
               ContainsInvariant(value, "load");
    }

    private static bool IsAmpStartedStateText(string? value)
    {
        if (TryParseAmpApplicationStateCode(value, out var stateCode) &&
            stateCode == 20)
        {
            return true;
        }

        return ContainsInvariant(value, "running") ||
               ContainsInvariant(value, "started") ||
               ContainsInvariant(value, "online") ||
               ContainsInvariant(value, "listening");
    }

    private async Task WaitForAmpApplicationReadyAfterStartAsync(
        AmpApiRestartPlan plan,
        CancellationToken cancellationToken)
    {
        if (!plan.IsControllerMode)
        {
            return;
        }

        var timeoutAt = DateTime.UtcNow + AmpStateWaitTimeout;
        bool? lastObservedRunning = null;
        string? lastObservedStateText = null;
        string? lastObservedError = null;
        bool? lastObservedControllerRunning = null;
        string? lastObservedControllerStateText = null;

        while (DateTime.UtcNow < timeoutAt)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (running, stateText, error) = await TryGetAmpApplicationStatusSnapshotAsync(plan, cancellationToken);
            var controllerRunning = await TryGetAmpControllerInstanceRunningAsync(plan, cancellationToken);
            var controllerStateText = await TryGetAmpControllerInstanceStateTextAsync(plan, cancellationToken);
            if (running.HasValue)
            {
                lastObservedRunning = running;
            }

            if (!string.IsNullOrWhiteSpace(stateText))
            {
                lastObservedStateText = stateText;
            }

            if (!string.IsNullOrWhiteSpace(error))
            {
                lastObservedError = error;
            }

            if (controllerRunning.HasValue)
            {
                lastObservedControllerRunning = controllerRunning;
            }

            if (!string.IsNullOrWhiteSpace(controllerStateText))
            {
                lastObservedControllerStateText = controllerStateText;
            }

            var appStarted = running == true || IsAmpStartedStateText(stateText);
            var appStillStarting = IsAmpStartStateTextActive(stateText);
            if (appStarted && !appStillStarting)
            {
                return;
            }

            var controllerStarted = controllerRunning == true &&
                                    !IsAmpStartStateTextActive(controllerStateText);
            if (controllerStarted && !appStillStarting)
            {
                return;
            }

            await Task.Delay(AmpStatePollInterval, cancellationToken);
        }

        var observedText = !string.IsNullOrWhiteSpace(lastObservedStateText)
            ? DescribeAmpApplicationState(lastObservedStateText)
            : lastObservedRunning.HasValue
                ? (lastObservedRunning.Value ? "running" : "stopped")
                : lastObservedError ?? "unknown";
        var observedControllerText = !string.IsNullOrWhiteSpace(lastObservedControllerStateText)
            ? lastObservedControllerStateText
            : lastObservedControllerRunning.HasValue
                ? (lastObservedControllerRunning.Value ? "running" : "stopped")
                : "unknown";
        throw new InvalidOperationException(
            $"AMP instance '{plan.InstanceName}' did not finish application start. Last observed app state: {observedText}. Last observed controller state: {observedControllerText}.");
    }

    private async Task WaitForAmpApplicationIdleBeforeStartAsync(
        AmpApiRestartPlan plan,
        CancellationToken cancellationToken,
        string operationDescription)
    {
        if (!plan.IsControllerMode)
        {
            return;
        }

        var timeoutAt = DateTime.UtcNow + AmpStateWaitTimeout;
        bool? lastControllerRunning = null;
        string? lastControllerStateText = null;
        bool? lastAppRunning = null;
        string? lastAppStateText = null;
        string? lastAppError = null;

        while (DateTime.UtcNow < timeoutAt)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var controllerRunning = await TryGetAmpControllerInstanceRunningAsync(plan, cancellationToken);
            var controllerStateText = await TryGetAmpControllerInstanceStateTextAsync(plan, cancellationToken);
            var (appRunning, appStateText, appError) = await TryGetAmpApplicationStatusSnapshotAsync(plan, cancellationToken);

            if (controllerRunning.HasValue)
            {
                lastControllerRunning = controllerRunning;
            }

            if (!string.IsNullOrWhiteSpace(controllerStateText))
            {
                lastControllerStateText = controllerStateText;
            }

            if (appRunning.HasValue)
            {
                lastAppRunning = appRunning;
            }

            if (!string.IsNullOrWhiteSpace(appStateText))
            {
                lastAppStateText = appStateText;
            }

            if (!string.IsNullOrWhiteSpace(appError))
            {
                lastAppError = appError;
            }

            var hasAppSignal = appRunning.HasValue || !string.IsNullOrWhiteSpace(appStateText);
            var controllerActive = controllerRunning == true || IsAmpStartStateTextActive(controllerStateText);
            var appActive = appRunning == true || IsAmpStartStateTextActive(appStateText);
            if ((hasAppSignal && !appActive) ||
                (!hasAppSignal && !controllerActive))
            {
                return;
            }

            await Task.Delay(AmpStatePollInterval, cancellationToken);
        }

        var appObservedText = !string.IsNullOrWhiteSpace(lastAppStateText)
            ? DescribeAmpApplicationState(lastAppStateText)
            : lastAppRunning.HasValue
                ? (lastAppRunning.Value ? "running" : "stopped")
                : lastAppError ?? "unknown";
        var controllerObservedText = !string.IsNullOrWhiteSpace(lastControllerStateText)
            ? lastControllerStateText
            : lastControllerRunning.HasValue
                ? (lastControllerRunning.Value ? "running" : "stopped")
                : "unknown";

        throw new InvalidOperationException(
            $"AMP instance '{plan.InstanceName}' did not return to an idle state after {operationDescription}. Last observed app state: {appObservedText}. Last observed controller state: {controllerObservedText}.");
    }

    private async Task<(bool? Running, string? StateText, string? Error)> TryGetAmpApplicationStatusSnapshotAsync(
        AmpApiRestartPlan plan,
        CancellationToken cancellationToken)
    {
        try
        {
            var statusElement = await InvokeAmpInstanceCoreMethodAsync(
                plan,
                module: "Core",
                method: "GetStatus",
                parameters: new Dictionary<string, object?>(StringComparer.Ordinal),
                cancellationToken);

            var stateText = TryFindStringByPropertyNames(
                statusElement,
                "DisplayState",
                "displayState",
                "Description",
                "description",
                "State",
                "state",
                "CurrentState",
                "currentState",
                "Status",
                "status",
                "InstanceState",
                "instanceState",
                "AppState",
                "appState",
                "ApplicationState",
                "applicationState");
            bool? running = TryFindAmpApplicationRunningState(statusElement, out var parsedRunning)
                ? parsedRunning
                : null;
            return (running, stateText, null);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(
                exception,
                "AMP Core/GetStatus failed while checking application start readiness for instance {InstanceName}.",
                plan.InstanceName);
            return (null, null, TruncateForLog(exception.Message, 300));
        }
    }

    private static bool TryFindAmpApplicationRunningState(JsonElement rootElement, out bool running)
    {
        foreach (var element in EnumerateJsonObjects(rootElement))
        {
            if (TryReadBooleanPropertyLoose(element, "Running", out running) ||
                TryReadBooleanPropertyLoose(element, "IsRunning", out running) ||
                TryReadBooleanPropertyLoose(element, "Started", out running) ||
                TryReadBooleanPropertyLoose(element, "IsStarted", out running) ||
                TryReadBooleanPropertyLoose(element, "ApplicationRunning", out running) ||
                TryReadBooleanPropertyLoose(element, "IsApplicationRunning", out running))
            {
                return true;
            }
        }

        running = false;
        return false;
    }

    private static bool TryParseAmpApplicationStateCode(string? value, out int stateCode)
    {
        stateCode = 0;
        return !string.IsNullOrWhiteSpace(value) &&
               int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out stateCode);
    }

    private static string DescribeAmpApplicationState(string? value)
    {
        if (!TryParseAmpApplicationStateCode(value, out var stateCode))
        {
            return value ?? "unknown";
        }

        return stateCode switch
        {
            5 => "5 (preparing to start)",
            20 => "20 (running)",
            _ => stateCode.ToString(CultureInfo.InvariantCulture)
        };
    }

    private async Task WaitForAmpControllerRunningStateAsync(
        AmpApiRestartPlan plan,
        bool expectedRunning,
        CancellationToken cancellationToken)
    {
        if (!plan.IsControllerMode)
        {
            return;
        }

        var timeoutAt = DateTime.UtcNow + AmpStateWaitTimeout;
        bool? lastObservedRunning = null;
        string? lastObservedStateText = null;
        while (DateTime.UtcNow < timeoutAt)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var observedRunning = await TryGetAmpControllerInstanceRunningAsync(plan, cancellationToken);
            var observedStateText = await TryGetAmpControllerInstanceStateTextAsync(plan, cancellationToken);
            if (!string.IsNullOrWhiteSpace(observedStateText))
            {
                lastObservedStateText = observedStateText;
            }

            if (observedRunning.HasValue)
            {
                lastObservedRunning = observedRunning;
                if (observedRunning.Value == expectedRunning &&
                    !(expectedRunning && IsAmpStartStateTextActive(observedStateText)))
                {
                    return;
                }
            }

            await Task.Delay(AmpStatePollInterval, cancellationToken);
        }

        var expectedText = expectedRunning ? "running" : "stopped";
        var observedText = !string.IsNullOrWhiteSpace(lastObservedStateText)
            ? lastObservedStateText
            : lastObservedRunning.HasValue
            ? (lastObservedRunning.Value ? "running" : "stopped")
            : "unknown";
        throw new InvalidOperationException(
            $"AMP instance '{plan.InstanceName}' did not reach expected {expectedText} state. Last observed state: {observedText}.");
    }

    private async Task<bool?> TryGetAmpControllerInstanceRunningAsync(
        AmpApiRestartPlan plan,
        CancellationToken cancellationToken)
    {
        if (!plan.IsControllerMode)
        {
            return null;
        }

        var instanceReference = await TryResolveAmpControllerInstanceReferenceAsync(plan, cancellationToken);
        var sessionId = await EnsureAmpApiSessionIdAsync(plan, cancellationToken);

        // Prefer GetInstances because it exposes per-instance Running state in this AMP setup.
        using var instancesDocument = await PostAmpApiRequestAsync(
            plan.ApiBaseUrl,
            module: "ADSModule",
            method: "GetInstances",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["SESSIONID"] = sessionId
            },
            cancellationToken);

        if (TryFindRunningStateFromInstances(instancesDocument.RootElement, plan, out var runningFromInstances))
        {
            return runningFromInstances;
        }

        // Fallback for AMP variants that only expose useful state through GetInstanceStatuses.
        using var statusDocument = await PostAmpApiRequestAsync(
            plan.ApiBaseUrl,
            module: "ADSModule",
            method: "GetInstanceStatuses",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["SESSIONID"] = sessionId
            },
            cancellationToken);

        if (TryFindRunningStateFromStatuses(statusDocument.RootElement, plan, instanceReference, out var runningFromStatus))
        {
            return runningFromStatus;
        }

        return null;
    }

    private async Task<Dictionary<string, object?>> BuildAmpControllerInstancePayloadAsync(
        AmpApiRestartPlan plan,
        CancellationToken cancellationToken)
    {
        var instanceReference = await TryResolveAmpControllerInstanceReferenceAsync(plan, cancellationToken);
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["InstanceName"] = instanceReference?.InstanceName ?? plan.InstanceName!
        };

        if (!string.IsNullOrWhiteSpace(instanceReference?.InstanceId))
        {
            payload["InstanceID"] = instanceReference.InstanceId;
        }

        if (!string.IsNullOrWhiteSpace(instanceReference?.TargetId))
        {
            payload["TargetID"] = instanceReference.TargetId;
        }

        return payload;
    }

    private async Task<AmpControllerInstanceReference?> TryResolveAmpControllerInstanceReferenceAsync(
        AmpApiRestartPlan plan,
        CancellationToken cancellationToken)
    {
        if (!plan.IsControllerMode)
        {
            return null;
        }

        if (plan.InstanceReference is not null)
        {
            return plan.InstanceReference;
        }

        var sessionId = await EnsureAmpApiSessionIdAsync(plan, cancellationToken);
        using var responseDocument = await PostAmpApiRequestAsync(
            plan.ApiBaseUrl,
            module: "ADSModule",
            method: "GetInstances",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["SESSIONID"] = sessionId
            },
            cancellationToken);

        var references = ReadAmpControllerInstanceReferences(responseDocument.RootElement);
        var matchedReference = references.FirstOrDefault(reference =>
            MatchesAmpControllerIdentifier(reference, plan.InstanceName!));
        if (matchedReference is null)
        {
            return null;
        }

        plan.InstanceReference = matchedReference;
        return matchedReference;
    }

    private static bool TryFindRunningStateFromStatuses(
        JsonElement rootElement,
        AmpApiRestartPlan plan,
        AmpControllerInstanceReference? instanceReference,
        out bool running)
    {
        running = false;
        var candidateIdentifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(plan.InstanceName))
        {
            candidateIdentifiers.Add(plan.InstanceName!);
        }

        if (instanceReference is not null)
        {
            if (!string.IsNullOrWhiteSpace(instanceReference.InstanceId))
            {
                candidateIdentifiers.Add(instanceReference.InstanceId);
            }

            if (!string.IsNullOrWhiteSpace(instanceReference.TargetId))
            {
                candidateIdentifiers.Add(instanceReference.TargetId);
            }

            if (!string.IsNullOrWhiteSpace(instanceReference.InstanceName))
            {
                candidateIdentifiers.Add(instanceReference.InstanceName);
            }
        }

        foreach (var element in EnumerateAmpObjectCandidates(rootElement))
        {
            if (!TryGetPropertyIgnoreCase(element, "Running", out var runningElement) ||
                !TryReadBooleanLoose(runningElement, out var parsedRunning))
            {
                continue;
            }

            string? resolvedIdentifier = null;
            if (TryGetPropertyIgnoreCase(element, "InstanceID", out var instanceIdElement))
            {
                resolvedIdentifier = ReadJsonString(instanceIdElement);
            }
            else if (TryGetPropertyIgnoreCase(element, "TargetID", out var targetIdElement))
            {
                resolvedIdentifier = ReadJsonString(targetIdElement);
            }
            else if (TryGetPropertyIgnoreCase(element, "InstanceName", out var instanceNameElement))
            {
                resolvedIdentifier = ReadJsonString(instanceNameElement);
            }

            if (string.IsNullOrWhiteSpace(resolvedIdentifier) ||
                !candidateIdentifiers.Contains(resolvedIdentifier))
            {
                continue;
            }

            running = parsedRunning;
            return true;
        }

        return false;
    }

    private static bool TryFindAmpControllerStateText(
        JsonElement rootElement,
        AmpApiRestartPlan plan,
        AmpControllerInstanceReference? instanceReference,
        out string? stateText)
    {
        stateText = null;
        var candidateIdentifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(plan.InstanceName))
        {
            candidateIdentifiers.Add(plan.InstanceName!);
        }

        if (instanceReference is not null)
        {
            if (!string.IsNullOrWhiteSpace(instanceReference.InstanceId))
            {
                candidateIdentifiers.Add(instanceReference.InstanceId);
            }

            if (!string.IsNullOrWhiteSpace(instanceReference.TargetId))
            {
                candidateIdentifiers.Add(instanceReference.TargetId);
            }

            if (!string.IsNullOrWhiteSpace(instanceReference.InstanceName))
            {
                candidateIdentifiers.Add(instanceReference.InstanceName);
            }

            if (!string.IsNullOrWhiteSpace(instanceReference.FriendlyName))
            {
                candidateIdentifiers.Add(instanceReference.FriendlyName);
            }
        }

        foreach (var element in EnumerateAmpObjectCandidates(rootElement))
        {
            var matchesIdentifier =
                (TryGetPropertyIgnoreCase(element, "InstanceID", out var instanceIdElement) &&
                 candidateIdentifiers.Contains(ReadJsonString(instanceIdElement) ?? string.Empty)) ||
                (TryGetPropertyIgnoreCase(element, "TargetID", out var targetIdElement) &&
                 candidateIdentifiers.Contains(ReadJsonString(targetIdElement) ?? string.Empty)) ||
                (TryGetPropertyIgnoreCase(element, "InstanceName", out var instanceNameElement) &&
                 candidateIdentifiers.Contains(ReadJsonString(instanceNameElement) ?? string.Empty)) ||
                (TryGetPropertyIgnoreCase(element, "FriendlyName", out var friendlyNameElement) &&
                 candidateIdentifiers.Contains(ReadJsonString(friendlyNameElement) ?? string.Empty));

            if (!matchesIdentifier)
            {
                continue;
            }

            stateText = TryFindStringByPropertyNames(
                element,
                "State",
                "state",
                "CurrentState",
                "currentState",
                "Status",
                "status",
                "InstanceState",
                "instanceState",
                "AppState",
                "appState",
                "Description",
                "description");
            if (!string.IsNullOrWhiteSpace(stateText))
            {
                stateText = stateText.Trim();
                return true;
            }
        }

        return false;
    }

    private static bool TryFindRunningStateFromInstances(
        JsonElement rootElement,
        AmpApiRestartPlan plan,
        out bool running)
    {
        running = false;
        foreach (var reference in ReadAmpControllerInstanceReferences(rootElement))
        {
            if (!MatchesAmpControllerIdentifier(reference, plan.InstanceName!))
            {
                continue;
            }

            if (!reference.Running.HasValue)
            {
                return false;
            }

            running = reference.Running.Value;
            return true;
        }

        return false;
    }

    private static List<AmpControllerInstanceReference> ReadAmpControllerInstanceReferences(JsonElement rootElement)
    {
        var references = new List<AmpControllerInstanceReference>();
        foreach (var element in EnumerateAmpObjectCandidates(rootElement))
        {
            var instanceName = TryGetPropertyIgnoreCase(element, "InstanceName", out var instanceNameElement)
                ? ReadJsonString(instanceNameElement)
                : null;
            var friendlyName = TryGetPropertyIgnoreCase(element, "FriendlyName", out var friendlyNameElement)
                ? ReadJsonString(friendlyNameElement)
                : null;
            var instanceId = TryGetPropertyIgnoreCase(element, "InstanceID", out var instanceIdElement)
                ? ReadJsonString(instanceIdElement)
                : null;
            var targetId = TryGetPropertyIgnoreCase(element, "TargetID", out var targetIdElement)
                ? ReadJsonString(targetIdElement)
                : null;
            bool? running = null;
            if (TryGetPropertyIgnoreCase(element, "Running", out var runningElement) &&
                TryReadBooleanLoose(runningElement, out var parsedRunning))
            {
                running = parsedRunning;
            }

            if (string.IsNullOrWhiteSpace(instanceName) &&
                string.IsNullOrWhiteSpace(friendlyName) &&
                string.IsNullOrWhiteSpace(instanceId) &&
                string.IsNullOrWhiteSpace(targetId))
            {
                continue;
            }

            references.Add(new AmpControllerInstanceReference(
                instanceName,
                friendlyName,
                instanceId,
                targetId,
                running));
        }

        return references;
    }

    private static IEnumerable<JsonElement> EnumerateAmpObjectCandidates(JsonElement rootElement)
    {
        if (rootElement.ValueKind == JsonValueKind.Object)
        {
            if (LooksLikeAmpInstanceObject(rootElement) || LooksLikeAmpStatusObject(rootElement))
            {
                yield return rootElement;
            }

            foreach (var property in rootElement.EnumerateObject())
            {
                if (property.Value.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object))
                {
                    continue;
                }

                foreach (var nested in EnumerateAmpObjectCandidates(property.Value))
                {
                    yield return nested;
                }
            }

            yield break;
        }

        if (rootElement.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in rootElement.EnumerateArray())
        {
            if (item.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object))
            {
                continue;
            }

            foreach (var nested in EnumerateAmpObjectCandidates(item))
            {
                yield return nested;
            }
        }
    }

    private static bool LooksLikeAmpInstanceObject(JsonElement element)
    {
        return TryGetPropertyIgnoreCase(element, "InstanceName", out _) ||
               TryGetPropertyIgnoreCase(element, "FriendlyName", out _) ||
               TryGetPropertyIgnoreCase(element, "TargetID", out _);
    }

    private static bool LooksLikeAmpStatusObject(JsonElement element)
    {
        return TryGetPropertyIgnoreCase(element, "InstanceID", out _) &&
               TryGetPropertyIgnoreCase(element, "Running", out _);
    }

    private static bool MatchesAmpControllerIdentifier(
        AmpControllerInstanceReference reference,
        string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return false;
        }

        return string.Equals(reference.InstanceName, identifier, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(reference.FriendlyName, identifier, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(reference.InstanceId, identifier, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(reference.TargetId, identifier, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            value = property.Value;
            return true;
        }

        return false;
    }

    private static bool TryReadBooleanLoose(JsonElement element, out bool value)
    {
        value = false;
        switch (element.ValueKind)
        {
            case JsonValueKind.True:
                value = true;
                return true;
            case JsonValueKind.False:
                value = false;
                return true;
            case JsonValueKind.Number when element.TryGetInt32(out var numericValue):
                value = numericValue != 0;
                return true;
            case JsonValueKind.String:
            {
                var rawValue = element.GetString()?.Trim();
                if (string.Equals(rawValue, "true", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(rawValue, "1", StringComparison.Ordinal))
                {
                    value = true;
                    return true;
                }

                if (string.Equals(rawValue, "false", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(rawValue, "0", StringComparison.Ordinal))
                {
                    value = false;
                    return true;
                }

                return false;
            }
            default:
                return false;
        }
    }

    private async Task ExecuteAmpConfigUpdateAsync(
        AmpApiRestartPlan plan,
        IReadOnlyDictionary<string, string> ampConfigValues,
        CancellationToken cancellationToken)
    {
        if (ampConfigValues.Count == 0)
        {
            return;
        }

        if (plan.IsControllerMode)
        {
            Exception? controllerApplyException = null;

            try
            {
                await ExecuteAmpControllerConfigUpdateAsync(plan, ampConfigValues, cancellationToken);
                await WaitForAmpInstanceApiAvailabilityAsync(plan, cancellationToken);
                return;
            }
            catch (Exception exception)
            {
                controllerApplyException = exception;
                _logger.LogWarning(
                    exception,
                    "AMP ADSModule/ApplyInstanceConfiguration failed for instance {InstanceName}. Falling back to instance API config writes.",
                    plan.InstanceName);
            }

            try
            {
                await InvokeAmpInstanceCoreMethodAsync(
                    plan,
                    module: "Core",
                    method: "SetConfigs",
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["data"] = ampConfigValues
                    },
                    cancellationToken);
                return;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "AMP Core/SetConfigs failed for instance {InstanceName}. Falling back to per-node SetConfig.",
                    plan.InstanceName);
            }

            var perNodeExceptions = new List<string>();
            foreach (var configEntry in ampConfigValues)
            {
                try
                {
                    await InvokeAmpInstanceCoreMethodAsync(
                        plan,
                        module: "Core",
                        method: "SetConfig",
                        new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["node"] = configEntry.Key,
                            ["value"] = configEntry.Value
                        },
                        cancellationToken);
                }
                catch (Exception exception)
                {
                    perNodeExceptions.Add($"{configEntry.Key}: {exception.Message}");
                }
            }

            if (perNodeExceptions.Count > 0)
            {
                var failureSummary = string.Join(" | ", perNodeExceptions);
                if (controllerApplyException is not null)
                {
                    throw new InvalidOperationException(
                        $"AMP controller config apply failed ({controllerApplyException.Message}) and instance SetConfig fallback failed ({failureSummary}).",
                        controllerApplyException);
                }

                throw new InvalidOperationException(
                    $"AMP instance SetConfig fallback failed ({failureSummary}).");
            }

            return;
        }

        await ExecuteAmpApiMethodAsync(
            plan,
            module: "Core",
            method: "SetConfigs",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["data"] = ampConfigValues
            },
            cancellationToken);
    }

    private async Task ExecuteAmpLoaderConfigUpdateAsync(
        AmpApiRestartPlan plan,
        IReadOnlyDictionary<string, string> ampConfigValues,
        CancellationToken cancellationToken)
    {
        if (!plan.IsControllerMode)
        {
            await ExecuteAmpConfigUpdateAsync(plan, ampConfigValues, cancellationToken);
            return;
        }

        await ExecuteAmpControllerConfigUpdateAsync(plan, ampConfigValues, cancellationToken);
        await WaitForAmpInstanceApiAvailabilityAsync(plan, cancellationToken);
    }

    private async Task ExecuteAmpControllerConfigUpdateAsync(
        AmpApiRestartPlan plan,
        IReadOnlyDictionary<string, string> ampConfigValues,
        CancellationToken cancellationToken)
    {
        if (!plan.IsControllerMode)
        {
            throw new InvalidOperationException("AMP controller config update requires controller mode.");
        }

        var instanceReference = await TryResolveAmpControllerInstanceReferenceAsync(plan, cancellationToken);
        if (instanceReference is null || string.IsNullOrWhiteSpace(instanceReference.InstanceId))
        {
            throw new InvalidOperationException(
                $"AMP controller instance reference for '{plan.InstanceName}' did not include an InstanceID.");
        }

        await ExecuteAmpApiMethodAsync(
            plan,
            module: "ADSModule",
            method: "ApplyInstanceConfiguration",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["InstanceID"] = instanceReference.InstanceId,
                ["Args"] = ampConfigValues,
                ["RebuildConfiguration"] = true
            },
            cancellationToken);
    }

    private async Task RefreshAmpControllerInstanceConfigAsync(
        AmpApiRestartPlan plan,
        AmpControllerInstanceReference? instanceReference,
        CancellationToken cancellationToken)
    {
        var instanceId = instanceReference?.InstanceId;
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return;
        }

        await ExecuteAmpApiMethodAsync(
            plan,
            module: "ADSModule",
            method: "RefreshInstanceConfig",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["InstanceId"] = instanceId
            },
            cancellationToken);
    }

    private async Task WaitForAmpInstanceApiAvailabilityAsync(
        AmpApiRestartPlan plan,
        CancellationToken cancellationToken,
        string operationDescription = "configuration update")
    {
        if (!plan.IsControllerMode)
        {
            return;
        }

        var timeoutAt = DateTime.UtcNow + AmpStateWaitTimeout;
        string? lastError = null;
        plan.ProxySessionIds.Clear();

        while (DateTime.UtcNow < timeoutAt)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (isAvailable, error) = await TryGetAmpInstanceApiAvailabilityAsync(plan, cancellationToken);
            if (isAvailable)
            {
                return;
            }

            lastError = error ?? lastError;
            plan.ProxySessionIds.Clear();
            await Task.Delay(AmpStatePollInterval, cancellationToken);
        }

        throw new InvalidOperationException(
            $"AMP instance '{plan.InstanceName}' did not become available after {operationDescription}. Last error: {lastError ?? "unknown"}.");
    }

    private async Task<(bool IsAvailable, string? Error)> TryGetAmpInstanceApiAvailabilityAsync(
        AmpApiRestartPlan plan,
        CancellationToken cancellationToken)
    {
        try
        {
            await InvokeAmpInstanceCoreMethodAsync(
                plan,
                module: "Core",
                method: "GetStatus",
                parameters: new Dictionary<string, object?>(StringComparer.Ordinal),
                cancellationToken);
            return (true, null);
        }
        catch (Exception exception)
        {
            return (false, TruncateForLog(exception.Message, 300));
        }
    }

    private static bool IsAmpInstanceUnavailableException(Exception exception)
    {
        if (ContainsInvariant(exception.Message, "instance unavailable"))
        {
            return true;
        }

        return exception.InnerException is not null &&
               IsAmpInstanceUnavailableException(exception.InnerException);
    }

    private static bool IsAmpUpdateApplicationRecoverableException(Exception exception)
    {
        if (ContainsInvariant(exception.Message, "updateapplication") &&
            (ContainsInvariant(exception.Message, "null reference") ||
             ContainsInvariant(exception.Message, "object reference not set") ||
             ContainsInvariant(exception.Message, "instance unavailable") ||
             ContainsInvariant(exception.Message, "unknown amp api error") ||
             ContainsInvariant(exception.Message, "failure status")))
        {
            return true;
        }

        return exception.InnerException is not null &&
               IsAmpUpdateApplicationRecoverableException(exception.InnerException);
    }

    private static Dictionary<string, string> ResolveAmpConfigValues(
        string? ampConfigValuesJson,
        string? requestedVersion,
        string? currentVersion,
        string? targetVersion,
        string? targetVersionDisplay,
        string? modpackName,
        int warningMinutes,
        DetectedMinecraftRuntime? detectedRuntime = null)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(ampConfigValuesJson))
        {
            return values;
        }

        using var document = JsonDocument.Parse(ampConfigValuesJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("modpack.ampConfigValuesJson must be a JSON object.");
        }

        foreach (var property in document.RootElement.EnumerateObject())
        {
            var node = property.Name?.Trim();
            if (string.IsNullOrWhiteSpace(node))
            {
                continue;
            }

            var rawValue = ReadJsonString(property.Value);
            if (rawValue is null)
            {
                continue;
            }

            values[node] = ExpandAmpConfigValueTokens(
                rawValue,
                requestedVersion,
                currentVersion,
                targetVersion,
                targetVersionDisplay,
                modpackName,
                warningMinutes,
                detectedRuntime);
        }

        return values;
    }

    private static Dictionary<string, string> MergeAmpConfigValues(
        IReadOnlyDictionary<string, string> autoAmpConfigValues,
        IReadOnlyDictionary<string, string> explicitAmpConfigValues)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var entry in autoAmpConfigValues)
        {
            merged[entry.Key] = entry.Value;
        }

        foreach (var entry in explicitAmpConfigValues)
        {
            merged[entry.Key] = entry.Value;
        }

        return merged;
    }

    private static string ExpandAmpConfigValueTokens(
        string template,
        string? requestedVersion,
        string? currentVersion,
        string? targetVersion,
        string? targetVersionDisplay,
        string? modpackName,
        int warningMinutes,
        DetectedMinecraftRuntime? detectedRuntime = null)
    {
        var resolvedTargetVersionDisplay = Normalize(targetVersionDisplay) ??
                                           Normalize(targetVersion) ??
                                           "Latest";
        return template
            .Replace("{requestedVersion}", requestedVersion ?? string.Empty, StringComparison.Ordinal)
            .Replace("{currentVersion}", currentVersion ?? string.Empty, StringComparison.Ordinal)
            .Replace("{targetVersion}", targetVersion ?? string.Empty, StringComparison.Ordinal)
            .Replace("{targetVersionDisplay}", resolvedTargetVersionDisplay, StringComparison.Ordinal)
            .Replace("{modpackName}", modpackName ?? string.Empty, StringComparison.Ordinal)
            .Replace("{warningMinutes}", warningMinutes.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{loaderId}", detectedRuntime?.LoaderId ?? string.Empty, StringComparison.Ordinal)
            .Replace("{loaderKind}", detectedRuntime?.LoaderKind ?? string.Empty, StringComparison.Ordinal)
            .Replace("{loaderVersion}", detectedRuntime?.LoaderVersion ?? string.Empty, StringComparison.Ordinal)
            .Replace("{minecraftVersion}", detectedRuntime?.MinecraftVersion ?? string.Empty, StringComparison.Ordinal);
    }

    private async Task ExecuteAmpApiMethodAsync(
        AmpApiRestartPlan plan,
        string module,
        string method,
        IDictionary<string, object?> parameters,
        CancellationToken cancellationToken)
    {
        var sessionId = await EnsureAmpApiSessionIdAsync(plan, cancellationToken);
        var payload = new Dictionary<string, object?>(parameters, StringComparer.Ordinal)
        {
            ["SESSIONID"] = sessionId
        };

        using var responseDocument = await PostAmpApiRequestAsync(
            plan.ApiBaseUrl,
            module,
            method,
            payload,
            cancellationToken);

        EnsureAmpApiResponseSucceeded(responseDocument.RootElement, module, method);
    }

    private async Task<string> EnsureAmpApiSessionIdAsync(
        AmpApiRestartPlan plan,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(plan.SessionId))
        {
            return plan.SessionId!;
        }

        using var responseDocument = await PostAmpApiRequestAsync(
            plan.ApiBaseUrl,
            module: "Core",
            method: "Login",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["username"] = plan.Username,
                ["password"] = plan.Password,
                ["token"] = plan.Token,
                ["rememberMe"] = plan.RememberMe
            },
            cancellationToken);

        EnsureAmpApiResponseSucceeded(responseDocument.RootElement, "Core", "Login");

        var sessionId = TryFindStringByPropertyNames(
            responseDocument.RootElement,
            "sessionID",
            "sessionId",
            "session_id",
            "SESSIONID");
        sessionId ??= TryExtractAmpSessionId(responseDocument.RootElement);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new InvalidOperationException("AMP API login succeeded but no session ID was returned.");
        }

        plan.SessionId = sessionId;
        return sessionId;
    }

    private async Task<JsonDocument> PostAmpApiRequestAsync(
        string ampApiBaseUrl,
        string module,
        string method,
        IDictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        var requestUrl = $"{ampApiBaseUrl}/API/{module}/{method}";
        using var client = _httpClientFactory.CreateClient(AmpApiHttpClientName);
        if (!client.DefaultRequestHeaders.Accept.Any())
        {
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.cubecoders-ampapi"));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUrl);
        if (TryGetAmpRequestSessionId(payload, out var sessionId))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sessionId);
        }

        request.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");
        using var response = await client.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var responseSummary = string.IsNullOrWhiteSpace(responseBody)
                ? $"HTTP {(int)response.StatusCode}"
                : TruncateForLog(responseBody.Trim(), 500);
            throw new InvalidOperationException(
                $"AMP API call {module}/{method} failed: {responseSummary}");
        }

        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return JsonDocument.Parse("{}");
        }

        return JsonDocument.Parse(responseBody);
    }

    private static bool TryGetAmpRequestSessionId(
        IDictionary<string, object?> payload,
        out string? sessionId)
    {
        sessionId = null;
        if (!payload.TryGetValue("SESSIONID", out var rawSessionId) ||
            rawSessionId is null)
        {
            return false;
        }

        sessionId = rawSessionId switch
        {
            string stringSessionId => Normalize(stringSessionId),
            JsonElement { ValueKind: JsonValueKind.String } jsonSessionId => Normalize(jsonSessionId.GetString()),
            _ => Normalize(rawSessionId.ToString())
        };

        return !string.IsNullOrWhiteSpace(sessionId);
    }

    private static void EnsureAmpApiResponseSucceeded(JsonElement rootElement, string module, string method)
    {
        if (rootElement.ValueKind == JsonValueKind.True)
        {
            return;
        }

        if (rootElement.ValueKind == JsonValueKind.False)
        {
            throw new InvalidOperationException(
                $"AMP API call {module}/{method} returned false.");
        }

        if (rootElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (TryReadBooleanProperty(rootElement, "success", out var success))
        {
            if (success)
            {
                return;
            }

            var successDescription = TryFindStringByPropertyNames(
                rootElement,
                "resultReason",
                "ResultReason",
                "Reason",
                "reason",
                "Description",
                "description",
                "Message",
                "message",
                "Title",
                "title") ?? "unknown AMP API error";
            throw new InvalidOperationException(
                $"AMP API call {module}/{method} returned failure: {successDescription}");
        }

        if (!TryReadBooleanProperty(rootElement, "Status", out var status))
        {
            var errorTitle = TryFindStringByPropertyNames(
                rootElement,
                "Title",
                "title");
            var errorMessage = TryFindStringByPropertyNames(
                rootElement,
                "Reason",
                "reason",
                "Message",
                "message",
                "Description",
                "description");

            if (!string.IsNullOrWhiteSpace(errorTitle) || !string.IsNullOrWhiteSpace(errorMessage))
            {
                throw new InvalidOperationException(
                    $"AMP API call {module}/{method} returned error: {string.Join(" - ", new[] { errorTitle, errorMessage }.Where(static value => !string.IsNullOrWhiteSpace(value)))}");
            }

            return;
        }

        if (status)
        {
            return;
        }

        var description = TryFindStringByPropertyNames(
            rootElement,
            "Reason",
            "reason",
            "Description",
            "description",
            "Message",
            "message",
            "Title",
            "title") ?? "unknown AMP API error";
        throw new InvalidOperationException(
            $"AMP API call {module}/{method} returned failure status: {description}");
    }

    private static bool TryReadBooleanProperty(JsonElement rootElement, string propertyName, out bool value)
    {
        value = false;
        if (rootElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in rootElement.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.True)
            {
                value = true;
                return true;
            }

            if (property.Value.ValueKind == JsonValueKind.False)
            {
                value = false;
                return true;
            }

            return false;
        }

        return false;
    }

    private static bool TryReadBooleanPropertyLoose(JsonElement rootElement, string propertyName, out bool value)
    {
        value = false;
        if (!TryGetPropertyIgnoreCase(rootElement, propertyName, out var propertyValue))
        {
            return false;
        }

        return TryReadBooleanLoose(propertyValue, out value);
    }

    private static bool TryReadInt32PropertyLoose(JsonElement rootElement, string propertyName, out int value)
    {
        value = 0;
        if (!TryGetPropertyIgnoreCase(rootElement, propertyName, out var propertyValue))
        {
            return false;
        }

        return TryReadInt32Loose(propertyValue, out value);
    }

    private static bool TryReadInt32Loose(JsonElement element, out int value)
    {
        value = 0;

        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetInt32(out value);
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            var rawValue = element.GetString();
            return int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        return false;
    }

    private static string? TryFindStringByPropertyNames(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (propertyNames.Any(name => string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    var foundValue = ReadJsonString(property.Value);
                    if (!string.IsNullOrWhiteSpace(foundValue))
                    {
                        return foundValue;
                    }
                }

                var nested = TryFindStringByPropertyNames(property.Value, propertyNames);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = TryFindStringByPropertyNames(item, propertyNames);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static IEnumerable<JsonElement> EnumerateJsonObjects(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                yield return element;
                foreach (var property in element.EnumerateObject())
                {
                    foreach (var nested in EnumerateJsonObjects(property.Value))
                    {
                        yield return nested;
                    }
                }

                yield break;
            }
            case JsonValueKind.Array:
            {
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nested in EnumerateJsonObjects(item))
                    {
                        yield return nested;
                    }
                }

                yield break;
            }
        }
    }

    private static string? ReadJsonString(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out var numericValue) => numericValue.ToString(CultureInfo.InvariantCulture),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static string? TryExtractAmpSessionId(JsonElement element)
    {
        var unwrapped = element;
        while (unwrapped.ValueKind == JsonValueKind.Object &&
               unwrapped.EnumerateObject().Count() == 1 &&
               unwrapped.EnumerateObject().First().Name.Equals("result", StringComparison.OrdinalIgnoreCase))
        {
            unwrapped = unwrapped.EnumerateObject().First().Value;
        }

        if (TryReadSessionCandidate(unwrapped, out var directSessionId))
        {
            return directSessionId;
        }

        if (unwrapped.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in unwrapped.EnumerateObject())
        {
            if (!property.Name.Equals("result", StringComparison.OrdinalIgnoreCase) &&
                !property.Name.Equals("data", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryReadSessionCandidate(property.Value, out var nestedSessionId))
            {
                return nestedSessionId;
            }
        }

        return null;
    }

    private static bool TryReadSessionCandidate(JsonElement value, out string sessionId)
    {
        sessionId = string.Empty;

        if (value.ValueKind == JsonValueKind.String)
        {
            var candidate = value.GetString()?.Trim();
            if (LooksLikeSessionId(candidate))
            {
                sessionId = candidate!;
                return true;
            }

            return false;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var candidate = property.Value.GetString()?.Trim();
            if (!LooksLikeSessionId(candidate))
            {
                continue;
            }

            sessionId = candidate!;
            return true;
        }

        return false;
    }

    private static bool LooksLikeSessionId(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        if (Guid.TryParse(candidate, out _))
        {
            return true;
        }

        if (candidate.Length is < 16 or > 128)
        {
            return false;
        }

        return candidate.All(static c =>
            char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
    }

    private static string NormalizeAmpApiBaseUrl(string ampApiUrl)
    {
        var normalized = ampApiUrl.Trim().TrimEnd('/');
        return normalized.EndsWith("/API", StringComparison.OrdinalIgnoreCase)
            ? normalized[..^4]
            : normalized;
    }

    private async Task ExecuteShellCommandAsync(
        string commandTemplate,
        string installRoot,
        string? modpackName,
        int commandId,
        int warningMinutes,
        string? requestedVersion,
        string? resolvedVersion,
        string? resolvedVersionDisplay,
        string phase,
        CancellationToken cancellationToken)
    {
        var resolvedCommand = ExpandCommandTemplate(
            commandTemplate,
            installRoot,
            modpackName,
            commandId,
            warningMinutes,
            requestedVersion,
            resolvedVersion,
            resolvedVersionDisplay);
        if (string.IsNullOrWhiteSpace(resolvedCommand))
        {
            throw new InvalidOperationException(
                $"Restart command for phase '{phase}' resolved to an empty command.");
        }

        _logger.LogInformation(
            "Running sync_modpack restart phase '{Phase}' for command #{CommandId}.",
            phase,
            commandId);

        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = installRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        if (OperatingSystem.IsWindows())
        {
            startInfo.FileName = "cmd.exe";
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(resolvedCommand);
        }
        else
        {
            startInfo.FileName = "/bin/bash";
            startInfo.ArgumentList.Add("-lc");
            startInfo.ArgumentList.Add(resolvedCommand);
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        using var process = new Process
        {
            StartInfo = startInfo
        };

        process.OutputDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                stdout.AppendLine(args.Data);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                stderr.AppendLine(args.Data);
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start restart phase '{phase}' process.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            throw;
        }

        if (process.ExitCode != 0)
        {
            var stderrText = stderr.ToString().Trim();
            throw new InvalidOperationException(
                $"Restart phase '{phase}' exited with code {process.ExitCode}. Error: {TruncateForLog(stderrText, 400)}");
        }

        var outputText = stdout.ToString().Trim();
        if (!string.IsNullOrWhiteSpace(outputText))
        {
            _logger.LogDebug(
                "sync_modpack restart phase '{Phase}' output: {Output}",
                phase,
                TruncateForLog(outputText, 400));
        }
    }

    private static string ExpandCommandTemplate(
        string commandTemplate,
        string installRoot,
        string? modpackName,
        int commandId,
        int warningMinutes,
        string? requestedVersion,
        string? resolvedVersion,
        string? resolvedVersionDisplay)
    {
        var targetVersionDisplay = Normalize(resolvedVersionDisplay) ??
                                   Normalize(resolvedVersion) ??
                                   "Latest";
        return commandTemplate
            .Replace("{installRootPath}", installRoot, StringComparison.Ordinal)
            .Replace("{modpackName}", modpackName ?? string.Empty, StringComparison.Ordinal)
            .Replace("{commandId}", commandId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{warningMinutes}", warningMinutes.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{requestedVersion}", requestedVersion ?? string.Empty, StringComparison.Ordinal)
            .Replace("{targetVersion}", resolvedVersion ?? string.Empty, StringComparison.Ordinal)
            .Replace("{targetVersionDisplay}", targetVersionDisplay, StringComparison.Ordinal);
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Ignore process kill failures during cancellation handling.
        }
    }

    private static string TruncateForLog(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength];
    }

    private SyncApplyStats ApplyFullSync(
        string sourceRoot,
        string installRoot,
        IReadOnlyList<string> configuredPreservedPaths)
    {
        var stats = new SyncApplyStats();

        foreach (var sourceEntry in Directory.EnumerateFileSystemEntries(sourceRoot))
        {
            var entryName = Path.GetFileName(sourceEntry);
            if (string.IsNullOrWhiteSpace(entryName))
            {
                continue;
            }

            if (IsPreservedPath(entryName, configuredPreservedPaths))
            {
                stats.SkippedPreservedEntries++;
                continue;
            }

            var targetEntry = Path.Combine(installRoot, entryName);
            if (File.Exists(targetEntry) || Directory.Exists(targetEntry))
            {
                DeleteFileSystemEntry(targetEntry);
                stats.ReplacedTopLevelEntries++;
            }

            CopyFileSystemEntry(sourceEntry, targetEntry, stats);
        }

        return stats;
    }

    private SyncApplyStats ApplyAmpManagedFullSync(
        string sourceRoot,
        string installRoot,
        IReadOnlyList<string> configuredPreservedPaths)
    {
        var stats = new SyncApplyStats();

        foreach (var sourceEntry in Directory.EnumerateFileSystemEntries(sourceRoot))
        {
            var entryName = Path.GetFileName(sourceEntry);
            if (string.IsNullOrWhiteSpace(entryName))
            {
                continue;
            }

            if (IsPreservedPath(entryName, configuredPreservedPaths))
            {
                stats.SkippedPreservedEntries++;
                continue;
            }

            var targetEntry = Path.Combine(installRoot, entryName);
            if (IsAmpManagedGeneratedTopLevelPath(entryName) &&
                (File.Exists(targetEntry) || Directory.Exists(targetEntry)))
            {
                stats.SkippedPreservedEntries++;
                continue;
            }

            var shouldReplaceTopLevel = ShouldReplaceAmpManagedTopLevelEntry(entryName);
            if (shouldReplaceTopLevel &&
                (File.Exists(targetEntry) || Directory.Exists(targetEntry)))
            {
                DeleteFileSystemEntry(targetEntry);
                stats.ReplacedTopLevelEntries++;
                CopyFileSystemEntry(sourceEntry, targetEntry, stats);
                continue;
            }

            CopyFileSystemEntryOverlay(sourceEntry, targetEntry, stats);
        }

        return stats;
    }

    private SyncApplyStats ApplyOverlay(
        string sourceRoot,
        string installRoot,
        IReadOnlyList<string> configuredPreservedPaths)
    {
        var stats = new SyncApplyStats();

        foreach (var sourceFile in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceRoot, sourceFile);
            if (IsPreservedPath(relativePath, configuredPreservedPaths))
            {
                stats.SkippedPreservedEntries++;
                continue;
            }

            var targetFile = Path.Combine(installRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            File.Copy(sourceFile, targetFile, overwrite: true);

            stats.CopiedFiles++;
            stats.CopiedBytes += new FileInfo(sourceFile).Length;
        }

        return stats;
    }

    private SyncApplyStats ApplyOverrideWithDeletes(
        string overrideRoot,
        string installRoot,
        IReadOnlyList<string> configuredPreservedPaths)
    {
        var stats = new SyncApplyStats();

        foreach (var deleteMarkerFile in Directory.EnumerateFiles(overrideRoot, "*.DELETE", SearchOption.AllDirectories))
        {
            stats.DeleteMarkersProcessed++;

            var relativeMarkerPath = Path.GetRelativePath(overrideRoot, deleteMarkerFile)
                .Replace('\\', '/')
                .TrimStart('/');

            if (relativeMarkerPath.Length <= ".DELETE".Length)
            {
                stats.DeleteMarkersSkippedInvalid++;
                continue;
            }

            var relativeDeleteTargetPattern = relativeMarkerPath[..^".DELETE".Length];
            if (string.IsNullOrWhiteSpace(relativeDeleteTargetPattern))
            {
                stats.DeleteMarkersSkippedInvalid++;
                continue;
            }

            relativeDeleteTargetPattern = ExpandDeleteMarkerWildcards(relativeDeleteTargetPattern);

            if (IsPreservedPath(relativeDeleteTargetPattern, configuredPreservedPaths))
            {
                stats.DeleteMarkersSkippedPreserved++;
                _logger.LogInformation(
                    "Skipping delete marker for preserved path: {DeleteTargetPattern}",
                    relativeDeleteTargetPattern);
                continue;
            }

            stats.DeleteMarkerDeletedEntries += DeleteTargetsFromMarkerPattern(installRoot, relativeDeleteTargetPattern);
        }

        foreach (var sourceFile in Directory.EnumerateFiles(overrideRoot, "*", SearchOption.AllDirectories))
        {
            if (sourceFile.EndsWith(".DELETE", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(overrideRoot, sourceFile);
            if (IsPreservedPath(relativePath, configuredPreservedPaths))
            {
                stats.SkippedPreservedEntries++;
                continue;
            }

            var targetFile = Path.Combine(installRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            File.Copy(sourceFile, targetFile, overwrite: true);

            stats.CopiedFiles++;
            stats.CopiedBytes += new FileInfo(sourceFile).Length;
        }

        return stats;
    }

    private IReadOnlyList<PreservedPathBackup> BackupConfiguredPreservedPaths(
        string installRoot,
        IReadOnlyList<string> configuredPreservedPaths,
        string backupRoot)
    {
        if (configuredPreservedPaths.Count == 0)
        {
            return [];
        }

        var backups = new List<PreservedPathBackup>();
        foreach (var preservedPath in configuredPreservedPaths)
        {
            var sourcePath = ResolveRelativePathUnderRoot(installRoot, preservedPath, "preserved path");
            if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
            {
                continue;
            }

            var backupPath = ResolveRelativePathUnderRoot(backupRoot, preservedPath, "preserved backup path");
            CopyFileSystemEntry(sourcePath, backupPath, new SyncApplyStats());
            backups.Add(new PreservedPathBackup(preservedPath));
        }

        return backups;
    }

    private SyncApplyStats RestoreConfiguredPreservedPaths(
        string installRoot,
        string backupRoot,
        IReadOnlyList<PreservedPathBackup> preservedPathBackups)
    {
        var stats = new SyncApplyStats();
        foreach (var preservedPathBackup in preservedPathBackups)
        {
            var backupPath = ResolveRelativePathUnderRoot(
                backupRoot,
                preservedPathBackup.RelativePath,
                "preserved backup path");
            if (!File.Exists(backupPath) && !Directory.Exists(backupPath))
            {
                continue;
            }

            var targetPath = ResolveRelativePathUnderRoot(
                installRoot,
                preservedPathBackup.RelativePath,
                "preserved path");
            if (File.Exists(targetPath) || Directory.Exists(targetPath))
            {
                DeleteFileSystemEntry(targetPath);
            }

            CopyFileSystemEntry(backupPath, targetPath, stats);
        }

        return stats;
    }

    private int DeleteTargetsFromMarkerPattern(string installRoot, string relativeDeleteTargetPattern)
    {
        var normalizedPattern = relativeDeleteTargetPattern.Replace('\\', '/').TrimStart('/');
        if (normalizedPattern.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Delete marker path traversal is not allowed: {relativeDeleteTargetPattern}");
        }

        var separatorIndex = normalizedPattern.LastIndexOf('/');
        var directoryPart = separatorIndex >= 0
            ? normalizedPattern[..separatorIndex]
            : string.Empty;
        var filePattern = separatorIndex >= 0
            ? normalizedPattern[(separatorIndex + 1)..]
            : normalizedPattern;

        if (string.IsNullOrWhiteSpace(filePattern))
        {
            return 0;
        }

        if (ContainsWildcard(directoryPart))
        {
            throw new InvalidOperationException(
                $"Delete marker directory wildcards are not supported: {relativeDeleteTargetPattern}");
        }

        var baseDirectory = string.IsNullOrEmpty(directoryPart)
            ? installRoot
            : Path.Combine(installRoot, directoryPart.Replace('/', Path.DirectorySeparatorChar));
        var normalizedBaseDirectory = Path.GetFullPath(baseDirectory);
        var normalizedInstallRootPath = Path.GetFullPath(installRoot);
        var normalizedInstallRoot = EnsureTrailingDirectorySeparator(normalizedInstallRootPath);
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!normalizedBaseDirectory.StartsWith(normalizedInstallRoot, pathComparison) &&
            !string.Equals(normalizedBaseDirectory, normalizedInstallRootPath, pathComparison))
        {
            throw new InvalidOperationException(
                $"Delete marker resolved outside install root: {relativeDeleteTargetPattern}");
        }

        if (!ContainsWildcard(filePattern))
        {
            var exactTargetPath = Path.Combine(normalizedBaseDirectory, filePattern);
            var normalizedExactTargetPath = Path.GetFullPath(exactTargetPath);
            if (!normalizedExactTargetPath.StartsWith(normalizedInstallRoot, pathComparison) &&
                !string.Equals(normalizedExactTargetPath, normalizedInstallRootPath, pathComparison))
            {
                throw new InvalidOperationException(
                    $"Delete marker resolved outside install root: {relativeDeleteTargetPattern}");
            }

            if (!File.Exists(normalizedExactTargetPath) && !Directory.Exists(normalizedExactTargetPath))
            {
                return 0;
            }

            DeleteFileSystemEntry(normalizedExactTargetPath);
            return 1;
        }

        if (!Directory.Exists(normalizedBaseDirectory))
        {
            return 0;
        }

        var deletedEntries = 0;
        foreach (var candidatePath in Directory.EnumerateFileSystemEntries(normalizedBaseDirectory))
        {
            var candidateName = Path.GetFileName(candidatePath);
            if (string.IsNullOrEmpty(candidateName))
            {
                continue;
            }

            if (!FileSystemName.MatchesSimpleExpression(filePattern, candidateName, ignoreCase: true))
            {
                continue;
            }

            if (!File.Exists(candidatePath) && !Directory.Exists(candidatePath))
            {
                continue;
            }

            DeleteFileSystemEntry(candidatePath);
            deletedEntries++;
        }

        return deletedEntries;
    }

    private static bool ContainsWildcard(string value)
    {
        return value.IndexOfAny(['*', '?']) >= 0;
    }

    private static string ExpandDeleteMarkerWildcards(string value)
    {
        return value
            .Replace("__STAR__", "*", StringComparison.OrdinalIgnoreCase)
            .Replace("__Q__", "?", StringComparison.OrdinalIgnoreCase);
    }

    private static void CopyFileSystemEntry(string sourcePath, string destinationPath, SyncApplyStats stats)
    {
        if (File.Exists(sourcePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath, overwrite: true);
            stats.CopiedFiles++;
            stats.CopiedBytes += new FileInfo(sourcePath).Length;
            return;
        }

        if (!Directory.Exists(sourcePath))
        {
            throw new InvalidOperationException($"Source entry not found: {sourcePath}");
        }

        CopyDirectoryRecursive(sourcePath, destinationPath, stats);
    }

    private static void CopyFileSystemEntryOverlay(string sourcePath, string destinationPath, SyncApplyStats stats)
    {
        if (File.Exists(sourcePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath, overwrite: true);
            stats.CopiedFiles++;
            stats.CopiedBytes += new FileInfo(sourcePath).Length;
            return;
        }

        if (!Directory.Exists(sourcePath))
        {
            throw new InvalidOperationException($"Source entry not found: {sourcePath}");
        }

        Directory.CreateDirectory(destinationPath);
        foreach (var sourceFile in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourcePath, sourceFile);
            var targetFile = Path.Combine(destinationPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            File.Copy(sourceFile, targetFile, overwrite: true);
            stats.CopiedFiles++;
            stats.CopiedBytes += new FileInfo(sourceFile).Length;
        }
    }

    private static void CopyDirectoryRecursive(string sourceDirectory, string destinationDirectory, SyncApplyStats stats)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var sourceFile in Directory.EnumerateFiles(sourceDirectory))
        {
            var destinationFile = Path.Combine(destinationDirectory, Path.GetFileName(sourceFile));
            File.Copy(sourceFile, destinationFile, overwrite: true);
            stats.CopiedFiles++;
            stats.CopiedBytes += new FileInfo(sourceFile).Length;
        }

        foreach (var sourceSubDirectory in Directory.EnumerateDirectories(sourceDirectory))
        {
            var destinationSubDirectory = Path.Combine(destinationDirectory, Path.GetFileName(sourceSubDirectory));
            CopyDirectoryRecursive(sourceSubDirectory, destinationSubDirectory, stats);
        }
    }

    private static int ExtractZipArchive(string archivePath, string destinationDirectory)
    {
        var extractionRoot = Path.GetFullPath(destinationDirectory);
        var extractionRootWithSeparator = EnsureTrailingDirectorySeparator(extractionRoot);
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var extractedFiles = 0;

        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.FullName))
            {
                continue;
            }

            var normalizedEntryPath = entry.FullName.Replace('\\', '/');
            var destinationPath = Path.GetFullPath(Path.Combine(extractionRoot, normalizedEntryPath));
            if (!destinationPath.StartsWith(extractionRootWithSeparator, pathComparison))
            {
                throw new InvalidOperationException(
                    $"Zip entry path traversal is not allowed: {entry.FullName}");
            }

            if (normalizedEntryPath.EndsWith("/", StringComparison.Ordinal))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            entry.ExtractToFile(destinationPath, overwrite: true);
            extractedFiles++;
        }

        return extractedFiles;
    }

    private static string ResolveExtractedContentRoot(string extractedDirectory)
    {
        var files = Directory.GetFiles(extractedDirectory);
        var directories = Directory.GetDirectories(extractedDirectory);

        if (files.Length == 0 && directories.Length == 1)
        {
            return directories[0];
        }

        return extractedDirectory;
    }

    private static string ResolveWorkingRoot()
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "modpack-sync"));
    }

    private static string? ResolveOverrideDirectory(string installRoot, string? overrideDirectory)
    {
        if (string.IsNullOrWhiteSpace(overrideDirectory))
        {
            return null;
        }

        return Path.IsPathRooted(overrideDirectory)
            ? Path.GetFullPath(overrideDirectory)
            : Path.GetFullPath(Path.Combine(installRoot, overrideDirectory));
    }

    private static string ResolveRelativePathUnderRoot(string rootPath, string relativePath, string fieldName)
    {
        var fullRootPath = Path.GetFullPath(rootPath);
        var fullRootPathWithSeparator = EnsureTrailingDirectorySeparator(fullRootPath);
        var combinedPath = Path.GetFullPath(Path.Combine(fullRootPath, relativePath));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!combinedPath.StartsWith(fullRootPathWithSeparator, comparison) &&
            !string.Equals(combinedPath, fullRootPath, comparison))
        {
            throw new InvalidOperationException(
                $"{fieldName} must stay within the configured root: {relativePath}");
        }

        return combinedPath;
    }

    private static void DeleteGeneratedServerPackPath(string generatedRoot, string relativePath)
    {
        var targetPath = ResolveRelativePathUnderRoot(
            generatedRoot,
            relativePath,
            "serverPackExcludedPaths");
        if (File.Exists(targetPath))
        {
            File.Delete(targetPath);
            return;
        }

        if (Directory.Exists(targetPath))
        {
            Directory.Delete(targetPath, recursive: true);
        }
    }

    private static void DeleteFileSystemEntry(string path)
    {
        if (File.Exists(path) && !Directory.Exists(path))
        {
            File.Delete(path);
            return;
        }

        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Keep working artifacts on disk for troubleshooting when cleanup fails.
        }
    }

    private static bool TryParsePayload(
        string payloadJson,
        out SyncModpackPayload payload,
        out string errorMessage)
    {
        try
        {
            payload = JsonSerializer.Deserialize<SyncModpackPayload>(payloadJson, JsonOptions) ?? new SyncModpackPayload();

            if (payload.Modpack is null)
            {
                errorMessage = "sync_modpack requires a 'modpack' object.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(payload.Modpack.InstallRootPath))
            {
                errorMessage = "sync_modpack requires modpack.installRootPath.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(payload.Modpack.Provider))
            {
                errorMessage = "sync_modpack requires modpack.provider.";
                return false;
            }

            errorMessage = string.Empty;
            return true;
        }
        catch (JsonException)
        {
            payload = new SyncModpackPayload();
            errorMessage = "sync_modpack payload is invalid JSON.";
            return false;
        }
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? NormalizeVersionSelector(string? value)
    {
        var normalized = Normalize(value);
        return string.Equals(normalized, "latest", StringComparison.OrdinalIgnoreCase)
            ? null
            : normalized;
    }

    private static string? ResolveVersionReference(ResolvedServerPackSource source)
    {
        if (source.ServerPackFileId.HasValue)
        {
            return source.ServerPackFileId.Value.ToString(CultureInfo.InvariantCulture);
        }

        if (source.ParentFileId.HasValue)
        {
            return source.ParentFileId.Value.ToString(CultureInfo.InvariantCulture);
        }

        if (source.FtbVersionId.HasValue)
        {
            return source.FtbVersionId.Value.ToString(CultureInfo.InvariantCulture);
        }

        return Normalize(source.SelectedVersion);
    }

    private static string? ResolveVersionDisplayReference(ResolvedServerPackSource source)
    {
        return Normalize(source.DisplayVersion) ??
               ResolveDisplayVersionFromSelector(source.SelectedVersion);
    }

    private static string? ResolveCurseForgeDisplayVersion(
        CurseForgeFileEntry parentFile,
        CurseForgeFileEntry serverPackFile,
        string? selectedVersion)
    {
        foreach (var candidate in new[]
                 {
                     parentFile.DisplayName,
                     parentFile.FileName,
                     serverPackFile.DisplayName,
                     serverPackFile.FileName
                 })
        {
            var resolved = TryExtractHumanReadableVersion(candidate);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }
        }

        return ResolveDisplayVersionFromSelector(selectedVersion);
    }

    private static string? ResolveCurseForgeClientDisplayVersion(
        CurseForgeFileEntry parentFile,
        string? selectedVersion)
    {
        foreach (var candidate in new[] { parentFile.DisplayName, parentFile.FileName })
        {
            var resolved = TryExtractHumanReadableVersion(candidate);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }
        }

        return ResolveDisplayVersionFromSelector(selectedVersion);
    }

    private static string? ResolveDisplayVersionFromDirectUrl(string explicitServerPackUrl, string? selectedVersion)
    {
        if (!Uri.TryCreate(explicitServerPackUrl, UriKind.Absolute, out var parsedUrl))
        {
            return ResolveDisplayVersionFromSelector(selectedVersion);
        }

        var fileName = Path.GetFileName(parsedUrl.AbsolutePath);
        return TryExtractHumanReadableVersion(fileName) ??
               ResolveDisplayVersionFromSelector(selectedVersion);
    }

    private static string? ResolveDisplayVersionFromSelector(string? selectedVersion)
    {
        var normalized = Normalize(selectedVersion);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        if (int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return null;
        }

        return TryExtractHumanReadableVersion(normalized) ??
               (normalized.Contains('.', StringComparison.Ordinal) ? normalized : null);
    }

    private static string? TryExtractHumanReadableVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var matches = HumanVersionRegex.Matches(value);
        return matches.Count == 0
            ? null
            : matches[^1].Value;
    }

    private static bool TryParseCurseForgeProjectId(string? sourceReference, out int projectId)
    {
        projectId = 0;
        if (string.IsNullOrWhiteSpace(sourceReference))
        {
            return false;
        }

        var normalized = sourceReference.Trim();
        if (int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out projectId) && projectId > 0)
        {
            return true;
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var pathSegments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in pathSegments.Reverse())
        {
            if (int.TryParse(segment, NumberStyles.Integer, CultureInfo.InvariantCulture, out projectId) && projectId > 0)
            {
                return true;
            }
        }

        projectId = 0;
        return false;
    }

    private static bool TryParseFtbPackId(string? sourceReference, out int packId)
    {
        return TryParseFtbNumericReference(sourceReference, out packId);
    }

    private static bool TryParseFtbVersionId(string? versionReference, out int versionId)
    {
        return TryParseFtbNumericReference(versionReference, out versionId);
    }

    private static bool TryParseFtbNumericReference(string? value, out int parsedId)
    {
        parsedId = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        if (int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedId) && parsedId > 0)
        {
            return true;
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            return false;
        }

        foreach (var segment in uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            if (TryParseLeadingPositiveInteger(segment, out parsedId))
            {
                return true;
            }
        }

        parsedId = 0;
        return false;
    }

    private static bool TryParseLeadingPositiveInteger(string value, out int parsedId)
    {
        parsedId = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var digitCount = 0;
        while (digitCount < value.Length && char.IsDigit(value[digitCount]))
        {
            digitCount++;
        }

        if (digitCount == 0)
        {
            return false;
        }

        return int.TryParse(value[..digitCount], NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedId) &&
               parsedId > 0;
    }

    private static IReadOnlyList<string> NormalizeConfiguredPreservedPaths(IEnumerable<string>? preservedPaths)
    {
        if (preservedPaths is null)
        {
            return [];
        }

        var normalizedPaths = new List<string>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var preservedPath in preservedPaths)
        {
            var normalizedPath = NormalizeConfiguredPreservedPath(preservedPath);
            if (normalizedPath is null || !seenPaths.Add(normalizedPath))
            {
                continue;
            }

            normalizedPaths.Add(normalizedPath);
        }

        return normalizedPaths;
    }

    private static string? NormalizeConfiguredPreservedPath(string? preservedPath)
    {
        if (string.IsNullOrWhiteSpace(preservedPath))
        {
            return null;
        }

        var normalizedPath = preservedPath.Replace('\\', '/').Trim();
        if (normalizedPath.StartsWith("/", StringComparison.Ordinal) ||
            Path.IsPathRooted(normalizedPath))
        {
            throw new InvalidOperationException(
                $"Configured preserved path must be relative to the install root: {preservedPath}");
        }

        normalizedPath = normalizedPath.TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return null;
        }

        if (normalizedPath.IndexOfAny(['*', '?']) >= 0)
        {
            throw new InvalidOperationException(
                $"Configured preserved path cannot contain wildcard characters: {preservedPath}");
        }

        var pathSegments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (pathSegments.Length == 0 ||
            pathSegments.Any(segment => segment is "." or ".."))
        {
            throw new InvalidOperationException(
                $"Configured preserved path must be a valid relative path: {preservedPath}");
        }

        return string.Join('/', pathSegments);
    }

    private static bool IsPreservedPath(
        string relativePath,
        IReadOnlyList<string>? configuredPreservedPaths = null)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        if (IsDefaultPreservedPath(relativePath))
        {
            return true;
        }

        return IsConfiguredPreservedPath(relativePath, configuredPreservedPaths);
    }

    private static bool IsDefaultPreservedPath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        var firstSeparatorIndex = normalized.IndexOf('/');
        var topLevel = firstSeparatorIndex >= 0
            ? normalized[..firstSeparatorIndex]
            : normalized;

        return PreservedTopLevelPatterns.Any(pattern => MatchesPreservedPattern(topLevel, pattern));
    }

    private static bool IsConfiguredPreservedPath(
        string relativePath,
        IReadOnlyList<string>? configuredPreservedPaths)
    {
        if (configuredPreservedPaths is null || configuredPreservedPaths.Count == 0)
        {
            return false;
        }

        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        foreach (var configuredPreservedPath in configuredPreservedPaths)
        {
            if (string.Equals(normalized, configuredPreservedPath, StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith(configuredPreservedPath + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesPreservedPattern(string entryName, string pattern)
    {
        if (pattern.EndsWith('*'))
        {
            var prefix = pattern[..^1];
            return entryName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(entryName, pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldReplaceAmpManagedTopLevelEntry(string entryName)
    {
        return AmpManagedReplaceTopLevelPatterns.Any(pattern =>
            MatchesPreservedPattern(entryName, pattern));
    }

    private static bool IsAmpManagedGeneratedTopLevelPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        if (normalized.Contains('/', StringComparison.Ordinal))
        {
            return false;
        }

        return normalized.EndsWith("-shim.jar", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "unix_args.txt", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "win_args.txt", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "user_jvm_args.txt", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "run.sh", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "run.bat", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "startserver.sh", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "startserver.bat", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsInvariant(string? source, string value)
    {
        return !string.IsNullOrWhiteSpace(source) &&
               source.Contains(value, StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeServerPack(CurseForgeFileEntry file)
    {
        return ContainsInvariant(file.DisplayName, "server") ||
               ContainsInvariant(file.FileName, "server");
    }

    private static void EnsureAbsoluteHttpUrl(string url, string fieldName)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsedUrl) ||
            (parsedUrl.Scheme != Uri.UriSchemeHttp && parsedUrl.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"{fieldName} must be an absolute HTTP/HTTPS URL.");
        }
    }

    private static string EnsureTrailingDirectorySeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }

    private static void EnsureDownloadClientHeaders(HttpClient client)
    {
        if (!client.DefaultRequestHeaders.UserAgent.Any())
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MCAgent/1.0");
        }
    }

    private sealed class RestartPlan
    {
        public bool Enabled { get; }
        public string? WarningCommandTemplate { get; }
        public string? StopCommandTemplate { get; }
        public string? StartCommandTemplate { get; }
        public string? Reason { get; }

        private RestartPlan(
            bool enabled,
            string? warningCommandTemplate,
            string? stopCommandTemplate,
            string? startCommandTemplate,
            string? reason)
        {
            Enabled = enabled;
            WarningCommandTemplate = warningCommandTemplate;
            StopCommandTemplate = stopCommandTemplate;
            StartCommandTemplate = startCommandTemplate;
            Reason = reason;
        }

        public static RestartPlan CreateEnabled(
            string? warningCommandTemplate,
            string stopCommandTemplate,
            string startCommandTemplate)
        {
            return new RestartPlan(
                enabled: true,
                warningCommandTemplate,
                stopCommandTemplate,
                startCommandTemplate,
                reason: null);
        }

        public static RestartPlan Disabled(string reason)
        {
            return new RestartPlan(
                enabled: false,
                warningCommandTemplate: null,
                stopCommandTemplate: null,
                startCommandTemplate: null,
                reason);
        }
    }

    private sealed class RestartExecutionState
    {
        public string Mode { get; private set; } = "none";
        public string Provider { get; private set; } = "shell";
        public bool OrchestrationEnabled { get; private set; }
        public string? DisabledReason { get; private set; }
        public bool SkipWarnings { get; private set; }
        public int WarningMinutes { get; private set; }
        public bool WarningCommandExecuted { get; set; }
        public int WarningWaitSeconds { get; set; }
        public bool StopCommandExecuted { get; set; }
        public bool StartCommandExecuted { get; set; }
        public bool AmpUpdateApplicationExecuted { get; set; }
        public int AmpConfigValuesApplied { get; set; }
        public bool AmpAutoLoaderDetected { get; set; }
        public string? AmpAutoLoaderKind { get; set; }
        public string? AmpAutoLoaderVersion { get; set; }
        public string? AmpAutoLoaderId { get; set; }
        public string? AmpAutoMinecraftVersion { get; set; }
        public IReadOnlyDictionary<string, string>? AmpAutoStartupSettings { get; set; }
        public bool AmpAutoLoaderConfigApplied { get; set; }
        public string? AmpAutoLoaderSettingNode { get; set; }
        public string? AmpAutoLoaderSettingValue { get; set; }
        public string? AmpAutoLoaderError { get; set; }

        public static RestartExecutionState Create(
            string mode,
            string provider,
            bool orchestrationEnabled,
            string? disabledReason,
            bool skipWarnings,
            int warningMinutes)
        {
            return new RestartExecutionState
            {
                Mode = mode,
                Provider = provider,
                OrchestrationEnabled = orchestrationEnabled,
                DisabledReason = disabledReason,
                SkipWarnings = skipWarnings,
                WarningMinutes = warningMinutes
            };
        }

        public object ToPayload()
        {
            return new
            {
                mode = Mode,
                provider = Provider,
                orchestrated = OrchestrationEnabled,
                disabledReason = DisabledReason,
                skipWarnings = SkipWarnings,
                warningMinutes = WarningMinutes,
                warningCommandExecuted = WarningCommandExecuted,
                warningWaitSeconds = WarningWaitSeconds,
                stopCommandExecuted = StopCommandExecuted,
                startCommandExecuted = StartCommandExecuted,
                ampUpdateApplicationExecuted = AmpUpdateApplicationExecuted,
                ampConfigValuesApplied = AmpConfigValuesApplied,
                ampAutoLoaderDetected = AmpAutoLoaderDetected,
                ampAutoLoaderKind = AmpAutoLoaderKind,
                ampAutoLoaderVersion = AmpAutoLoaderVersion,
                ampAutoLoaderId = AmpAutoLoaderId,
                ampAutoMinecraftVersion = AmpAutoMinecraftVersion,
                ampAutoStartupSettings = AmpAutoStartupSettings,
                ampAutoLoaderConfigApplied = AmpAutoLoaderConfigApplied,
                ampAutoLoaderSettingNode = AmpAutoLoaderSettingNode,
                ampAutoLoaderSettingValue = AmpAutoLoaderSettingValue,
                ampAutoLoaderError = AmpAutoLoaderError
            };
        }
    }

    private sealed class AmpApiRestartPlan
    {
        public AmpApiRestartPlan(
            string apiBaseUrl,
            string username,
            string password,
            string token,
            bool rememberMe,
            string? instanceName = null)
        {
            ApiBaseUrl = apiBaseUrl;
            Username = username;
            Password = password;
            Token = token;
            RememberMe = rememberMe;
            InstanceName = string.IsNullOrWhiteSpace(instanceName) ? null : instanceName.Trim();
        }

        public string ApiBaseUrl { get; }
        public string Username { get; }
        public string Password { get; }
        public string Token { get; }
        public bool RememberMe { get; }
        public string? InstanceName { get; }
        public string? WarningMessageTemplate { get; init; }
        public bool IsControllerMode => !string.IsNullOrWhiteSpace(InstanceName);
        public AmpControllerInstanceReference? InstanceReference { get; set; }
        public string? SessionId { get; set; }
        public IDictionary<string, string> ProxySessionIds { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record AmpControllerInstanceReference(
        string? InstanceName,
        string? FriendlyName,
        string? InstanceId,
        string? TargetId,
        bool? Running);

    private sealed class SyncApplyStats
    {
        public int CopiedFiles { get; set; }
        public long CopiedBytes { get; set; }
        public int ReplacedTopLevelEntries { get; set; }
        public int SkippedPreservedEntries { get; set; }
        public int DeleteMarkersProcessed { get; set; }
        public int DeleteMarkerDeletedEntries { get; set; }
        public int DeleteMarkersSkippedPreserved { get; set; }
        public int DeleteMarkersSkippedInvalid { get; set; }
        public int BackedUpPreservedPaths { get; set; }
        public int RestoredPreservedPaths { get; set; }
    }

    private sealed class SyncModpackPayload
    {
        public SyncModpackDetails? Modpack { get; set; }
        public SyncModpackOptions? Options { get; set; }
        public SyncMetadata? Metadata { get; set; }
    }

    private sealed class SyncModpackDetails
    {
        public int? Id { get; set; }
        public string? Name { get; set; }
        public string? Provider { get; set; }
        public string? SourceReference { get; set; }
        public string? ServerPackUrl { get; set; }
        public bool? BuildServerPackFromClientFiles { get; set; }
        public string[]? ServerPackExcludedPaths { get; set; }
        public int[]? ServerPackExcludedCurseForgeProjectIds { get; set; }
        public string? VersionLock { get; set; }
        public string? CurrentVersion { get; set; }
        public string? InstallRootPath { get; set; }
        public string? OverrideDirectory { get; set; }
        public string[]? PreservedPaths { get; set; }
        public string? RestartMode { get; set; }
        public int? WarningMinutes { get; set; }
        public string? AmpInstanceName { get; set; }
        public string? AmpApiUrl { get; set; }
        public string? AmpConfigValuesJson { get; set; }
    }

    private sealed class SyncModpackOptions
    {
        public string? RequestedVersion { get; set; }
        public bool? ForceFullSync { get; set; }
        public bool? SkipWarnings { get; set; }
        public bool? IgnoreCurrentVersion { get; set; }
    }

    private sealed class SyncMetadata
    {
        public string? QueuedBy { get; set; }
        public DateTime? QueuedAtUtc { get; set; }
    }

    private sealed class CurseForgeListResponse<TItem>
    {
        public List<TItem>? Data { get; set; }
    }

    private sealed class CurseForgeFileEntry
    {
        public int Id { get; set; }
        public int Status { get; set; }
        public string? DisplayName { get; set; }
        public string? FileName { get; set; }
        public DateTimeOffset DateCreated { get; set; }
        public bool HasServerPack { get; set; }
        public int AdditionalServerPackFilesCount { get; set; }
    }

    private sealed class CurseForgeManifest
    {
        public string? Overrides { get; set; }
        public List<CurseForgeManifestFile>? Files { get; set; }
    }

    private sealed class CurseForgeManifestFile
    {
        public int ProjectId { get; set; }
        public int FileId { get; set; }
        public bool? Required { get; set; }
    }

    private sealed class FtbPublicModpackResponse
    {
        public string? Status { get; set; }
        public List<FtbVersionEntry>? Versions { get; set; }
    }

    private sealed class FtbVersionEntry
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public string? Type { get; set; }
        public long Updated { get; set; }
        public long Released { get; set; }
        public bool Private { get; set; }
        public List<FtbTargetEntry>? Targets { get; set; }
    }

    private sealed class FtbTargetEntry
    {
        public string? Name { get; set; }
        public string? Version { get; set; }
        public string? Type { get; set; }
    }

    private readonly record struct MaterializedServerPackSource(
        string ContentRoot,
        long DownloadedBytes,
        int ExtractedFileCount);

private readonly record struct ResolvedServerPackSource(
    string? DownloadUrl,
    string SourceKind,
    int? ProjectId,
    int? ParentFileId,
    int? ServerPackFileId,
    int? FtbPackId,
    int? FtbVersionId,
    DetectedMinecraftRuntime? DetectedRuntime,
    string? DisplayVersion,
    string? SelectedVersion,
    string[]? GeneratedServerPackExcludedPaths,
    int[]? GeneratedServerPackExcludedCurseForgeProjectIds);

private sealed record DetectedMinecraftRuntime(
    string? MinecraftVersion,
    string LoaderId,
    string LoaderKind,
    string LoaderVersion);

private sealed record AutoAmpLoaderConfigApplyResult(
    AutoAmpLoaderConfigSelection PrimarySelection,
    IReadOnlyList<string> AppliedSettingNodes);

private sealed record AutoAmpLoaderConfigSelection(
    string SettingNode,
    string SettingValue,
    string LoaderKind,
    string LoaderVersion,
    string LoaderId);

private sealed record PreservedPathBackup(
    string RelativePath);
}
