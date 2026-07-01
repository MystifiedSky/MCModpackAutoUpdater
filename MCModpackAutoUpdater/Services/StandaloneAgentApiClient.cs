using System.Net;
using Microsoft.EntityFrameworkCore;
using MCAgent.Models.AgentApi;
using MCAgent.Services;
using MCModpackAutoUpdater.Data;

namespace MCModpackAutoUpdater.Services;

public sealed class StandaloneAgentApiClient : IAgentApiClient
{
    private readonly IServiceProvider _serviceProvider;

    public StandaloneAgentApiClient(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public Task<AgentHeartbeatResponse> SendHeartbeatAsync(
        AgentHeartbeatRequest request,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException("The embedded local runner does not use remote agent heartbeats.");
    }

    public Task<IReadOnlyList<AgentCommandPayload>> GetPendingCommandsAsync(
        int take,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException("The embedded local runner does not poll remote agent commands.");
    }

    public Task<AgentCommandAckResponse> AcknowledgeCommandAsync(
        int commandId,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException("The embedded local runner does not acknowledge remote agent commands.");
    }

    public Task<AgentCommandAckResponse> CompleteCommandAsync(
        int commandId,
        AgentCommandCompletionRequest request,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException("The embedded local runner does not complete remote agent commands.");
    }

    public Task<AgentAmpRuntimeConfigResponse> GetAmpRuntimeConfigAsync(
        int modpackId,
        CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<UpdaterIdentityDbContext>();
        var ampController = dbContext.UpdaterAmpControllerSettings
            .AsNoTracking()
            .FirstOrDefault();
        var profile = dbContext.UpdaterModpackProfiles
            .AsNoTracking()
            .FirstOrDefault(current => current.Id == modpackId);
        var directAmpApi = dbContext.UpdaterDirectAmpApiSettings
            .AsNoTracking()
            .FirstOrDefault();
        var instanceName = profile?.AmpInstanceName?.Trim();
        var hasController = ampController is not null &&
                            ampController.Enabled &&
                            !string.IsNullOrWhiteSpace(instanceName);
        var hasDirectAmpApi = directAmpApi is not null &&
                              directAmpApi.Enabled &&
                              !string.IsNullOrWhiteSpace(profile?.AmpApiUrl) &&
                              !string.IsNullOrWhiteSpace(directAmpApi.Username) &&
                              !string.IsNullOrWhiteSpace(directAmpApi.Password);

        if (!hasController && !hasDirectAmpApi)
        {
            throw new AgentApiException(HttpStatusCode.NotFound, $"No AMP runtime settings are configured for standalone modpack id {modpackId}.");
        }

        if (hasController &&
            (string.IsNullOrWhiteSpace(ampController!.ControllerApiUrl) ||
             string.IsNullOrWhiteSpace(ampController.Username) ||
             string.IsNullOrWhiteSpace(ampController.Password)))
        {
            throw new AgentApiException(HttpStatusCode.Conflict, "Standalone AMP controller settings are incomplete.");
        }

        return Task.FromResult(new AgentAmpRuntimeConfigResponse
        {
            ControllerApiUrl = hasController ? ampController!.ControllerApiUrl : string.Empty,
            InstanceName = hasController ? instanceName! : string.Empty,
            Username = hasController ? ampController!.Username : string.Empty,
            Password = hasController ? ampController!.Password : string.Empty,
            Token = hasController ? ampController!.Token : string.Empty,
            RememberMe = !hasController || ampController!.RememberMe,
            DirectAmpApiEnabled = hasDirectAmpApi,
            DirectAmpApiUrl = hasDirectAmpApi ? profile!.AmpApiUrl ?? string.Empty : string.Empty,
            DirectAmpApiUsername = hasDirectAmpApi ? directAmpApi!.Username : string.Empty,
            DirectAmpApiPassword = hasDirectAmpApi ? directAmpApi!.Password : string.Empty,
            DirectAmpApiToken = hasDirectAmpApi ? directAmpApi!.Token : string.Empty,
            DirectAmpApiRememberMe = !hasDirectAmpApi || directAmpApi!.RememberMe,
            DirectAmpApiWarningMessageTemplate = hasDirectAmpApi ? directAmpApi!.WarningMessageTemplate : string.Empty
        });
    }

}
