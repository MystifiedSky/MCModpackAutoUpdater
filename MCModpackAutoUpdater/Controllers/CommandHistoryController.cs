using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MCModpackAutoUpdater.Data;
using MCModpackAutoUpdater.Models.Web;
using MCModpackAutoUpdater.Security;
using MCModpackAutoUpdater.Services;

namespace MCModpackAutoUpdater.Controllers;

[Authorize(Roles = $"{UpdaterRoles.Admin},{UpdaterRoles.Operator}")]
public sealed class CommandHistoryController : Controller
{
    private readonly UpdaterIdentityDbContext _dbContext;
    private readonly UpdaterCommandService _commandService;

    public CommandHistoryController(
        UpdaterIdentityDbContext dbContext,
        UpdaterCommandService commandService)
    {
        _dbContext = dbContext;
        _commandService = commandService;
    }

    [HttpGet("/history")]
    public async Task<IActionResult> Index(
        [FromQuery] int? agentId,
        [FromQuery] int? modpackId,
        [FromQuery] string? status,
        [FromQuery] string? commandType,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 10, 200);

        var agents = await _dbContext.UpdaterAgentNodes
            .AsNoTracking()
            .OrderBy(agent => agent.Name)
            .Select(agent => new CommandHistoryOptionViewModel { Id = agent.Id, Name = agent.Name })
            .ToListAsync(cancellationToken);
        var modpacks = await _dbContext.UpdaterModpackProfiles
            .AsNoTracking()
            .OrderBy(profile => profile.Name)
            .Select(profile => new CommandHistoryOptionViewModel { Id = profile.Id, Name = profile.Name })
            .ToListAsync(cancellationToken);
        var statusValues = await _dbContext.UpdaterAgentCommands
            .AsNoTracking()
            .Select(command => command.Status)
            .Distinct()
            .OrderBy(value => value)
            .ToListAsync(cancellationToken);
        var commandTypeValues = await _dbContext.UpdaterAgentCommands
            .AsNoTracking()
            .Select(command => command.CommandType)
            .Distinct()
            .OrderBy(value => value)
            .ToListAsync(cancellationToken);

        var statusMap = statusValues
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(value => value, value => value, StringComparer.OrdinalIgnoreCase);
        foreach (var knownStatus in new[]
                 {
                     UpdaterAgentCommandStatus.Pending,
                     UpdaterAgentCommandStatus.InProgress,
                     UpdaterAgentCommandStatus.Completed,
                     UpdaterAgentCommandStatus.Failed,
                     UpdaterAgentCommandStatus.Cancelled
                 })
        {
            statusMap.TryAdd(knownStatus, knownStatus);
        }

        var commandTypeMap = commandTypeValues
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(value => value, value => value, StringComparer.OrdinalIgnoreCase);
        var hasAgentFilter = agentId.HasValue && agents.Any(agent => agent.Id == agentId.Value);
        var hasModpackFilter = modpackId is > 0;
        var normalizedStatus = string.IsNullOrWhiteSpace(status) || !statusMap.TryGetValue(status.Trim(), out var matchedStatus)
            ? null
            : matchedStatus;
        var normalizedCommandType = string.IsNullOrWhiteSpace(commandType) || !commandTypeMap.TryGetValue(commandType.Trim(), out var matchedCommandType)
            ? null
            : matchedCommandType;

        var query = _dbContext.UpdaterAgentCommands
            .AsNoTracking()
            .Include(command => command.AgentNode)
            .Include(command => command.ModpackUpdateAudit)
            .ThenInclude(audit => audit!.ModpackProfile)
            .AsQueryable();

        if (hasAgentFilter)
        {
            query = query.Where(command => command.AgentNodeId == agentId!.Value);
        }

        if (hasModpackFilter)
        {
            var selectedModpackId = modpackId!.Value;
            query = query.Where(command =>
                (command.ModpackUpdateAudit != null && command.ModpackUpdateAudit.ModpackProfileId == selectedModpackId) ||
                (UpdaterIdentityDbContext.IsValidJson(command.PayloadJson)
                    ? UpdaterIdentityDbContext.ReadJsonInteger(command.PayloadJson, "$.modpack.id")
                    : null) == selectedModpackId);
        }

        if (normalizedStatus is not null)
        {
            query = query.Where(command => command.Status == normalizedStatus);
        }

