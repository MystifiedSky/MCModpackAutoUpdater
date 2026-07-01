namespace MCAgent.Commands;

public sealed class AgentCommandExecutionResult
{
    private AgentCommandExecutionResult(bool success, string summary, string? resultPayloadJson)
    {
        Success = success;
        Summary = summary;
        ResultPayloadJson = resultPayloadJson;
    }

    public bool Success { get; }

    public string Summary { get; }

    public string? ResultPayloadJson { get; }

    public static AgentCommandExecutionResult Completed(string summary, string? resultPayloadJson = null)
    {
        return new AgentCommandExecutionResult(true, summary, resultPayloadJson);
    }

    public static AgentCommandExecutionResult Failed(string summary, string? resultPayloadJson = null)
    {
        return new AgentCommandExecutionResult(false, summary, resultPayloadJson);
    }
}
