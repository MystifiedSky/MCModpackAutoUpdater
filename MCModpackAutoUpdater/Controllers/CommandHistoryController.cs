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
        [FromQuery] int pageSize = 100,
        CancellationToken cancellationToken = default)
    {
        var clampedPageSize = Math.Clamp(pageSize, 10, 500);
        var query = _dbContext.UpdaterAgentCommands
            .AsNoTracking()
            .Include(command => command.AgentNode)
            .Include(command => command.ModpackUpdateAudit)
            .ThenInclude(audit => audit!.ModpackProfile)
            .AsQueryable();

        if (agentId.HasValue)
        {
            query = query.Where(command => command.AgentNodeId == agentId.Value);
        }

        if (modpackId.HasValue)
        {
            query = query.Where(command => command.ModpackUpdateAudit != null &&
                                           command.ModpackUpdateAudit.ModpackProfileId == modpackId.Value);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(command => command.Status == status.Trim());
        }

        if (!string.IsNullOrWhiteSpace(commandType))
        {
            query = query.Where(command => command.CommandType == commandType.Trim());
        }

        var commands = await query
            .OrderByDescending(command => command.CreatedUtc)
            .Take(clampedPageSize)
            .Select(command => new CommandHistoryItemViewModel
            {
                Id = command.Id,
                AgentName = command.AgentNode == null ? "Unknown" : command.AgentNode.Name,
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
                CanCancel = command.Status == UpdaterAgentCommandStatus.Pending ||
                            command.Status == UpdaterAgentCommandStatus.InProgress,
                CanRetrySync = command.CommandType == "sync_modpack" &&
                               (command.Status == UpdaterAgentCommandStatus.Failed ||
                                command.Status == UpdaterAgentCommandStatus.Cancelled) &&
                               command.ModpackUpdateAudit != null,
                PayloadJson = command.PayloadJson,
                ResultPayloadJson = command.ResultPayloadJson
            })
            .ToListAsync(cancellationToken);

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
        var statuses = await _dbContext.UpdaterAgentCommands
            .AsNoTracking()
            .Select(command => command.Status)
            .Distinct()
            .OrderBy(value => value)
            .ToListAsync(cancellationToken);
        var commandTypes = await _dbContext.UpdaterAgentCommands
            .AsNoTracking()
            .Select(command => command.CommandType)
            .Distinct()
            .OrderBy(value => value)
            .ToListAsync(cancellationToken);

        return View(new CommandHistoryIndexViewModel
        {
            Commands = commands,
            Filters = new CommandHistoryFilterModel
            {
                AgentId = agentId,
                ModpackId = modpackId,
                Status = status,
                CommandType = commandType,
                PageSize = clampedPageSize
            },
            Agents = agents,
            Modpacks = modpacks,
            Statuses = statuses,
            CommandTypes = commandTypes,
            AmpConsole = new AmpConsoleCommandFormModel(),
            AmpConfig = new AmpConfigCommandFormModel()
        });
    }

    [HttpPost("/history/{commandId:int}/cancel")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int commandId, CancellationToken cancellationToken)
    {
        await _commandService.CancelCommandAsync(commandId, User.Identity?.Name ?? "unknown", cancellationToken);
        TempData["Message"] = $"Command {commandId} cancelled if it was still active.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("/history/{commandId:int}/retry-sync")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RetrySync(int commandId, CancellationToken cancellationToken)
    {
        var command = await _dbContext.UpdaterAgentCommands
            .Include(current => current.ModpackUpdateAudit)
            .FirstOrDefaultAsync(current => current.Id == commandId, cancellationToken);
        if (command?.ModpackUpdateAudit is null || command.CommandType != "sync_modpack")
        {
            TempData["Message"] = $"Command {commandId} is not a retryable sync command.";
            return RedirectToAction(nameof(Index));
        }

        if (command.Status is not UpdaterAgentCommandStatus.Failed and not UpdaterAgentCommandStatus.Cancelled)
        {
            TempData["Message"] = $"Command {commandId} is not failed or cancelled.";
            return RedirectToAction(nameof(Index));
        }

        var profile = await _dbContext.UpdaterModpackProfiles
            .Include(current => current.AgentNode)
            .FirstOrDefaultAsync(current => current.Id == command.ModpackUpdateAudit.ModpackProfileId, cancellationToken);
        if (profile?.AgentNode is null || !profile.AgentNode.Enabled)
        {
            TempData["Message"] = "Cannot retry because the profile agent is missing or disabled.";
            return RedirectToAction(nameof(Index));
        }

        await _commandService.QueueSyncCommandAsync(
            profile,
            profile.AgentNode,
            profile.RequestedVersion,
            profile.ForceFullSync,
            profile.SkipWarnings,
            ignoreCurrentVersion: true,
            User.Identity?.Name ?? "admin",
            $"retry:{commandId}",
            command.ModpackUpdateAudit.TargetVersion,
            command.ModpackUpdateAudit.TargetVersionDisplay,
            cancellationToken);

        TempData["Message"] = $"Queued retry for sync command {commandId}.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("/history/amp-console")]
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

        await QueueDebugCommandAsync(
            profile,
            "amp_console",
            new { consoleCommand = model.ConsoleCommand.Trim() },
            cancellationToken);
        TempData["Message"] = $"Queued AMP console command for {profile.Name}.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("/history/amp-config")]
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

        await QueueDebugCommandAsync(
            profile,
            "amp_config",
            new
            {
                settingNode = model.SettingNode.Trim(),
                settingValue = string.IsNullOrWhiteSpace(model.SettingValue) ? null : model.SettingValue.Trim(),
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
}
