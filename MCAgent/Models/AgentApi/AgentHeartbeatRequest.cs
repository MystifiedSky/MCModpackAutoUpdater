namespace MCAgent.Models.AgentApi;

public sealed class AgentHeartbeatRequest
{
    public string? AgentVersion { get; set; }

    public string? Status { get; set; }
}
