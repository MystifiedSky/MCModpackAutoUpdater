namespace MCModpackAutoUpdater.Data;

public sealed class UpdaterModpackUpdateAudit
{
    public int Id { get; set; }

    public int ModpackProfileId { get; set; }

    public UpdaterModpackProfile? ModpackProfile { get; set; }

    public int? AgentNodeId { get; set; }

    public UpdaterAgentNode? AgentNode { get; set; }

    public int? AgentCommandId { get; set; }

    public UpdaterAgentCommand? AgentCommand { get; set; }

    public string TriggerSource { get; set; } = string.Empty;

    public string? RequestedVersion { get; set; }

    public string? PreviousVersion { get; set; }

    public string? TargetVersion { get; set; }

    public string? TargetVersionDisplay { get; set; }

    public string? AppliedVersion { get; set; }

    public string? AppliedVersionDisplay { get; set; }

    public string Status { get; set; } = UpdaterModpackUpdateAuditStatus.Queued;

    public string? Summary { get; set; }

    public string? ResultPayloadJson { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }
}
