namespace MCModpackAutoUpdater.Models.Web;

public sealed class DashboardViewModel
{
    public required IReadOnlyList<DashboardProfileViewModel> Profiles { get; init; }

    public int TotalProfiles { get; init; }

    public int EnabledScheduledProfiles { get; init; }

    public int ActiveCommands { get; init; }

    public int DiscordReadyProfiles { get; init; }
}

public sealed class DashboardProfileViewModel
{
    public int Id { get; init; }

    public string? AgentName { get; init; }

    public bool IsAssignedAgentEnabled { get; init; }

    public bool HasActiveCommand { get; init; }

    public required string Name { get; init; }

    public required string Provider { get; init; }

    public bool Enabled { get; init; }

    public string? SourceReference { get; init; }

    public string? ScheduleTime { get; init; }

    public string? RestartMode { get; init; }

    public string? CurrentVersion { get; init; }

    public string? CurrentVersionDisplay { get; init; }

    public DateTime? LastRunUtc { get; init; }

    public DateTime? LastSuccessUtc { get; init; }

    public DateTime? LastQueuedUtc { get; init; }

    public DateTime? LastDryRunCheckUtc { get; init; }

    public string? LastDryRunCheckSummary { get; init; }

    public string? LastDryRunCheckTargetVersion { get; init; }

    public string? LastDryRunCheckTargetVersionDisplay { get; init; }

    public DateTime UpdatedUtc { get; init; }

    public bool LastSucceeded { get; init; }

    public bool LastSkipped { get; init; }

    public string? LastSummary { get; init; }
}
