using MCAgent.Models.AgentApi;

namespace MCAgent.Commands;

public sealed class NoOpCommandHandler(ILogger<NoOpCommandHandler> logger) : IAgentCommandHandler
{
    public string CommandType => "noop";

    public Task<AgentCommandExecutionResult> ExecuteAsync(
        AgentCommandPayload command,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Executed noop command #{CommandId}.", command.Id);
        return Task.FromResult(AgentCommandExecutionResult.Completed("No-op command executed."));
    }
}
