using MCModpackAutoUpdater.Data;

namespace MCModpackAutoUpdater.Services;

public interface IModpackVersionResolver
{
    Task<ModpackVersionResolutionResult> ResolveTargetVersionAsync(
        UpdaterModpackProfile modpack,
        string? requestedVersion,
        CancellationToken cancellationToken);
}
