namespace MCAgent.Models.AgentApi;

public sealed class AgentCommandPayload
{
    public int Id { get; init; }

    public required string CommandType { get; init; }

    public required string PayloadJson { get; init; }

    public required DateTime CreatedUtc { get; init; }
}
