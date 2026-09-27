using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MCAgent.Models.AgentApi;
using MCModpackAutoUpdater.Data;
using MCModpackAutoUpdater.Services;

namespace MCModpackAutoUpdater.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/agent")]
public sealed class AgentApiController : ControllerBase
{
    private readonly UpdaterIdentityDbContext _dbContext;
    private readonly UpdaterAgentAuthenticationService _agentAuthenticationService;
    private readonly UpdaterCommandService _commandService;

    public AgentApiController(
        UpdaterIdentityDbContext dbContext,
        UpdaterAgentAuthenticationService agentAuthenticationService,
        UpdaterCommandService commandService)
    {
        _dbContext = dbContext;
        _agentAuthenticationService = agentAuthenticationService;
        _commandService = commandService;
    }

    [HttpPost("heartbeat")]
    public async Task<IActionResult> Heartbeat(
        [FromBody] AgentHeartbeatRequest request,
        CancellationToken cancellationToken)
    {
        var agent = await _agentAuthenticationService.AuthenticateAsync(Request, cancellationToken);
        if (agent is null)
        {
            return Unauthorized(new { error = "Invalid agent token." });
        }

        var utcNow = DateTime.UtcNow;
        agent.LastSeenUtc = utcNow;
        agent.LastReportedVersion = TruncateOrNull(request.AgentVersion, 100);
        agent.LastReportedStatus = TruncateOrNull(request.Status, 500);
        agent.UpdatedUtc = utcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new AgentHeartbeatResponse
        {
            AgentName = agent.Name,
            ServerTimeUtc = utcNow,
            NextPollSeconds = 30
        });
    }

    [HttpGet("commands/pending")]
    public async Task<IActionResult> GetPendingCommands(
        [FromQuery] int take = 20,
        CancellationToken cancellationToken = default)
    {
        var agent = await _agentAuthenticationService.AuthenticateAsync(Request, cancellationToken);
        if (agent is null)
        {
            return Unauthorized(new { error = "Invalid agent token." });
        }

        if (!TryGetExecutionOwnerId(Request, out var executionOwnerId))
        {
            return BadRequest(new { error = "X-Agent-Instance must contain a valid GUID." });
        }

        var clampedTake = Math.Clamp(take, 1, 100);
        var commands = await _dbContext.UpdaterAgentCommands
            .AsNoTracking()
            .Where(command =>
                command.AgentNodeId == agent.Id &&
                (command.Status == UpdaterAgentCommandStatus.Pending ||
                 command.Status == UpdaterAgentCommandStatus.InProgress &&
                 (command.ExecutionOwnerId == null ||
                  executionOwnerId != null && command.ExecutionOwnerId == executionOwnerId)))
            .OrderBy(command => command.CreatedUtc)
            .Take(clampedTake)
            .Select(command => new AgentCommandPayload
            {
                Id = command.Id,
                CommandType = command.CommandType,
                PayloadJson = command.PayloadJson,
                Status = command.Status,
                CreatedUtc = command.CreatedUtc,
                ExecutionOwnerId = command.ExecutionOwnerId
            })
            .ToListAsync(cancellationToken);

        return Ok(commands);
    }

    [HttpGet("modpacks/{modpackId:int}/amp-runtime")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> GetAmpRuntimeConfig(
        int modpackId,
        CancellationToken cancellationToken = default)
    {
        var agent = await _agentAuthenticationService.AuthenticateAsync(Request, cancellationToken);
        if (agent is null)
        {
            return Unauthorized(new { error = "Invalid agent token." });
        }

        var modpack = await _dbContext.UpdaterModpackProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(profile => profile.Id == modpackId, cancellationToken);
        if (modpack is null || modpack.AgentNodeId != agent.Id)
        {
            return NotFound(new { error = "Modpack profile was not found for this agent." });
        }

        if (!string.Equals(modpack.RestartMode, "amp", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(modpack.RestartMode, "amp_api", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { error = "Modpack restart mode is not AMP." });
        }

        var ampController = await _dbContext.UpdaterAmpControllerSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);
        var directAmpApi = await _dbContext.UpdaterDirectAmpApiSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);
        var hasDirectAmpApi = directAmpApi is not null &&
                              directAmpApi.Enabled &&
                              !string.IsNullOrWhiteSpace(modpack.AmpApiUrl) &&
                              !string.IsNullOrWhiteSpace(directAmpApi.Username) &&
                              !string.IsNullOrWhiteSpace(directAmpApi.Password);
        var hasController = ampController is not null &&
                            ampController.Enabled &&
                            !string.IsNullOrWhiteSpace(modpack.AmpInstanceName);

        if (!hasController && !hasDirectAmpApi)
        {
            return NotFound(new { error = "No AMP runtime settings are configured for this modpack." });
        }

        if (hasController &&
            (string.IsNullOrWhiteSpace(ampController!.ControllerApiUrl) ||
             string.IsNullOrWhiteSpace(ampController.Username) ||
             string.IsNullOrWhiteSpace(ampController.Password)))
        {
            return Conflict(new { error = "AMP controller settings are incomplete." });
        }

        return Ok(new AgentAmpRuntimeConfigResponse
        {
            ControllerApiUrl = hasController ? ampController!.ControllerApiUrl : string.Empty,
            InstanceName = hasController ? modpack.AmpInstanceName ?? string.Empty : string.Empty,
            Username = hasController ? ampController!.Username : string.Empty,
            Password = hasController ? ampController!.Password : string.Empty,
            Token = hasController ? ampController!.Token : string.Empty,
            RememberMe = !hasController || ampController!.RememberMe,
            DirectAmpApiEnabled = hasDirectAmpApi,
            DirectAmpApiUrl = hasDirectAmpApi ? modpack.AmpApiUrl ?? string.Empty : string.Empty,
            DirectAmpApiUsername = hasDirectAmpApi ? directAmpApi!.Username : string.Empty,
            DirectAmpApiPassword = hasDirectAmpApi ? directAmpApi!.Password : string.Empty,
            DirectAmpApiToken = hasDirectAmpApi ? directAmpApi!.Token : string.Empty,
            DirectAmpApiRememberMe = !hasDirectAmpApi || directAmpApi!.RememberMe,
            DirectAmpApiWarningMessageTemplate = hasDirectAmpApi ? directAmpApi!.WarningMessageTemplate : string.Empty
        });
    }

    [HttpPost("commands/{commandId:int}/ack")]
    public async Task<IActionResult> AcknowledgeCommand(
        int commandId,
        CancellationToken cancellationToken = default)
    {
        var agent = await _agentAuthenticationService.AuthenticateAsync(Request, cancellationToken);
        if (agent is null)
        {
            return Unauthorized(new { error = "Invalid agent token." });
        }

        if (!TryGetExecutionOwnerId(Request, out var executionOwnerId))
        {
            return BadRequest(new { error = "X-Agent-Instance must contain a valid GUID." });
        }

        try
        {
            var response = await _commandService.AcknowledgeCommandAsync(
                commandId, agent.Id, cancellationToken, executionOwnerId);
            return response is null
                ? NotFound(new { error = "Command not found." })
                : Ok(response);
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(new { error = exception.Message });
        }
    }

    [HttpPost("commands/{commandId:int}/complete")]
    public async Task<IActionResult> CompleteCommand(
        int commandId,
        [FromBody] AgentCommandCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        var agent = await _agentAuthenticationService.AuthenticateAsync(Request, cancellationToken);
        if (agent is null)
        {
            return Unauthorized(new { error = "Invalid agent token." });
        }

        if (!TryGetExecutionOwnerId(Request, out var executionOwnerId))
        {
            return BadRequest(new { error = "X-Agent-Instance must contain a valid GUID." });
        }

        try
        {
            var response = await _commandService.CompleteCommandAsync(
                commandId, agent.Id, request, cancellationToken, executionOwnerId);
            return response is null
                ? NotFound(new { error = "Command not found." })
                : Ok(response);
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(new { error = exception.Message });
        }
    }

    private static string? TruncateOrNull(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static bool TryGetExecutionOwnerId(HttpRequest request, out string? executionOwnerId)
    {
        var headerValue = request.Headers["X-Agent-Instance"].ToString().Trim();
        if (string.IsNullOrEmpty(headerValue))
        {
            executionOwnerId = null;
            return true;
        }

        if (!Guid.TryParse(headerValue, out var parsed))
        {
            executionOwnerId = null;
            return false;
        }

        executionOwnerId = parsed.ToString("N");
        return true;
    }
}
