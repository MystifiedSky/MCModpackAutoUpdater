namespace MCModpackAutoUpdater.Models.Web;

public sealed class CommandHistoryIndexViewModel
{
    public required IReadOnlyList<CommandHistoryItemViewModel> Commands { get; init; }

    public required CommandHistoryFilterModel Filters { get; init; }

    public required IReadOnlyList<CommandHistoryOptionViewModel> Agents { get; init; }

    public required IReadOnlyList<CommandHistoryOptionViewModel> Modpacks { get; init; }

    public required IReadOnlyList<string> Statuses { get; init; }

    public required IReadOnlyList<string> CommandTypes { get; init; }

    public required AmpConsoleCommandFormModel AmpConsole { get; init; }

    public required AmpConfigCommandFormModel AmpConfig { get; init; }
}

public sealed class CommandHistoryFilterModel
{
    public int? AgentId { get; init; }

    public int? ModpackId { get; init; }

    public string? Status { get; init; }

    public string? CommandType { get; init; }

    public int PageSize { get; init; } = 100;
}

public sealed class CommandHistoryOptionViewModel
{
    public int Id { get; init; }

    public required string Name { get; init; }
}

public sealed class AmpConsoleCommandFormModel
{
    public int? ModpackId { get; set; }

    public string? ConsoleCommand { get; set; }
}

public sealed class AmpConfigCommandFormModel
{
    public int? ModpackId { get; set; }

    public string? SettingNode { get; set; }

    public string? SettingValue { get; set; }

    public bool RefreshValues { get; set; } = true;
}

public sealed class CommandHistoryItemViewModel
{
    public int Id { get; init; }

    public required string AgentName { get; init; }

    public required string CommandType { get; init; }

    public required string Status { get; init; }

    public DateTime CreatedUtc { get; init; }

    public DateTime UpdatedUtc { get; init; }

    public DateTime? AcknowledgedUtc { get; init; }

    public DateTime? CompletedUtc { get; init; }

    public string? ResultSummary { get; init; }

    public string? ModpackName { get; init; }

    public string? TriggerSource { get; init; }

    public string? PreviousVersion { get; init; }

    public string? TargetVersion { get; init; }

    public string? AppliedVersion { get; init; }

    public bool CanCancel { get; init; }

    public bool CanRetrySync { get; init; }

    public string? PayloadJson { get; init; }

    public string? ResultPayloadJson { get; init; }
}
