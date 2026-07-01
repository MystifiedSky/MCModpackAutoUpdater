using System.ComponentModel.DataAnnotations;

namespace MCModpackAutoUpdater.Models.Web;

public sealed class AgentsIndexViewModel
{
    public required IReadOnlyList<AgentNodeViewModel> Agents { get; init; }

    public required AgentNodeFormModel NewAgent { get; init; }

    public string? GeneratedToken { get; init; }
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