        if (normalizedCommandType is not null)
        {
            query = query.Where(command => command.CommandType == normalizedCommandType);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Min(page, totalPages);
        var commands = await query
            .OrderByDescending(command => command.CreatedUtc)
            .ThenByDescending(command => command.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(command => new CommandHistoryItemViewModel
            {
                Id = command.Id,
                AgentName = command.AgentNode == null ? "Unknown" : command.AgentNode.Name,
                AgentHost = command.AgentNode == null ? null : command.AgentNode.Host,
                CommandType = command.CommandType,
                Status = command.Status,
                CreatedUtc = command.CreatedUtc,
                UpdatedUtc = command.UpdatedUtc,
                AcknowledgedUtc = command.AcknowledgedUtc,
                CompletedUtc = command.CompletedUtc,
                ResultSummary = command.ResultSummary,
                ModpackName = command.ModpackUpdateAudit == null || command.ModpackUpdateAudit.ModpackProfile == null
                    ? null
                    : command.ModpackUpdateAudit.ModpackProfile.Name,
                TriggerSource = command.ModpackUpdateAudit == null ? null : command.ModpackUpdateAudit.TriggerSource,
                PreviousVersion = command.ModpackUpdateAudit == null ? null : command.ModpackUpdateAudit.PreviousVersion,
                TargetVersion = command.ModpackUpdateAudit == null
                    ? null
                    : command.ModpackUpdateAudit.TargetVersionDisplay ?? command.ModpackUpdateAudit.TargetVersion,
                AppliedVersion = command.ModpackUpdateAudit == null
                    ? null
                    : command.ModpackUpdateAudit.AppliedVersionDisplay ?? command.ModpackUpdateAudit.AppliedVersion,
                CanCancel = User.IsInRole(UpdaterRoles.Admin) && command.Status == UpdaterAgentCommandStatus.Pending,
                CanRetry = User.IsInRole(UpdaterRoles.Admin) && command.AgentNode != null &&
                           command.AgentNode.Enabled &&
                           UpdaterAgentCommandStatus.FinalStatuses.Contains(command.Status),
                PayloadJson = command.PayloadJson,
                ResultPayloadJson = command.ResultPayloadJson
            })
            .ToListAsync(cancellationToken);

        return View(new CommandHistoryIndexViewModel
        {
            Commands = commands,
            Filters = new CommandHistoryFilterModel
            {
                AgentId = hasAgentFilter ? agentId : null,
                ModpackId = hasModpackFilter ? modpackId : null,
                Status = normalizedStatus,
                CommandType = normalizedCommandType,
                Page = page,
                PageSize = pageSize
            },
            Agents = agents,
            Modpacks = modpacks,
            Statuses = statusMap.Values
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray(),
            CommandTypes = commandTypeMap.Values
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Page = page,
            TotalPages = totalPages,
            TotalCount = totalCount,
            AmpConsole = new AmpConsoleCommandFormModel(),
            AmpConfig = new AmpConfigCommandFormModel()
        });
    }

    [HttpPost("/history/{commandId:int}/cancel")]
    [Authorize(Roles = UpdaterRoles.Admin)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(
        int commandId,
        int? agentId,
        int? modpackId,
        string? status,
        string? commandType,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        await _commandService.CancelCommandAsync(commandId, User.Identity?.Name ?? "unknown", cancellationToken);
        TempData["Message"] = $"Command {commandId} cancelled if it was still pending.";
        return RedirectToHistory(agentId, modpackId, status, commandType, page, pageSize);
    }

    [HttpPost("/history/{commandId:int}/retry")]
    [Authorize(Roles = UpdaterRoles.Admin)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Retry(
        int commandId,
        int? agentId,
        int? modpackId,
        string? status,
        string? commandType,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        var command = await _dbContext.UpdaterAgentCommands
            .Include(current => current.AgentNode)
            .Include(current => current.ModpackUpdateAudit)
            .FirstOrDefaultAsync(current => current.Id == commandId, cancellationToken);
        if (command is null)
        {
            TempData["Message"] = $"Command {commandId} was not found.";
            return RedirectToHistory(agentId, modpackId, status, commandType, page, pageSize);
        }

        if (!UpdaterAgentCommandStatus.FinalStatuses.Contains(command.Status, StringComparer.Ordinal))
        {
            TempData["Message"] = $"Command {commandId} is {command.Status}; only finalized commands can be retried.";
            return RedirectToHistory(agentId, modpackId, status, commandType, page, pageSize);
        }

        if (command.AgentNode is null || !command.AgentNode.Enabled)
        {
            TempData["Message"] = "Cannot retry because the target agent is missing or disabled.";
            return RedirectToHistory(agentId, modpackId, status, commandType, page, pageSize);
        }

        var payloadJson = command.PayloadJson.Trim();
        if (payloadJson.Length > 20000)
        {
            TempData["Message"] = "Cannot retry because the command payload exceeds 20,000 characters.";
            return RedirectToHistory(agentId, modpackId, status, commandType, page, pageSize);
        }

        try
        {
            using var _ = JsonDocument.Parse(payloadJson);
        }
        catch (JsonException)
        {
            TempData["Message"] = "Cannot retry because the command payload is invalid JSON.";
            return RedirectToHistory(agentId, modpackId, status, commandType, page, pageSize);
        }

        UpdaterModpackProfile? syncProfile = null;
        if (string.Equals(command.CommandType, "sync_modpack", StringComparison.OrdinalIgnoreCase))
        {
            var syncProfileId = UpdaterCommandService.ReadModpackId(payloadJson);
            if (!syncProfileId.HasValue)
            {
                TempData["Message"] = "Cannot retry this sync because its modpack profile could not be identified.";
                return RedirectToHistory(agentId, modpackId, status, commandType, page, pageSize);
            }

            syncProfile = await _dbContext.UpdaterModpackProfiles
                .FirstOrDefaultAsync(current => current.Id == syncProfileId.Value, cancellationToken);
            if (syncProfile is null || syncProfile.AgentNodeId != command.AgentNodeId)
            {
                TempData["Message"] = "Cannot retry this sync because the profile is missing or assigned to another agent.";
                return RedirectToHistory(agentId, modpackId, status, commandType, page, pageSize);
            }

            if (await _commandService.HasActiveSyncCommandForModpackAsync(syncProfile.Id, cancellationToken))
            {
                TempData["Message"] = $"Cannot retry this sync while '{syncProfile.Name}' already has a pending or running sync.";
                return RedirectToHistory(agentId, modpackId, status, commandType, page, pageSize);
            }
        }

        var utcNow = DateTime.UtcNow;
        var retriedCommand = new UpdaterAgentCommand
        {
            AgentNodeId = command.AgentNodeId,
            CommandType = command.CommandType,
            PayloadJson = payloadJson,
            Status = UpdaterAgentCommandStatus.Pending,
            CreatedUtc = utcNow,
            UpdatedUtc = utcNow
        };
        _dbContext.UpdaterAgentCommands.Add(retriedCommand);

        if (syncProfile is not null)
        {
            syncProfile.LastQueuedUtc = utcNow;
            syncProfile.UpdatedUtc = utcNow;
            var priorAudit = command.ModpackUpdateAudit;
            var requestedVersion = ReadPayloadString(payloadJson, "options", "requestedVersion");
            _dbContext.UpdaterModpackUpdateAudits.Add(new UpdaterModpackUpdateAudit
            {
                ModpackProfileId = syncProfile.Id,
                AgentNodeId = command.AgentNodeId,
                AgentCommand = retriedCommand,
                TriggerSource = $"retry:{commandId}",
                RequestedVersion = requestedVersion is { Length: > 100 } ? requestedVersion[..100] : requestedVersion,
                PreviousVersion = syncProfile.CurrentVersion,
                TargetVersion = priorAudit?.TargetVersion,
                TargetVersionDisplay = priorAudit?.TargetVersionDisplay,
                Status = UpdaterModpackUpdateAuditStatus.Queued,
                Summary = $"Retried from command #{commandId}.",
                CreatedUtc = utcNow,
                UpdatedUtc = utcNow
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        TempData["Message"] = $"Queued command #{retriedCommand.Id} as a retry of command #{commandId}.";
        return RedirectToHistory(agentId, modpackId, status, commandType, page, pageSize);
    }

    [HttpPost("/history/{commandId:int}/retry-sync")]
    [Authorize(Roles = UpdaterRoles.Admin)]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> RetrySync(
        int commandId,
        int? agentId,
        int? modpackId,
        string? status,
        string? commandType,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        return Retry(commandId, agentId, modpackId, status, commandType, page, pageSize, cancellationToken);
    }

    [HttpPost("/history/amp-console")]
    [Authorize(Roles = UpdaterRoles.Admin)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> QueueAmpConsole(AmpConsoleCommandFormModel model, CancellationToken cancellationToken)
    {
        if (!model.ModpackId.HasValue || string.IsNullOrWhiteSpace(model.ConsoleCommand))
        {
            TempData["Message"] = "AMP console command requires a modpack and command text.";
            return RedirectToAction(nameof(Index));
        }

        var profile = await LoadProfileForDebugCommandAsync(model.ModpackId.Value, cancellationToken);
        if (profile is null)
        {
            TempData["Message"] = "Cannot queue AMP console command because the profile or assigned agent is unavailable.";
            return RedirectToAction(nameof(Index));
        }

        var consoleCommand = model.ConsoleCommand.Trim();
        if (consoleCommand.Length > 500)
        {
            TempData["Message"] = "AMP console command must be 500 characters or fewer.";
            return RedirectToAction(nameof(Index));
        }

        await QueueDebugCommandAsync(
            profile,
            "amp_console",
            new { consoleCommand },
            cancellationToken);
        TempData["Message"] = $"Queued AMP console command for {profile.Name}.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("/history/amp-config")]
    [Authorize(Roles = UpdaterRoles.Admin)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> QueueAmpConfig(AmpConfigCommandFormModel model, CancellationToken cancellationToken)
    {
        if (!model.ModpackId.HasValue || string.IsNullOrWhiteSpace(model.SettingNode))
        {
            TempData["Message"] = "AMP config command requires a modpack and setting node.";
            return RedirectToAction(nameof(Index));
        }

        var profile = await LoadProfileForDebugCommandAsync(model.ModpackId.Value, cancellationToken);
        if (profile is null)
        {
            TempData["Message"] = "Cannot queue AMP config command because the profile or assigned agent is unavailable.";
            return RedirectToAction(nameof(Index));
        }

        var settingNode = model.SettingNode.Trim();
        var settingValue = string.IsNullOrWhiteSpace(model.SettingValue) ? null : model.SettingValue.Trim();
        if (settingNode.Length > 250 || (settingValue?.Length ?? 0) > 500)
        {
            TempData["Message"] = "AMP setting node must be 250 characters or fewer and setting value must be 500 characters or fewer.";
            return RedirectToAction(nameof(Index));
        }

        await QueueDebugCommandAsync(
            profile,
            "amp_config",
            new
            {
                settingNode,
                settingValue,
                refreshValues = model.RefreshValues
            },
            cancellationToken);
        TempData["Message"] = $"Queued AMP config command for {profile.Name}.";
        return RedirectToAction(nameof(Index));
    }

    private async Task<UpdaterModpackProfile?> LoadProfileForDebugCommandAsync(
        int modpackId,
        CancellationToken cancellationToken)
    {
        return await _dbContext.UpdaterModpackProfiles
            .Include(profile => profile.AgentNode)
            .FirstOrDefaultAsync(
                profile => profile.Id == modpackId &&
                           profile.AgentNode != null &&
                           profile.AgentNode.Enabled,
                cancellationToken);
    }

    private async Task QueueDebugCommandAsync(
        UpdaterModpackProfile profile,
        string commandType,
        object options,
        CancellationToken cancellationToken)
    {
        var utcNow = DateTime.UtcNow;
        _dbContext.UpdaterAgentCommands.Add(new UpdaterAgentCommand
        {
            AgentNodeId = profile.AgentNodeId!.Value,
            CommandType = commandType,
            PayloadJson = JsonSerializer.Serialize(new
            {
                type = commandType,
                modpack = new
                {
                    id = profile.Id,
                    name = profile.Name
                },
                options,
                metadata = new
                {
                    queuedBy = User.Identity?.Name ?? "admin",
                    queuedAtUtc = utcNow
                }
            }),
            Status = UpdaterAgentCommandStatus.Pending,
            CreatedUtc = utcNow,
            UpdatedUtc = utcNow
        });
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private RedirectToActionResult RedirectToHistory(
        int? agentId,
        int? modpackId,
        string? status,
        string? commandType,
        int page,
        int pageSize)
    {
        return RedirectToAction(nameof(Index), new { agentId, modpackId, status, commandType, page, pageSize });
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
