namespace MCAgent.Models.AgentApi;

public sealed class AgentCommandCompletionRequest
{
    public bool Success { get; set; }

    public string? Summary { get; set; }

    public string? ResultPayloadJson { get; set; }
}
