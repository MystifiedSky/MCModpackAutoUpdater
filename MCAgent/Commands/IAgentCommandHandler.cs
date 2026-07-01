using MCAgent.Models.AgentApi;

namespace MCAgent.Commands;

public interface IAgentCommandHandler
{
    string CommandType { get; }

    Task<AgentCommandExecutionResult> ExecuteAsync(AgentCommandPayload command, CancellationToken cancellationToken);
}
