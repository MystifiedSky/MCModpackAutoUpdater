using System.Text.Json;
using MCModpackAutoUpdater.Data;

namespace MCModpackAutoUpdater.Services;

public static class SyncModpackCommandPayloadBuilder
{
    public static string Build(
        UpdaterModpackProfile modpack,
        UpdaterAgentNode agent,
        string? requestedVersion,
        bool forceFullSync,
        bool skipWarnings,
        bool ignoreCurrentVersion,
        string queuedBy,
        DateTime queuedAtUtc)
    {
        var payload = new
        {
            type = "sync_modpack",
            modpack = new
            {
                id = modpack.Id,
                name = modpack.Name,
                provider = modpack.Provider,
                sourceReference = modpack.SourceReference,
                serverPackUrl = modpack.ServerPackUrl,
                buildServerPackFromClientFiles = modpack.BuildServerPackFromClientFiles,
                serverPackExcludedPaths = StandaloneStringListParser.ResolveStringList(null, modpack.ServerPackExcludedPaths),
                serverPackExcludedCurseForgeProjectIds = StandaloneStringListParser.ResolvePositiveIntegerList(null, modpack.ServerPackExcludedCurseForgeProjectIds),
                versionLock = modpack.VersionLock,
                currentVersion = modpack.CurrentVersion,
                installRootPath = modpack.InstallRootPath,
                overrideDirectory = modpack.OverrideDirectory,
                preservedPaths = StandaloneStringListParser.ResolveStringList(null, modpack.PreservedPaths),
                restartMode = modpack.RestartMode,
                warningMinutes = modpack.WarningMinutes,
                ampInstanceName = modpack.AmpInstanceName,
                ampApiUrl = modpack.AmpApiUrl,
                ampConfigValuesJson = modpack.AmpConfigValuesJson
            },
            targetAgent = new
            {
                id = agent.Id,
                name = agent.Name,
                host = agent.Host,
                apiBaseUrl = agent.ApiBaseUrl,
                executionMode = agent.ExecutionMode
            },
            options = new
            {
                requestedVersion,
                forceFullSync,
                skipWarnings,
                ignoreCurrentVersion
            },
            metadata = new
            {
                queuedBy,
                queuedAtUtc
            }
        };

        return JsonSerializer.Serialize(payload);
    }
}
