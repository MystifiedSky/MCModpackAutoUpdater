namespace MCAgent.Models.AgentApi;

public sealed class AgentHeartbeatResponse
{
    public required string AgentName { get; init; }

    public required DateTime ServerTimeUtc { get; init; }

    public int NextPollSeconds { get; init; } = 30;
}
