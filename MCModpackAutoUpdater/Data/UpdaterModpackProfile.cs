namespace MCModpackAutoUpdater.Data;

public sealed class UpdaterModpackProfile
{
    public int Id { get; set; }

    public int? AgentNodeId { get; set; }

    public UpdaterAgentNode? AgentNode { get; set; }

    public bool Enabled { get; set; } = true;

    public bool? RunOnStartup { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Provider { get; set; } = "CurseForge";

    public string SourceReference { get; set; } = string.Empty;

    public string? ServerPackUrl { get; set; }

    public bool BuildServerPackFromClientFiles { get; set; }

    public string? ServerPackExcludedPaths { get; set; }

    public string? ServerPackExcludedCurseForgeProjectIds { get; set; }

    public string? VersionLock { get; set; }

    public string CurrentVersion { get; set; } = string.Empty;

    public string? CurrentVersionDisplay { get; set; }

    public string InstallRootPath { get; set; } = string.Empty;

    public string? OverrideDirectory { get; set; }

    public string? PreservedPaths { get; set; }

    public string? ScheduleTime { get; set; } = "03:00";

    public string RestartMode { get; set; } = "amp";

    public int WarningMinutes { get; set; } = 10;

    public string? AmpInstanceName { get; set; }

    public string? AmpApiUrl { get; set; }

    public string? AmpConfigValuesJson { get; set; }

    public string? DiscordAnnouncementChannelId { get; set; }

    public string? DiscordAnnouncementRoleId { get; set; }

    public string? RequestedVersion { get; set; }

    public bool ForceFullSync { get; set; } = true;

    public bool SkipWarnings { get; set; }

    public bool IgnoreCurrentVersion { get; set; }

    public string? LastScheduledCheckDate { get; set; }

    public DateTime? LastScheduledCheckUtc { get; set; }

    public DateTime? LastDryRunCheckUtc { get; set; }

    public string? LastDryRunCheckSummary { get; set; }

    public string? LastDryRunCheckTargetVersion { get; set; }

    public string? LastDryRunCheckTargetVersionDisplay { get; set; }

    public DateTime? LastQueuedUtc { get; set; }

    public DateTime? LastRunUtc { get; set; }

    public DateTime? LastSuccessUtc { get; set; }

    public bool LastSucceeded { get; set; }

    public bool LastSkipped { get; set; }

    public string? LastSummary { get; set; }

    public string? LastResultPayloadJson { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    public ICollection<UpdaterModpackUpdateAudit> UpdateAudits { get; set; } = new List<UpdaterModpackUpdateAudit>();
}
