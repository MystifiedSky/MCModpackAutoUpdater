using MCAgent.Models.AgentApi;

namespace MCAgent.Services;

public interface IAgentApiClient
{
    Task<AgentHeartbeatResponse> SendHeartbeatAsync(AgentHeartbeatRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<AgentCommandPayload>> GetPendingCommandsAsync(int take, CancellationToken cancellationToken);

    Task<AgentCommandAckResponse> AcknowledgeCommandAsync(int commandId, CancellationToken cancellationToken);

    Task<AgentCommandAckResponse> CompleteCommandAsync(
        int commandId,
        AgentCommandCompletionRequest request,
        CancellationToken cancellationToken);

    Task<AgentAmpRuntimeConfigResponse> GetAmpRuntimeConfigAsync(int modpackId, CancellationToken cancellationToken);
}
