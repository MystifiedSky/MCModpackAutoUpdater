using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MCModpackAutoUpdater.Data;
using MCModpackAutoUpdater.Models.Web;
using MCModpackAutoUpdater.Security;
using MCModpackAutoUpdater.Services;

namespace MCModpackAutoUpdater.Controllers;

[Authorize(Roles = UpdaterRoles.Admin)]
public sealed class AgentsController : Controller
{
    private readonly UpdaterIdentityDbContext _dbContext;
    private readonly UpdaterCommandService _commandService;

    public AgentsController(UpdaterIdentityDbContext dbContext, UpdaterCommandService commandService)
    {
        _dbContext = dbContext;
        _commandService = commandService;
    }

    [HttpGet("/agents")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        return View(await BuildModelAsync(TempData["GeneratedToken"] as string, cancellationToken));
    }

    [HttpPost("/agents/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(AgentNodeFormModel model, CancellationToken cancellationToken)
    {
        ValidateAgent(model, creating: true);
        var normalizedName = model.Name?.Trim() ?? string.Empty;
        if (normalizedName.Length > 0 &&
            await _dbContext.UpdaterAgentNodes.AnyAsync(agent => agent.Name == normalizedName, cancellationToken))
        {
            ModelState.AddModelError(nameof(model.Name), "An agent with this name already exists.");
        }

        if (!ModelState.IsValid)
        {
            return View(nameof(Index), await BuildModelAsync(null, cancellationToken, model));
        }

        var token = UpdaterAgentTokenUtility.GenerateToken();
        var utcNow = DateTime.UtcNow;
        _dbContext.UpdaterAgentNodes.Add(new UpdaterAgentNode
        {
            Name = normalizedName,
            Host = model.Host.Trim(),
            ApiBaseUrl = model.ApiBaseUrl.Trim(),
            Platform = model.Platform.Trim(),
            ExecutionMode = UpdaterAgentExecutionMode.Remote,
            Enabled = model.Enabled,
            AuthTokenHash = UpdaterAgentTokenUtility.HashToken(token),
            AuthTokenLastRotatedUtc = utcNow,
            CreatedUtc = utcNow,
            UpdatedUtc = utcNow
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        TempData["Message"] = $"Remote agent '{model.Name}' created. Copy the token before leaving this page.";
        TempData["GeneratedToken"] = token;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("/agents/update")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(AgentNodeFormModel model, CancellationToken cancellationToken)
    {
        ValidateAgent(model, creating: false);
        var agent = await _dbContext.UpdaterAgentNodes.FirstOrDefaultAsync(item => item.Id == model.Id, cancellationToken);
        if (agent is null)
        {
            TempData["Message"] = "Agent was not found.";
            return RedirectToAction(nameof(Index));
        }

        var normalizedName = model.Name?.Trim() ?? string.Empty;
        if (normalizedName.Length > 0 &&
            await _dbContext.UpdaterAgentNodes.AnyAsync(item => item.Id != model.Id && item.Name == normalizedName, cancellationToken))
        {
            ModelState.AddModelError(nameof(model.Name), "An agent with this name already exists.");
        }

        if (!ModelState.IsValid)
        {
            return View(nameof(Index), await BuildModelAsync(null, cancellationToken, editedAgent: model));
        }

        agent.Name = normalizedName;
        agent.Host = model.Host.Trim();
        agent.Platform = model.Platform.Trim();
        agent.Enabled = model.Enabled;
        agent.UpdatedUtc = DateTime.UtcNow;
        if (agent.ExecutionMode == UpdaterAgentExecutionMode.Remote)
        {
            agent.ApiBaseUrl = model.ApiBaseUrl.Trim();
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        TempData["Message"] = $"Agent '{agent.Name}' updated.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("/agents/rotate-token")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RotateToken(int id, CancellationToken cancellationToken)
    {
        var agent = await _dbContext.UpdaterAgentNodes.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (agent is null)
        {
            TempData["Message"] = "Agent was not found.";
            return RedirectToAction(nameof(Index));
        }

        if (agent.ExecutionMode != UpdaterAgentExecutionMode.Remote)
        {
            TempData["Message"] = "Local runner tokens are internal and cannot be rotated from the UI.";
            return RedirectToAction(nameof(Index));
        }

        var token = UpdaterAgentTokenUtility.GenerateToken();
        agent.AuthTokenHash = UpdaterAgentTokenUtility.HashToken(token);
        agent.AuthTokenLastRotatedUtc = DateTime.UtcNow;
        agent.UpdatedUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        TempData["Message"] = $"Token rotated for '{agent.Name}'. Copy the new token before leaving this page.";
        TempData["GeneratedToken"] = token;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("/agents/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        var agent = await _dbContext.UpdaterAgentNodes.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (agent is null)
        {
            TempData["Message"] = "Agent was not found.";
            return RedirectToAction(nameof(Index));
        }

        var hasActiveCommands = await _dbContext.UpdaterAgentCommands
            .AsNoTracking()
            .AnyAsync(command =>
                command.AgentNodeId == id &&
                (command.Status == UpdaterAgentCommandStatus.Pending ||
                 command.Status == UpdaterAgentCommandStatus.InProgress), cancellationToken);
        if (hasActiveCommands)
        {
            TempData["Message"] = $"Cannot delete '{agent.Name}' while it has pending or running commands.";
            return RedirectToAction(nameof(Index));
        }

        var name = agent.Name;
        _dbContext.UpdaterAgentNodes.Remove(agent);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        TempData["Message"] = $"Agent '{name}' deleted. Its command history was removed and assigned profiles are now unassigned.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("/agents/queue-command")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> QueueCommand(AgentCommandFormModel model, CancellationToken cancellationToken)
    {
        var agent = await _dbContext.UpdaterAgentNodes
            .AsNoTracking()
            .FirstOrDefaultAsync(current => current.Id == model.AgentNodeId, cancellationToken);
        if (agent is null || !agent.Enabled)
        {
            TempData["Message"] = agent is null ? "Agent was not found." : "Cannot queue a command for a disabled agent.";
            return RedirectToAction(nameof(Index));
        }

        var commandType = model.CommandType?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(commandType) || commandType.Length > 100)
        {
            return await ReturnCommandErrorAsync(
                agent.Id, model, "Command type is required and must be 100 characters or fewer.", cancellationToken);
        }

        var payloadJson = string.IsNullOrWhiteSpace(model.PayloadJson) ? "{}" : model.PayloadJson.Trim();
        if (payloadJson.Length > 20000)
        {
            return await ReturnCommandErrorAsync(
                agent.Id, model, "Payload JSON must be 20,000 characters or fewer.", cancellationToken);
        }

        try
        {
            using var _ = JsonDocument.Parse(payloadJson);
        }
        catch (JsonException)
        {
            return await ReturnCommandErrorAsync(agent.Id, model, "Payload must be valid JSON.", cancellationToken);
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        agent = await _dbContext.UpdaterAgentNodes
            .AsNoTracking()
            .FirstOrDefaultAsync(current => current.Id == model.AgentNodeId, cancellationToken);
        if (agent is null || !agent.Enabled)
        {
            TempData["Message"] = agent is null ? "Agent was not found." : "Cannot queue a command for a disabled agent.";
            return RedirectToAction(nameof(Index));
        }

        var isSyncCommand = string.Equals(commandType, "sync_modpack", StringComparison.OrdinalIgnoreCase);
        UpdaterModpackProfile? syncProfile = null;
        var syncProfileId = isSyncCommand ? UpdaterCommandService.ReadModpackId(payloadJson) : null;
        if (isSyncCommand && !syncProfileId.HasValue)
        {
            return await ReturnCommandErrorAsync(
                agent.Id, model, "A sync command must identify its modpack profile in the payload.", cancellationToken);
        }

        if (isSyncCommand)
        {
            syncProfile = await _dbContext.UpdaterModpackProfiles
                .FirstOrDefaultAsync(profile => profile.Id == syncProfileId!.Value, cancellationToken);
            if (syncProfile is null || syncProfile.AgentNodeId != agent.Id)
            {
                return await ReturnCommandErrorAsync(
                    agent.Id, model, "The sync profile is missing or is not assigned to the selected agent.", cancellationToken);
            }

            if (await _commandService.HasActiveSyncCommandForModpackAsync(syncProfile.Id, cancellationToken))
            {
                return await ReturnCommandErrorAsync(
                    agent.Id, model, $"'{syncProfile.Name}' already has a pending or running sync command.", cancellationToken);
            }
        }

        var utcNow = DateTime.UtcNow;
        var command = new UpdaterAgentCommand
        {
            AgentNodeId = agent.Id,
            CommandType = commandType,
            PayloadJson = payloadJson,
            Status = UpdaterAgentCommandStatus.Pending,
            CreatedUtc = utcNow,
            UpdatedUtc = utcNow
        };
        _dbContext.UpdaterAgentCommands.Add(command);
        if (syncProfile is not null)
        {
            var requestedVersion = ReadPayloadString(payloadJson, "options", "requestedVersion");
            syncProfile.LastQueuedUtc = utcNow;
            syncProfile.UpdatedUtc = utcNow;
            _dbContext.UpdaterModpackUpdateAudits.Add(new UpdaterModpackUpdateAudit
            {
                ModpackProfileId = syncProfile.Id,
                AgentNodeId = agent.Id,
                AgentCommand = command,
                TriggerSource = "admin:raw-sync-command",
                RequestedVersion = requestedVersion is { Length: > 100 } ? requestedVersion[..100] : requestedVersion,
                PreviousVersion = syncProfile.CurrentVersion,
                TargetVersion = requestedVersion is { Length: > 100 } ? requestedVersion[..100] : requestedVersion,
                Status = UpdaterModpackUpdateAuditStatus.Queued,
                Summary = $"Queued by {User.Identity?.Name ?? "admin"} through agent command management.",
                CreatedUtc = utcNow,
                UpdatedUtc = utcNow
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        TempData["Message"] = $"Queued '{commandType}' command for '{agent.Name}'.";
        return RedirectToDetails(agent.Id);
    }

    [HttpGet("/agents/{id:int}")]
    public async Task<IActionResult> Details(
        int id,
        int page = 1,
        int pageSize = 25,
        bool autoRefresh = true,
        CancellationToken cancellationToken = default)
    {
        var model = await BuildDetailsModelAsync(id, page, pageSize, autoRefresh, cancellationToken);
        if (model is null)
        {
            TempData["Message"] = "Agent was not found.";
            return RedirectToAction(nameof(Index));
        }

        return View(model);
    }

    private async Task<AgentDetailsViewModel?> BuildDetailsModelAsync(
        int id,
        int page,
        int pageSize,
        bool autoRefresh,
        CancellationToken cancellationToken,
        AgentCommandFormModel? newCommand = null)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 10, 200);

        var agent = await _dbContext.UpdaterAgentNodes
            .AsNoTracking()
            .FirstOrDefaultAsync(current => current.Id == id, cancellationToken);
        if (agent is null)
        {
            return null;
        }

        var statusCounts = await _dbContext.UpdaterAgentCommands
            .AsNoTracking()
            .Where(command => command.AgentNodeId == id)
            .GroupBy(command => command.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToDictionaryAsync(entry => entry.Status, entry => entry.Count, cancellationToken);

        var query = _dbContext.UpdaterAgentCommands
            .AsNoTracking()
            .Where(command => command.AgentNodeId == id);
        var totalCount = await query.CountAsync(cancellationToken);
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Min(page, totalPages);
        var commands = await query
            .OrderByDescending(command => command.CreatedUtc)
            .ThenByDescending(command => command.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(command => new AgentCommandHistoryViewModel
            {
                Id = command.Id,
                CommandType = command.CommandType,
                Status = command.Status,
                CreatedUtc = command.CreatedUtc,
                UpdatedUtc = command.UpdatedUtc,
                AcknowledgedUtc = command.AcknowledgedUtc,
                CompletedUtc = command.CompletedUtc,
                ResultSummary = command.ResultSummary,
                PayloadJson = command.PayloadJson,
                ResultPayloadJson = command.ResultPayloadJson
            })
            .ToListAsync(cancellationToken);

        var agentModel = await _dbContext.UpdaterAgentNodes
            .AsNoTracking()
            .Where(current => current.Id == id)
            .Select(current => new AgentNodeViewModel
            {
                Id = current.Id,
                Name = current.Name,
                Host = current.Host,
                ApiBaseUrl = current.ApiBaseUrl,
                Platform = current.Platform,
                ExecutionMode = current.ExecutionMode,
                Enabled = current.Enabled,
                LastSeenUtc = current.LastSeenUtc,
                LastReportedStatus = current.LastReportedStatus,
                LastReportedVersion = current.LastReportedVersion,
                ProfileCount = current.ModpackProfiles.Count,
                AuthTokenLastRotatedUtc = current.AuthTokenLastRotatedUtc,
                UpdatedUtc = current.UpdatedUtc,
                InProgressCommandCount = current.Commands.Count(command => command.Status == UpdaterAgentCommandStatus.InProgress),
                PendingCommandCount = current.Commands.Count(command =>
                    command.Status == UpdaterAgentCommandStatus.Pending)
            })
            .SingleAsync(cancellationToken);

        statusCounts.TryGetValue(UpdaterAgentCommandStatus.Pending, out var pendingCount);
        statusCounts.TryGetValue(UpdaterAgentCommandStatus.InProgress, out var inProgressCount);
        statusCounts.TryGetValue(UpdaterAgentCommandStatus.Completed, out var completedCount);
        statusCounts.TryGetValue(UpdaterAgentCommandStatus.Failed, out var failedCount);
        statusCounts.TryGetValue(UpdaterAgentCommandStatus.Cancelled, out var cancelledCount);

        return new AgentDetailsViewModel
        {
            Agent = agentModel,
            Commands = commands,
            NewCommand = newCommand ?? new AgentCommandFormModel { AgentNodeId = id },
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalPages = totalPages,
            TotalPending = pendingCount,
            TotalInProgress = inProgressCount,
            TotalCompleted = completedCount,
            TotalFailed = failedCount,
            TotalCancelled = cancelledCount,
            AutoRefresh = autoRefresh
        };
    }

    private async Task<IActionResult> ReturnCommandErrorAsync(
        int agentId,
        AgentCommandFormModel model,
        string message,
        CancellationToken cancellationToken)
    {
        TempData["Message"] = message;
        var detailsModel = await BuildDetailsModelAsync(agentId, 1, 25, false, cancellationToken, model);
        return detailsModel is null
            ? RedirectToAction(nameof(Index))
            : View(nameof(Details), detailsModel);
    }

    private async Task<AgentsIndexViewModel> BuildModelAsync(
        string? token,
        CancellationToken cancellationToken,
        AgentNodeFormModel? newAgent = null,
        AgentNodeFormModel? editedAgent = null)
    {
        var agents = await _dbContext.UpdaterAgentNodes
            .AsNoTracking()
            .Select(agent => new AgentNodeViewModel
            {
                Id = agent.Id,
                Name = agent.Name,
                Host = agent.Host,
                ApiBaseUrl = agent.ApiBaseUrl,
                Platform = agent.Platform,
                ExecutionMode = agent.ExecutionMode,
                Enabled = agent.Enabled,
                LastSeenUtc = agent.LastSeenUtc,
                LastReportedStatus = agent.LastReportedStatus,
                LastReportedVersion = agent.LastReportedVersion,
                ProfileCount = agent.ModpackProfiles.Count,
                AuthTokenLastRotatedUtc = agent.AuthTokenLastRotatedUtc,
                UpdatedUtc = agent.UpdatedUtc,
                InProgressCommandCount = agent.Commands.Count(command => command.Status == UpdaterAgentCommandStatus.InProgress),
                PendingCommandCount = agent.Commands.Count(command =>
                    command.Status == UpdaterAgentCommandStatus.Pending ||
                    command.Status == UpdaterAgentCommandStatus.InProgress)
            })
            .OrderBy(agent => agent.Name)
            .ToListAsync(cancellationToken);

        return new AgentsIndexViewModel
        {
            Agents = agents,
            NewAgent = newAgent ?? new AgentNodeFormModel(),
            EditedAgent = editedAgent,
            NewCommand = new AgentCommandFormModel
            {
                AgentNodeId = agents.FirstOrDefault(agent => agent.Enabled)?.Id ?? agents.FirstOrDefault()?.Id ?? 0
            },
            GeneratedToken = token
        };
    }

    private void ValidateAgent(AgentNodeFormModel model, bool creating)
    {
        if (creating || !string.IsNullOrWhiteSpace(model.ApiBaseUrl))
        {
            if (!Uri.TryCreate(model.ApiBaseUrl?.Trim(), UriKind.Absolute, out var apiUrl) ||
                (apiUrl.Scheme != Uri.UriSchemeHttp && apiUrl.Scheme != Uri.UriSchemeHttps))
            {
                ModelState.AddModelError(nameof(model.ApiBaseUrl), "Runner API URL must be an absolute HTTP/HTTPS URL.");
            }
        }

        if (!string.Equals(model.Platform, "Linux", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(model.Platform, "Windows", StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(nameof(model.Platform), "Platform must be Linux or Windows.");
        }
    }

    private RedirectToActionResult RedirectToDetails(int id)
    {
        return RedirectToAction(nameof(Details), new { id });
    }

    private static string? ReadPayloadString(string payloadJson, string section, string property)
    {
        try
        {
            using var json = JsonDocument.Parse(payloadJson);
            if (json.RootElement.ValueKind == JsonValueKind.Object &&
                json.RootElement.TryGetProperty(section, out var objectElement) &&
                objectElement.ValueKind == JsonValueKind.Object &&
                objectElement.TryGetProperty(property, out var value))
            {
                return value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString(),
                    JsonValueKind.Number => value.GetRawText(),
                    _ => null
                };
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }
}
