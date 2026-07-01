namespace MCAgent.Models.AgentApi;

public sealed class AgentCommandAckResponse
{
    public int CommandId { get; init; }

    public required string Status { get; init; }

    public required DateTime ServerTimeUtc { get; init; }
}
