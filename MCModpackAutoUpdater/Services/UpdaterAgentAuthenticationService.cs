using Microsoft.EntityFrameworkCore;
using MCModpackAutoUpdater.Data;

namespace MCModpackAutoUpdater.Services;

public sealed class UpdaterAgentAuthenticationService
{
    private const string AgentTokenHeader = "X-Agent-Token";
    private readonly UpdaterIdentityDbContext _dbContext;

    public UpdaterAgentAuthenticationService(UpdaterIdentityDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<UpdaterAgentNode?> AuthenticateAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var token = request.Headers[AgentTokenHeader].ToString().Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var tokenHash = UpdaterAgentTokenUtility.HashToken(token);
        return await _dbContext.UpdaterAgentNodes
            .FirstOrDefaultAsync(
                agent => agent.Enabled &&
                         agent.ExecutionMode == UpdaterAgentExecutionMode.Remote &&
                         agent.AuthTokenHash == tokenHash,
                cancellationToken);
    }
}
