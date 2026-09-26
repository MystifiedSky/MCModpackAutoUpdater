using System.ComponentModel.DataAnnotations;

namespace MCModpackAutoUpdater.Models.Web;

public sealed class AgentsIndexViewModel
{
    public required IReadOnlyList<AgentNodeViewModel> Agents { get; init; }

    public required AgentNodeFormModel NewAgent { get; init; }

    public required AgentCommandFormModel NewCommand { get; init; }

    public string? GeneratedToken { get; init; }
}

public sealed class AgentDetailsViewModel
{
    public required AgentNodeViewModel Agent { get; init; }

    public required IReadOnlyList<AgentCommandHistoryViewModel> Commands { get; init; }

    public required AgentCommandFormModel NewCommand { get; init; }

    public int Page { get; init; }

    public int PageSize { get; init; }

    public int TotalCount { get; init; }

    public int TotalPages { get; init; }

    public int TotalPending { get; init; }

    public int TotalInProgress { get; init; }

    public int TotalCompleted { get; init; }

    public int TotalFailed { get; init; }

    public int TotalCancelled { get; init; }

    public bool AutoRefresh { get; init; } = true;
}

public sealed class AgentCommandHistoryViewModel
{
    public int Id { get; init; }

    public required string CommandType { get; init; }

    public required string Status { get; init; }

    public DateTime CreatedUtc { get; init; }

    public DateTime UpdatedUtc { get; init; }

    public DateTime? AcknowledgedUtc { get; init; }

    public DateTime? CompletedUtc { get; init; }

    public string? ResultSummary { get; init; }

    public string? PayloadJson { get; init; }

    public string? ResultPayloadJson { get; init; }
}

public sealed class AgentNodeViewModel
{
    public int Id { get; init; }

    public required string Name { get; init; }

    public required string Host { get; init; }

    public required string ApiBaseUrl { get; init; }

    public required string Platform { get; init; }

    public required string ExecutionMode { get; init; }

    public bool Enabled { get; init; }

    public DateTime? LastSeenUtc { get; init; }

    public string? LastReportedStatus { get; init; }

    public string? LastReportedVersion { get; init; }

    public int ProfileCount { get; init; }

    public int PendingCommandCount { get; init; }

    public int InProgressCommandCount { get; init; }

    public DateTime? AuthTokenLastRotatedUtc { get; init; }

    public DateTime UpdatedUtc { get; init; }
}

public sealed class AgentNodeFormModel
{
    public int Id { get; set; }

    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [MaxLength(255)]
    public string Host { get; set; } = string.Empty;

    [Required]
    [MaxLength(500)]
    public string ApiBaseUrl { get; set; } = string.Empty;

    [Required]
    [MaxLength(50)]
    public string Platform { get; set; } = "Linux";

    public bool Enabled { get; set; } = true;
}

public sealed class AgentCommandFormModel
{
    public int AgentNodeId { get; set; }

    [Required]
    [MaxLength(100)]
    public string CommandType { get; set; } = string.Empty;

    [MaxLength(20000)]
    public string PayloadJson { get; set; } = "{}";
}
