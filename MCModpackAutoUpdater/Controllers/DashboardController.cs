using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MCModpackAutoUpdater.Data;
using MCModpackAutoUpdater.Models.Web;
using MCModpackAutoUpdater.Security;
using MCModpackAutoUpdater.Services;

namespace MCModpackAutoUpdater.Controllers;

public sealed class DashboardController : Controller
{
    private readonly UpdaterIdentityDbContext _dbContext;
    private readonly UpdaterCommandService _commandService;
    private readonly IModpackVersionResolver _versionResolver;

    public DashboardController(
        UpdaterIdentityDbContext dbContext,
        UpdaterCommandService commandService,
        IModpackVersionResolver versionResolver)
    {
        _dbContext = dbContext;
        _commandService = commandService;
        _versionResolver = versionResolver;
    }

    [HttpGet("/")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var profiles = await _dbContext.UpdaterModpackProfiles
            .AsNoTracking()
            .Include(profile => profile.AgentNode)
            .OrderBy(profile => profile.Name)
            .ToListAsync(cancellationToken);

        var viewProfiles = new List<DashboardProfileViewModel>();
        foreach (var profile in profiles)
        {
            viewProfiles.Add(new DashboardProfileViewModel
            {
                Id = profile.Id,
                AgentName = profile.AgentNode?.Name,
                IsAssignedAgentEnabled = profile.AgentNode?.Enabled == true,
                HasActiveCommand = await _commandService.HasActiveSyncCommandForModpackAsync(profile.Id, cancellationToken),
                Name = profile.Name,
                Provider = profile.Provider,
                Enabled = profile.Enabled,
                SourceReference = profile.SourceReference,
                ScheduleTime = profile.ScheduleTime,
                RestartMode = profile.RestartMode,
                CurrentVersion = profile.CurrentVersion,
                CurrentVersionDisplay = profile.CurrentVersionDisplay,
                LastRunUtc = profile.LastRunUtc,
                LastSuccessUtc = profile.LastSuccessUtc,
                LastQueuedUtc = profile.LastQueuedUtc,
                LastDryRunCheckUtc = profile.LastDryRunCheckUtc,
                LastDryRunCheckSummary = profile.LastDryRunCheckSummary,
                LastDryRunCheckTargetVersion = profile.LastDryRunCheckTargetVersion,
                LastDryRunCheckTargetVersionDisplay = profile.LastDryRunCheckTargetVersionDisplay,
                UpdatedUtc = profile.UpdatedUtc,
                LastSucceeded = profile.LastSucceeded,
                LastSkipped = profile.LastSkipped,
                LastSummary = profile.LastSummary
            });
        }

        var activeCommands = await _dbContext.UpdaterAgentCommands
            .AsNoTracking()
            .CountAsync(
                command => command.Status == UpdaterAgentCommandStatus.Pending ||
                           command.Status == UpdaterAgentCommandStatus.InProgress,
                cancellationToken);

        return View(new DashboardViewModel
        {
            Profiles = viewProfiles,
            TotalProfiles = profiles.Count,
            EnabledScheduledProfiles = profiles.Count(profile => profile.Enabled && !string.IsNullOrWhiteSpace(profile.ScheduleTime)),
            ActiveCommands = activeCommands,
            DiscordReadyProfiles = profiles.Count(profile => !string.IsNullOrWhiteSpace(profile.DiscordAnnouncementChannelId))
        });
    }

