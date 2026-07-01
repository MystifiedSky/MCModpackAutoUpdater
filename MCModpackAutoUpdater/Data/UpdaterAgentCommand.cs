namespace MCModpackAutoUpdater.Data;

public sealed class UpdaterAgentCommand
{
    public int Id { get; set; }

    public int AgentNodeId { get; set; }

    public UpdaterAgentNode? AgentNode { get; set; }

    public string CommandType { get; set; } = string.Empty;

    public string PayloadJson { get; set; } = "{}";

    public string Status { get; set; } = UpdaterAgentCommandStatus.Pending;

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    public DateTime? AcknowledgedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }

    public string? ResultSummary { get; set; }

    public string? ResultPayloadJson { get; set; }

    public UpdaterModpackUpdateAudit? ModpackUpdateAudit { get; set; }
}
