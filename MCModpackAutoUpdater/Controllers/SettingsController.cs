using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MCModpackAutoUpdater.Data;
using MCModpackAutoUpdater.Models.Web;
using MCModpackAutoUpdater.Options;
using MCModpackAutoUpdater.Security;
using MCModpackAutoUpdater.Services;

namespace MCModpackAutoUpdater.Controllers;

[Authorize(Roles = UpdaterRoles.Admin)]
public sealed class SettingsController : Controller
{
    private readonly IOptionsMonitor<StandaloneUpdaterOptions> _updaterOptions;
    private readonly UpdaterIdentityDbContext _dbContext;

    public SettingsController(
        IOptionsMonitor<StandaloneUpdaterOptions> updaterOptions,
        UpdaterIdentityDbContext dbContext)
    {
        _updaterOptions = updaterOptions;
        _dbContext = dbContext;
    }

    [HttpGet("/settings")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        return View(await BuildModelAsync(cancellationToken));
    }

    [HttpPost("/settings/runtime")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveRuntime(
        RuntimeSettingsFormModel model,
        CancellationToken cancellationToken)
    {
        if (!CanResolveTimeZone(model.ScheduleTimeZone))
        {
            ModelState.AddModelError(nameof(model.ScheduleTimeZone), "Schedule time zone must be Local, UTC, or a time zone ID available on this host.");
        }

        if (!ModelState.IsValid)
        {
            return View(nameof(Index), await BuildModelAsync(cancellationToken));
        }

        var settings = await GetOrCreateRuntimeSettingsAsync(cancellationToken);
        settings.RunOnStartup = model.RunOnStartup;
        settings.ExitAfterStartupRun = model.ExitAfterStartupRun;
        settings.LoopDelaySeconds = Math.Clamp(model.LoopDelaySeconds, 5, 3600);
        settings.ScheduleTimeZone = model.ScheduleTimeZone.Trim();
        settings.UpdatedUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        TempData["Message"] = "Runtime scheduler settings saved.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("/settings/amp-controller")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveAmpController(
        AmpControllerSettingsFormModel model,
        CancellationToken cancellationToken)
    {
        var currentUpdater = _updaterOptions.CurrentValue;
        var settings = await GetOrCreateAmpControllerSettingsAsync(cancellationToken);
        if (model.Enabled &&
            (string.IsNullOrWhiteSpace(model.ControllerApiUrl) ||
             string.IsNullOrWhiteSpace(model.Username) ||
             (string.IsNullOrWhiteSpace(model.Password) &&
              string.IsNullOrWhiteSpace(settings.Password))))
        {
            ModelState.AddModelError(string.Empty, "Enabled ADS controller mode requires URL, username, and password.");
        }

        if (!ModelState.IsValid)
        {
            return View(nameof(Index), await BuildModelAsync(cancellationToken));
        }

        settings.Enabled = model.Enabled;
        settings.ControllerApiUrl = Normalize(model.ControllerApiUrl) ?? string.Empty;
        settings.Username = Normalize(model.Username) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(model.Password))
        {
            settings.Password = model.Password;
        }

        if (!string.IsNullOrWhiteSpace(model.Token))
        {
            settings.Token = model.Token;
        }

        settings.RememberMe = model.RememberMe;
        settings.UpdatedUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        TempData["Message"] = "AMP controller settings saved.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("/settings/direct-amp")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveDirectAmp(
        DirectAmpApiSettingsFormModel model,
        CancellationToken cancellationToken)
    {
        var settings = await GetOrCreateDirectAmpApiSettingsAsync(cancellationToken);
        if (model.Enabled &&
            (string.IsNullOrWhiteSpace(model.Username) ||
             (string.IsNullOrWhiteSpace(model.Password) &&
              string.IsNullOrWhiteSpace(settings.Password))))
        {
            ModelState.AddModelError(string.Empty, "Enabled direct AMP mode requires username and password.");
        }

        if (!ModelState.IsValid)
        {
            return View(nameof(Index), await BuildModelAsync(cancellationToken));
        }

        settings.Enabled = model.Enabled;
        settings.Username = Normalize(model.Username) ?? string.Empty;
        settings.Password = string.IsNullOrWhiteSpace(model.Password) ? settings.Password : model.Password;
        settings.Token = string.IsNullOrWhiteSpace(model.Token) ? settings.Token : model.Token;
        settings.RememberMe = model.RememberMe;
        settings.WarningMessageTemplate = string.IsNullOrWhiteSpace(model.WarningMessageTemplate)
            ? new UpdaterDirectAmpApiSettings().WarningMessageTemplate
            : model.WarningMessageTemplate.Trim();
        settings.UpdatedUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        TempData["Message"] = "Direct AMP API settings saved.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("/settings/discord")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveDiscord(
        DiscordSettingsFormModel model,
        CancellationToken cancellationToken)
    {
        var settings = await GetOrCreateDiscordSettingsAsync(cancellationToken);
        if (model.Enabled &&
            string.IsNullOrWhiteSpace(model.BotToken) &&
            string.IsNullOrWhiteSpace(settings.BotToken))
        {
            ModelState.AddModelError(nameof(model.BotToken), "Enabled Discord announcements require a bot token.");
        }

        if (!ModelState.IsValid)
        {
            return View(nameof(Index), await BuildModelAsync(cancellationToken));
        }

        settings.Enabled = model.Enabled;
        if (!string.IsNullOrWhiteSpace(model.BotToken))
        {
            settings.BotToken = model.BotToken.Trim();
        }

        settings.MessageTemplate = string.IsNullOrWhiteSpace(model.MessageTemplate)
            ? new UpdaterDiscordSettings().MessageTemplate
            : model.MessageTemplate.Trim();
        settings.UpdatedUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        TempData["Message"] = "Discord announcement settings saved.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("/settings/modpacks/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateModpack(
        ModpackSettingsFormModel model,
        CancellationToken cancellationToken)
    {
        ValidateModpack(model);
        if (!model.AgentNodeId.HasValue || !await _dbContext.UpdaterAgentNodes.AnyAsync(agent => agent.Id == model.AgentNodeId.Value, cancellationToken))
        {
            ModelState.AddModelError(nameof(model.AgentNodeId), "Assigned agent is required.");
        }

        if (await _dbContext.UpdaterModpackProfiles.AnyAsync(profile => profile.Name == model.Name.Trim(), cancellationToken))
        {
            ModelState.AddModelError(nameof(model.Name), "A profile with this name already exists.");
        }

        if (!ModelState.IsValid)
        {
            return View(nameof(Index), await BuildModelAsync(cancellationToken));
        }

        var utcNow = DateTime.UtcNow;
        _dbContext.UpdaterModpackProfiles.Add(ToEntity(model, utcNow));
        await _dbContext.SaveChangesAsync(cancellationToken);
        TempData["Message"] = $"Modpack '{model.Name}' created.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("/settings/modpacks/update")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateModpack(
        ModpackSettingsFormModel model,
        CancellationToken cancellationToken)
    {
        ValidateModpack(model);
        var entity = await _dbContext.UpdaterModpackProfiles.FirstOrDefaultAsync(profile => profile.Id == model.Id, cancellationToken);
        if (entity is null)
        {
            ModelState.AddModelError(string.Empty, "Modpack profile was not found.");
        }

        if (!model.AgentNodeId.HasValue || !await _dbContext.UpdaterAgentNodes.AnyAsync(agent => agent.Id == model.AgentNodeId.Value, cancellationToken))
        {
            ModelState.AddModelError(nameof(model.AgentNodeId), "Assigned agent is required.");
        }

        if (await _dbContext.UpdaterModpackProfiles.AnyAsync(profile => profile.Id != model.Id && profile.Name == model.Name.Trim(), cancellationToken))
        {
            ModelState.AddModelError(nameof(model.Name), "A profile with this name already exists.");
        }

        if (!ModelState.IsValid)
        {
            return View(nameof(Index), await BuildModelAsync(cancellationToken));
        }

        ApplyToEntity(entity!, model);
        await _dbContext.SaveChangesAsync(cancellationToken);
        TempData["Message"] = $"Modpack '{model.Name}' updated.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("/settings/modpacks/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteModpack(int index, CancellationToken cancellationToken)
    {
        var entity = await _dbContext.UpdaterModpackProfiles.FirstOrDefaultAsync(profile => profile.Id == index, cancellationToken);
        if (entity is null)
        {
            TempData["Message"] = "Modpack profile was not found.";
            return RedirectToAction(nameof(Index));
        }

        var name = entity.Name;
        _dbContext.UpdaterModpackProfiles.Remove(entity);
        await _dbContext.SaveChangesAsync(cancellationToken);
        TempData["Message"] = $"Modpack '{name}' deleted.";
        return RedirectToAction(nameof(Index));
    }

    private async Task<SettingsIndexViewModel> BuildModelAsync(CancellationToken cancellationToken)
    {
        var runtime = await GetOrCreateRuntimeSettingsAsync(cancellationToken);
        var ampApi = await GetOrCreateDirectAmpApiSettingsAsync(cancellationToken);
        var ampController = await GetOrCreateAmpControllerSettingsAsync(cancellationToken);
        var discord = await GetOrCreateDiscordSettingsAsync(cancellationToken);
        var discordFailures = await _dbContext.UpdaterDiscordAnnouncements
            .AsNoTracking()
            .Include(announcement => announcement.ModpackProfile)
            .Where(announcement => announcement.Status == UpdaterDiscordAnnouncementStatus.Failed)
            .OrderByDescending(announcement => announcement.UpdatedUtc)
            .Take(10)
            .Select(announcement => new DiscordAnnouncementStatusViewModel
            {
                Id = announcement.Id,
                ModpackName = announcement.ModpackProfile == null ? "Unknown" : announcement.ModpackProfile.Name,
                Status = announcement.Status,
                RetryCount = announcement.RetryCount,
                FailureReason = announcement.FailureReason,
                UpdatedUtc = announcement.UpdatedUtc
            })
            .ToListAsync(cancellationToken);
        var profiles = await _dbContext.UpdaterModpackProfiles
            .AsNoTracking()
            .OrderBy(profile => profile.Name)
            .ToListAsync(cancellationToken);
        var agents = await _dbContext.UpdaterAgentNodes
            .AsNoTracking()
            .OrderBy(agent => agent.Name)
            .ToListAsync(cancellationToken);
        var defaultAgentId = agents.FirstOrDefault(agent => agent.Enabled)?.Id ?? agents.FirstOrDefault()?.Id;

        return new SettingsIndexViewModel
        {
            Runtime = new RuntimeSettingsFormModel
            {
                RunOnStartup = runtime.RunOnStartup,
                ExitAfterStartupRun = runtime.ExitAfterStartupRun,
                LoopDelaySeconds = runtime.LoopDelaySeconds,
                ScheduleTimeZone = runtime.ScheduleTimeZone
            },
            AmpController = new AmpControllerSettingsFormModel
            {
                Enabled = ampController.Enabled,
                ControllerApiUrl = ampController.ControllerApiUrl,
                Username = ampController.Username,
                RememberMe = ampController.RememberMe
            },
            DirectAmpApi = new DirectAmpApiSettingsFormModel
            {
                Enabled = ampApi.Enabled,
                Username = ampApi.Username,
                RememberMe = ampApi.RememberMe,
                WarningMessageTemplate = ampApi.WarningMessageTemplate
            },
            Discord = new DiscordSettingsFormModel
            {
                Enabled = discord.Enabled,
                MessageTemplate = discord.MessageTemplate
            },
            DiscordFailures = discordFailures,
            Modpacks = profiles.Select(ToForm).ToArray(),
            NewModpack = new ModpackSettingsFormModel
            {
                AgentNodeId = defaultAgentId,
                InstallRootPath = "/home/amp/.ampdata/instances/",
                OverrideDirectory = ".a UPDATE Files"
            },
            AgentOptions = agents.Select(agent => new AgentOptionViewModel
            {
                Id = agent.Id,
                Name = agent.Name,
                Host = agent.Host,
                ExecutionMode = agent.ExecutionMode,
                Enabled = agent.Enabled
            }).ToArray()
        };
    }

    private async Task<UpdaterAmpControllerSettings> GetOrCreateAmpControllerSettingsAsync(
        CancellationToken cancellationToken)
    {
        var settings = await _dbContext.UpdaterAmpControllerSettings.FirstOrDefaultAsync(cancellationToken);
        if (settings is not null)
        {
            return settings;
        }

        var utcNow = DateTime.UtcNow;
        var configured = _updaterOptions.CurrentValue.AmpController;
        settings = new UpdaterAmpControllerSettings
        {
            Enabled = configured.Enabled,
            ControllerApiUrl = configured.ControllerApiUrl.Trim(),
            Username = configured.Username.Trim(),
            Password = configured.Password,
            Token = configured.Token,
            RememberMe = configured.RememberMe,
            CreatedUtc = utcNow,
            UpdatedUtc = utcNow
        };
        _dbContext.UpdaterAmpControllerSettings.Add(settings);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return settings;
    }

    private async Task<UpdaterDirectAmpApiSettings> GetOrCreateDirectAmpApiSettingsAsync(
        CancellationToken cancellationToken)
    {
        var settings = await _dbContext.UpdaterDirectAmpApiSettings.FirstOrDefaultAsync(cancellationToken);
        if (settings is not null)
        {
            return settings;
        }

        var utcNow = DateTime.UtcNow;
        settings = new UpdaterDirectAmpApiSettings
        {
            CreatedUtc = utcNow,
            UpdatedUtc = utcNow
        };
        _dbContext.UpdaterDirectAmpApiSettings.Add(settings);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return settings;
    }

    private async Task<UpdaterRuntimeSettings> GetOrCreateRuntimeSettingsAsync(
        CancellationToken cancellationToken)
    {
        var settings = await _dbContext.UpdaterRuntimeSettings.FirstOrDefaultAsync(cancellationToken);
        if (settings is not null)
        {
            return settings;
        }

        var utcNow = DateTime.UtcNow;
        var configured = _updaterOptions.CurrentValue;
        settings = new UpdaterRuntimeSettings
        {
            RunOnStartup = configured.RunOnStartup,
            ExitAfterStartupRun = configured.ExitAfterStartupRun,
            LoopDelaySeconds = Math.Clamp(configured.LoopDelaySeconds, 5, 3600),
            ScheduleTimeZone = string.IsNullOrWhiteSpace(configured.ScheduleTimeZone)
                ? "America/New_York"
                : configured.ScheduleTimeZone.Trim(),
            CreatedUtc = utcNow,
            UpdatedUtc = utcNow
        };
        _dbContext.UpdaterRuntimeSettings.Add(settings);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return settings;
    }

    private async Task<UpdaterDiscordSettings> GetOrCreateDiscordSettingsAsync(
        CancellationToken cancellationToken)
    {
        var settings = await _dbContext.UpdaterDiscordSettings.FirstOrDefaultAsync(cancellationToken);
        if (settings is not null)
        {
            return settings;
        }

        var utcNow = DateTime.UtcNow;
        settings = new UpdaterDiscordSettings
        {
            CreatedUtc = utcNow,
            UpdatedUtc = utcNow
        };
        _dbContext.UpdaterDiscordSettings.Add(settings);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return settings;
    }

    private static ModpackSettingsFormModel ToForm(UpdaterModpackProfile profile)
    {
        return new ModpackSettingsFormModel
        {
            Id = profile.Id,
            AgentNodeId = profile.AgentNodeId,
            Enabled = profile.Enabled,
            RunOnStartup = profile.RunOnStartup,
            Name = profile.Name,
            Provider = profile.Provider,
            SourceReference = profile.SourceReference,
            ServerPackUrl = profile.ServerPackUrl,
            BuildServerPackFromClientFiles = profile.BuildServerPackFromClientFiles,
            ServerPackExcludedPathsText = profile.ServerPackExcludedPaths,
            ServerPackExcludedCurseForgeProjectIdsText = profile.ServerPackExcludedCurseForgeProjectIds,
            VersionLock = profile.VersionLock,
            CurrentVersion = profile.CurrentVersion,
            InstallRootPath = profile.InstallRootPath,
            OverrideDirectory = profile.OverrideDirectory,
            PreservedPathsText = profile.PreservedPaths,
            ScheduleTime = profile.ScheduleTime,
            RestartMode = profile.RestartMode,
            WarningMinutes = profile.WarningMinutes,
            AmpInstanceName = profile.AmpInstanceName,
            AmpApiUrl = profile.AmpApiUrl,
            AmpConfigValuesJson = profile.AmpConfigValuesJson,
            DiscordAnnouncementChannelId = profile.DiscordAnnouncementChannelId,
            DiscordAnnouncementRoleId = profile.DiscordAnnouncementRoleId,
            RequestedVersion = profile.RequestedVersion,
            ForceFullSync = profile.ForceFullSync,
            SkipWarnings = profile.SkipWarnings,
            IgnoreCurrentVersion = profile.IgnoreCurrentVersion
        };
    }

    private static UpdaterModpackProfile ToEntity(ModpackSettingsFormModel model, DateTime utcNow)
    {
        var entity = new UpdaterModpackProfile
        {
            CreatedUtc = utcNow
        };
        ApplyToEntity(entity, model);
        entity.CreatedUtc = utcNow;
        return entity;
    }

    private static void ApplyToEntity(UpdaterModpackProfile entity, ModpackSettingsFormModel model)
    {
        entity.AgentNodeId = model.AgentNodeId;
        entity.UpdatedUtc = DateTime.UtcNow;
        entity.Enabled = model.Enabled;
        entity.RunOnStartup = model.RunOnStartup;
        entity.Name = model.Name.Trim();
        entity.Provider = NormalizeProvider(model.Provider);
        entity.SourceReference = Normalize(model.SourceReference) ?? string.Empty;
        entity.ServerPackUrl = Normalize(model.ServerPackUrl);
        entity.BuildServerPackFromClientFiles = model.BuildServerPackFromClientFiles;
        entity.ServerPackExcludedPaths = NormalizePathListText(model.ServerPackExcludedPathsText);
        entity.ServerPackExcludedCurseForgeProjectIds = NormalizePositiveIntegerListText(model.ServerPackExcludedCurseForgeProjectIdsText);
        entity.VersionLock = Normalize(model.VersionLock);
        entity.CurrentVersion = Normalize(model.CurrentVersion) ?? string.Empty;
        entity.InstallRootPath = model.InstallRootPath.Trim();
        entity.OverrideDirectory = Normalize(model.OverrideDirectory);
        entity.PreservedPaths = NormalizePathListText(model.PreservedPathsText);
        entity.ScheduleTime = Normalize(model.ScheduleTime);
        entity.RestartMode = model.RestartMode.Trim();
        entity.WarningMinutes = model.WarningMinutes;
        entity.AmpInstanceName = Normalize(model.AmpInstanceName);
        entity.AmpApiUrl = Normalize(model.AmpApiUrl);
        entity.AmpConfigValuesJson = Normalize(model.AmpConfigValuesJson);
        entity.DiscordAnnouncementChannelId = Normalize(model.DiscordAnnouncementChannelId);
        entity.DiscordAnnouncementRoleId = Normalize(model.DiscordAnnouncementRoleId);
        entity.RequestedVersion = Normalize(model.RequestedVersion);
        entity.ForceFullSync = model.ForceFullSync;
        entity.SkipWarnings = model.SkipWarnings;
        entity.IgnoreCurrentVersion = model.IgnoreCurrentVersion;
    }

    private void ValidateModpack(ModpackSettingsFormModel model)
    {
        if (string.IsNullOrWhiteSpace(model.SourceReference) && string.IsNullOrWhiteSpace(model.ServerPackUrl))
        {
            ModelState.AddModelError(nameof(model.SourceReference), "Source reference or server pack URL is required.");
        }

        if (!string.IsNullOrWhiteSpace(model.AmpConfigValuesJson))
        {
            try
            {
                using var document = JsonDocument.Parse(model.AmpConfigValuesJson);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    ModelState.AddModelError(nameof(model.AmpConfigValuesJson), "AMP config JSON must be a JSON object.");
                }
            }
            catch (JsonException)
            {
                ModelState.AddModelError(nameof(model.AmpConfigValuesJson), "AMP config JSON is invalid.");
            }
        }

        if (!string.IsNullOrWhiteSpace(model.ScheduleTime) &&
            !TimeSpan.TryParseExact(model.ScheduleTime.Trim(), "hh\\:mm", CultureInfo.InvariantCulture, out _))
        {
            ModelState.AddModelError(nameof(model.ScheduleTime), "Daily check time must use HH:mm.");
        }

        if (!string.IsNullOrWhiteSpace(model.ServerPackUrl) && !IsAbsoluteHttpUrl(model.ServerPackUrl))
        {
            ModelState.AddModelError(nameof(model.ServerPackUrl), "Server pack URL must be an absolute HTTP/HTTPS URL.");
        }

        if (!string.IsNullOrWhiteSpace(model.AmpApiUrl) && !IsAbsoluteHttpUrl(model.AmpApiUrl))
        {
            ModelState.AddModelError(nameof(model.AmpApiUrl), "AMP instance API URL must be an absolute HTTP/HTTPS URL.");
        }

        ValidatePathList(model.PreservedPathsText, nameof(model.PreservedPathsText));
        ValidatePathList(model.ServerPackExcludedPathsText, nameof(model.ServerPackExcludedPathsText));
        ValidatePositiveIntegerList(model.ServerPackExcludedCurseForgeProjectIdsText, nameof(model.ServerPackExcludedCurseForgeProjectIdsText), "Excluded CurseForge project IDs must be positive integers.");
        ValidateDigitsOnly(model.DiscordAnnouncementChannelId, nameof(model.DiscordAnnouncementChannelId), "Discord channel ID must contain digits only.");
        ValidateDigitsOnly(model.DiscordAnnouncementRoleId, nameof(model.DiscordAnnouncementRoleId), "Discord role ID must contain digits only.");

        if (model.BuildServerPackFromClientFiles &&
            !string.Equals(NormalizeProvider(model.Provider), "CurseForge", StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(nameof(model.BuildServerPackFromClientFiles), "Building server packs from client files is only supported for CurseForge profiles.");
        }

        if (IsAmpControllerRestartMode(model.RestartMode) &&
            string.IsNullOrWhiteSpace(model.AmpApiUrl) &&
            string.IsNullOrWhiteSpace(model.AmpInstanceName))
        {
            ModelState.AddModelError(nameof(model.AmpInstanceName), "AMP instance name is required for ADS/AMP controller restart mode.");
        }
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string NormalizeProvider(string? provider)
    {
        var normalized = Normalize(provider) ?? "CurseForge";
        return string.Equals(normalized, "Custom", StringComparison.OrdinalIgnoreCase)
            ? "Direct"
            : normalized;
    }

    private static bool CanResolveTimeZone(string? timeZoneId)
    {
        try
        {
            StandaloneTimeZoneResolver.Resolve(timeZoneId);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsAbsoluteHttpUrl(string value)
    {
        return Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static bool IsAmpControllerRestartMode(string? restartMode)
    {
        return string.Equals(restartMode?.Trim(), "amp", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(restartMode?.Trim(), "amp_api", StringComparison.OrdinalIgnoreCase);
    }

    private void ValidatePathList(string? text, string fieldName)
    {
        foreach (var path in SplitLines(text))
        {
            if (NormalizeRelativePath(path) is null)
            {
                ModelState.AddModelError(fieldName, $"Path '{path}' must be relative, cannot contain wildcards, and cannot include . or .. segments.");
            }
        }
    }

    private void ValidatePositiveIntegerList(string? text, string fieldName, string message)
    {
        foreach (var value in SplitLines(text))
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
            {
                ModelState.AddModelError(fieldName, message);
                return;
            }
        }
    }

    private void ValidateDigitsOnly(string? value, string fieldName, string message)
    {
        if (!string.IsNullOrWhiteSpace(value) && !Regex.IsMatch(value.Trim(), "^[0-9]+$"))
        {
            ModelState.AddModelError(fieldName, message);
        }
    }

    private static string? NormalizePathListText(string? text)
    {
        var paths = SplitLines(text)
            .Select(NormalizeRelativePath)
            .Where(static path => path is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return paths.Length == 0 ? null : string.Join(Environment.NewLine, paths);
    }

    private static string? NormalizePositiveIntegerListText(string? text)
    {
        var values = SplitLines(text)
            .Select(static value => int.Parse(value, CultureInfo.InvariantCulture))
            .Distinct()
            .Order()
            .Select(static value => value.ToString(CultureInfo.InvariantCulture))
            .ToArray();

        return values.Length == 0 ? null : string.Join(Environment.NewLine, values);
    }

    private static IEnumerable<string> SplitLines(string? text)
    {
        return (text ?? string.Empty)
            .Split(["\r\n", "\n", "\r", ",", ";"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static value => !string.IsNullOrWhiteSpace(value));
    }

    private static string? NormalizeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var normalized = path.Replace('\\', '/').Trim();
        if (normalized.StartsWith("/", StringComparison.Ordinal) ||
            Path.IsPathRooted(normalized) ||
            normalized.IndexOfAny(['*', '?']) >= 0)
        {
            return null;
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0 || segments.Any(static segment => segment is "." or ".."))
        {
            return null;
        }

        return string.Join('/', segments);
    }

}
