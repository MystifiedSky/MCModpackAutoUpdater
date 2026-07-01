namespace MCModpackAutoUpdater.Services;

public sealed record ModpackVersionResolutionResult(
    bool Success,
    string? TargetVersion,
    string? TargetVersionDisplay,
    string SourceKind,
    string Message,
    int? ProjectId = null,
    int? ParentFileId = null,
    int? ServerPackFileId = null,
    string? SelectedVersion = null)
{
    public bool IsUpToDate(string? currentVersion)
    {
        return Success &&
               !string.IsNullOrWhiteSpace(TargetVersion) &&
               string.Equals(TargetVersion, currentVersion?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static ModpackVersionResolutionResult Failure(string message)
    {
        return new ModpackVersionResolutionResult(
            false,
            null,
            null,
            "unresolved",
            message);
    }
}