    [Authorize(Roles = $"{UpdaterRoles.Admin},{UpdaterRoles.Operator}")]
    [HttpPost("/profiles/{profileId:int}/check")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Check(int profileId, CancellationToken cancellationToken)
    {
        var target = await ResolveQueueTargetAsync(profileId, cancellationToken);
        if (!target.Success)
        {
            TempData["Message"] = target.ErrorMessage;
            return RedirectToAction(nameof(Index));
        }

        var resolution = await _versionResolver.ResolveTargetVersionAsync(target.Profile!, null, cancellationToken);
        string summary;
        if (!resolution.Success || string.IsNullOrWhiteSpace(resolution.TargetVersion))
        {
            summary = $"Update check failed: {resolution.Message}";
            TempData["Message"] = $"Update check failed for '{target.Profile!.Name}': {resolution.Message}";
        }
        else if (resolution.IsUpToDate(target.Profile!.CurrentVersion))
        {
            summary = $"No update available. Current version is {target.Profile.CurrentVersion}.";
            TempData["Message"] = $"No update available for '{target.Profile!.Name}'. Current version is {target.Profile.CurrentVersion}.";
        }
        else
        {
            summary = $"Update available: {target.Profile.CurrentVersion} -> {resolution.TargetVersionDisplay ?? resolution.TargetVersion}.";
            TempData["Message"] = $"Update available for '{target.Profile!.Name}': {target.Profile.CurrentVersion} -> {resolution.TargetVersionDisplay ?? resolution.TargetVersion}.";
        }

        target.Profile!.LastDryRunCheckUtc = DateTime.UtcNow;
        target.Profile.LastDryRunCheckSummary = summary.Length <= 500 ? summary : summary[..500];
        target.Profile.LastDryRunCheckTargetVersion = resolution.TargetVersion;
        target.Profile.LastDryRunCheckTargetVersionDisplay = resolution.TargetVersionDisplay;
        target.Profile.UpdatedUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        return RedirectToAction(nameof(Index));
    }

    [Authorize(Roles = $"{UpdaterRoles.Admin},{UpdaterRoles.Operator}")]
    [HttpPost("/profiles/{profileId:int}/toggle-enabled")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleEnabled(int profileId, CancellationToken cancellationToken)
    {
        var profile = await _dbContext.UpdaterModpackProfiles
            .FirstOrDefaultAsync(current => current.Id == profileId, cancellationToken);
        if (profile is null)
        {
            TempData["Message"] = "Modpack profile not found.";
            return RedirectToAction(nameof(Index));
        }

        profile.Enabled = !profile.Enabled;
        profile.UpdatedUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        TempData["Message"] = $"{profile.Name} is now {(profile.Enabled ? "enabled" : "disabled")}.";
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Roles = $"{UpdaterRoles.Admin},{UpdaterRoles.Operator}")]
    [HttpPost("/profiles/{profileId:int}/check-and-queue")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CheckAndQueue(int profileId, bool skipWarnings = false, CancellationToken cancellationToken = default)
    {
        var target = await ResolveQueueTargetAsync(profileId, cancellationToken);
        if (!target.Success)
        {
            TempData["Message"] = target.ErrorMessage;
            return RedirectToAction(nameof(Index));
        }

        if (await _commandService.HasActiveSyncCommandForModpackAsync(profileId, cancellationToken))
        {
            TempData["Message"] = $"A sync command is already pending or in progress for '{target.Profile!.Name}'.";
            return RedirectToAction(nameof(Index));
        }

        var resolution = await _versionResolver.ResolveTargetVersionAsync(target.Profile!, null, cancellationToken);
        if (!resolution.Success || string.IsNullOrWhiteSpace(resolution.TargetVersion))
        {
            TempData["Message"] = $"Update check failed for '{target.Profile!.Name}': {resolution.Message}";
            return RedirectToAction(nameof(Index));
        }

        if (resolution.IsUpToDate(target.Profile!.CurrentVersion))
        {
            TempData["Message"] = $"No update queued for '{target.Profile!.Name}'. Already on version {target.Profile.CurrentVersion}.";
            return RedirectToAction(nameof(Index));
        }

        var queuedBy = $"{User.Identity?.Name ?? "unknown"}:check-queue";
        await _commandService.QueueSyncCommandAsync(
            target.Profile!,
            target.Agent!,
            resolution.TargetVersion,
            target.Profile.ForceFullSync,
            skipWarnings,
            ignoreCurrentVersion: false,
            queuedBy,
            "manual:check-and-queue",
            resolution.TargetVersion,
            resolution.TargetVersionDisplay,
            cancellationToken);

        TempData["Message"] = $"Queued update for '{target.Profile.Name}' ({target.Profile.CurrentVersion} -> {resolution.TargetVersionDisplay ?? resolution.TargetVersion}).";
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Roles = $"{UpdaterRoles.Admin},{UpdaterRoles.Operator}")]
    [HttpPost("/profiles/{profileId:int}/run")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Run(int profileId, bool skipWarnings = false, CancellationToken cancellationToken = default)
    {
        var target = await ResolveQueueTargetAsync(profileId, cancellationToken);
        if (!target.Success)
        {
            TempData["Message"] = target.ErrorMessage;
            return RedirectToAction(nameof(Index));
        }

        var profile = target.Profile!;
        var agent = target.Agent!;
        await _commandService.QueueSyncCommandAsync(
            profile,
            agent,
            profile.RequestedVersion,
            forceFullSync: true,
            skipWarnings,
            ignoreCurrentVersion: true,
            User.Identity?.Name ?? "unknown",
            "manual:force",
            profile.RequestedVersion,
            null,
            cancellationToken);

        TempData["Message"] = $"Force sync queued for '{profile.Name}'.";
        return RedirectToAction(nameof(Index));
    }

    private async Task<QueueTargetResolutionResult> ResolveQueueTargetAsync(
        int profileId,
        CancellationToken cancellationToken)
    {
        var profile = await _dbContext.UpdaterModpackProfiles
            .Include(current => current.AgentNode)
            .FirstOrDefaultAsync(current => current.Id == profileId, cancellationToken);
        if (profile is null)
        {
            return QueueTargetResolutionResult.Failed("Modpack profile not found.");
        }

        if (!profile.Enabled)
        {
            return QueueTargetResolutionResult.Failed("Cannot queue updates for a disabled modpack profile.");
        }

        if (profile.AgentNode is null || !profile.AgentNode.Enabled)
        {
            return QueueTargetResolutionResult.Failed("Assigned agent is missing or disabled.");
        }

        return QueueTargetResolutionResult.Succeeded(profile, profile.AgentNode);
    }

    private readonly record struct QueueTargetResolutionResult(
        bool Success,
        string ErrorMessage,
        UpdaterModpackProfile? Profile,
        UpdaterAgentNode? Agent)
    {
        public static QueueTargetResolutionResult Failed(string errorMessage)
        {
            return new QueueTargetResolutionResult(false, errorMessage, null, null);
        }

        public static QueueTargetResolutionResult Succeeded(UpdaterModpackProfile profile, UpdaterAgentNode agent)
        {
            return new QueueTargetResolutionResult(true, string.Empty, profile, agent);
        }
    }
}
