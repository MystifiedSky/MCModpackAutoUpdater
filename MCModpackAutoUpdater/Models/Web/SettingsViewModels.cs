using System.ComponentModel.DataAnnotations;

namespace MCModpackAutoUpdater.Models.Web;

public sealed class SettingsIndexViewModel
{
    public required RuntimeSettingsFormModel Runtime { get; init; }

    public required AmpControllerSettingsFormModel AmpController { get; init; }

    public required DirectAmpApiSettingsFormModel DirectAmpApi { get; init; }

    public required DiscordSettingsFormModel Discord { get; init; }

    public required IReadOnlyList<DiscordAnnouncementStatusViewModel> DiscordFailures { get; init; }

    public required IReadOnlyList<ModpackSettingsFormModel> Modpacks { get; init; }

    public required ModpackSettingsFormModel NewModpack { get; init; }

    public required IReadOnlyList<AgentOptionViewModel> AgentOptions { get; init; }
}

public sealed class RuntimeSettingsFormModel
{
    public bool RunOnStartup { get; set; }

    public bool ExitAfterStartupRun { get; set; }

    [Range(5, 3600)]
    public int LoopDelaySeconds { get; set; } = 30;

    [Required]
    [MaxLength(100)]
    public string ScheduleTimeZone { get; set; } = "America/New_York";
}

public sealed class AgentOptionViewModel
{
    public int Id { get; init; }

    public required string Name { get; init; }

    public required string Host { get; init; }

    public required string ExecutionMode { get; init; }

    public bool Enabled { get; init; }
}

public sealed class AmpControllerSettingsFormModel
{
    public bool Enabled { get; set; }

    [Url]
    [MaxLength(500)]
    public string? ControllerApiUrl { get; set; }

    [MaxLength(200)]
    public string? Username { get; set; }

    [DataType(DataType.Password)]
    [MaxLength(200)]
    public string? Password { get; set; }

    [DataType(DataType.Password)]
    [MaxLength(200)]
    public string? Token { get; set; }

    public bool RememberMe { get; set; } = true;
}

public sealed class DirectAmpApiSettingsFormModel
{
    public bool Enabled { get; set; }

    [MaxLength(200)]
    public string? Username { get; set; }

    [DataType(DataType.Password)]
    [MaxLength(200)]
    public string? Password { get; set; }

    [DataType(DataType.Password)]
    [MaxLength(200)]
    public string? Token { get; set; }

    public bool RememberMe { get; set; } = true;

    [MaxLength(500)]
    public string WarningMessageTemplate { get; set; } =
        "say Server will restart in {warningMinutes} minute(s) for an automatic modpack update to {targetVersionDisplay}. Please update your clients.";
}

public sealed class DiscordSettingsFormModel
{
    public bool Enabled { get; set; }

    [DataType(DataType.Password)]
    [MaxLength(1000)]
    public string? BotToken { get; set; }

    [MaxLength(1000)]
    public string MessageTemplate { get; set; } = "{roleMention}{modpackName} updated to {version}.";
}

public sealed class DiscordAnnouncementStatusViewModel
{
    public int Id { get; init; }

    public required string ModpackName { get; init; }

    public required string Status { get; init; }

    public int RetryCount { get; init; }

    public string? FailureReason { get; init; }

    public DateTime UpdatedUtc { get; init; }
}

public sealed class ModpackSettingsFormModel
{
    public int Index { get; set; }

    public int? Id { get; set; }

    public int? AgentNodeId { get; set; }

    public bool Enabled { get; set; } = true;

    public bool? RunOnStartup { get; set; }

    [Required]
    [MaxLength(120)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [MaxLength(40)]
    public string Provider { get; set; } = "CurseForge";

    [MaxLength(300)]
    public string? SourceReference { get; set; }

    [Url]
    [MaxLength(500)]
    public string? ServerPackUrl { get; set; }

    public bool BuildServerPackFromClientFiles { get; set; }

    [MaxLength(4000)]
    public string? ServerPackExcludedPathsText { get; set; }

    [MaxLength(4000)]
    public string? ServerPackExcludedCurseForgeProjectIdsText { get; set; }

    [MaxLength(100)]
    public string? VersionLock { get; set; }

    [MaxLength(100)]
    public string? CurrentVersion { get; set; }

    [Required]
    [MaxLength(500)]
    public string InstallRootPath { get; set; } = string.Empty;

    [MaxLength(300)]
    public string? OverrideDirectory { get; set; }

    [MaxLength(2000)]
    public string? PreservedPathsText { get; set; }

    [MaxLength(5)]
    public string? ScheduleTime { get; set; } = "03:00";

    [Required]
    [MaxLength(30)]
    public string RestartMode { get; set; } = "amp";

    [Range(0, 240)]
    public int WarningMinutes { get; set; } = 10;

    [MaxLength(150)]
    public string? AmpInstanceName { get; set; }

    [Url]
    [MaxLength(500)]
    public string? AmpApiUrl { get; set; }

    [MaxLength(10000)]
    public string? AmpConfigValuesJson { get; set; }

    [MaxLength(50)]
    public string? DiscordAnnouncementChannelId { get; set; }

    [MaxLength(50)]
    public string? DiscordAnnouncementRoleId { get; set; }

    [MaxLength(100)]
    public string? RequestedVersion { get; set; }

    public bool ForceFullSync { get; set; } = true;

    public bool SkipWarnings { get; set; }

    public bool IgnoreCurrentVersion { get; set; }
}
