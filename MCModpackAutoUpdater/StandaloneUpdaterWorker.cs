using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MCModpackAutoUpdater.Data;
using MCModpackAutoUpdater.Options;
using MCModpackAutoUpdater.Services;

namespace MCModpackAutoUpdater;

public sealed class StandaloneUpdaterWorker : BackgroundService
{
    private readonly ILogger<StandaloneUpdaterWorker> _logger;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly IOptionsMonitor<StandaloneUpdaterOptions> _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly HashSet<string> _startupRuns = new(StringComparer.OrdinalIgnoreCase);

    public StandaloneUpdaterWorker(
        ILogger<StandaloneUpdaterWorker> logger,
        IHostApplicationLifetime applicationLifetime,
        IOptionsMonitor<StandaloneUpdaterOptions> options,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _applicationLifetime = applicationLifetime;
        _options = options;
        _serviceProvider = serviceProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MCModpackAutoUpdater starting.");

        var startupRuntimeSettings = await GetRuntimeSettingsAsync(stoppingToken);
        if (startupRuntimeSettings.ExitAfterStartupRun)
        {
            await RunStartupChecksAsync(forceAllEnabledProfiles: true, stoppingToken);
            _logger.LogInformation("ExitAfterStartupRun is enabled; stopping after startup checks.");
            _applicationLifetime.StopApplication();
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var runtimeSettings = await GetRuntimeSettingsAsync(stoppingToken);
            try
            {
                await RunStartupChecksAsync(forceAllEnabledProfiles: false, stoppingToken);
                await RunDueScheduledChecksAsync(runtimeSettings, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Standalone updater loop failed.");
            }

            var delaySeconds = Math.Clamp(runtimeSettings.LoopDelaySeconds, 5, 3600);
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);
        }

        _logger.LogInformation("MCModpackAutoUpdater stopping.");
    }

    private async Task RunStartupChecksAsync(bool forceAllEnabledProfiles, CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<UpdaterIdentityDbContext>();
        var commandService = scope.ServiceProvider.GetRequiredService<UpdaterCommandService>();

        var runtimeSettings = await GetRuntimeSettingsAsync(cancellationToken);
        var profiles = await dbContext.UpdaterModpackProfiles
            .Include(profile => profile.AgentNode)
            .Where(profile => profile.Enabled)
            .OrderBy(profile => profile.Id)
            .ToListAsync(cancellationToken);

        foreach (var profile in profiles)
        {
            var runOnStartup = forceAllEnabledProfiles || (profile.RunOnStartup ?? runtimeSettings.RunOnStartup);
            if (!runOnStartup)
            {
                continue;
            }

            if (!_startupRuns.Add(profile.Id.ToString(CultureInfo.InvariantCulture)))
            {
                continue;
            }

            if (profile.AgentNode is null || !profile.AgentNode.Enabled)
            {
                _logger.LogWarning("Startup check skipped for {ProfileName}; assigned agent is missing or disabled.", profile.Name);
                continue;
            }

            if (await commandService.HasActiveSyncCommandForModpackAsync(profile.Id, cancellationToken))
            {
                continue;
            }

            await commandService.QueueSyncCommandAsync(
                profile,
                profile.AgentNode,
                profile.RequestedVersion,
                profile.ForceFullSync,
                profile.SkipWarnings,
                forceAllEnabledProfiles || profile.IgnoreCurrentVersion,
                "scheduler",
                forceAllEnabledProfiles ? "startup:exit-after-run" : "startup",
                profile.RequestedVersion,
                null,
                cancellationToken);
        }
    }

    private async Task RunDueScheduledChecksAsync(
        UpdaterRuntimeSettings runtimeSettings,
        CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<UpdaterIdentityDbContext>();
        var resolver = scope.ServiceProvider.GetRequiredService<IModpackVersionResolver>();
        var commandService = scope.ServiceProvider.GetRequiredService<UpdaterCommandService>();

        var timeZone = StandaloneTimeZoneResolver.Resolve(runtimeSettings.ScheduleTimeZone);
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone);
        var currentDate = DateOnly.FromDateTime(now.DateTime);
        var currentDateText = currentDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var profiles = await dbContext.UpdaterModpackProfiles
            .Include(profile => profile.AgentNode)
            .Where(profile => profile.Enabled && !string.IsNullOrWhiteSpace(profile.ScheduleTime))
            .OrderBy(profile => profile.Id)
            .ToListAsync(cancellationToken);

        foreach (var profile in profiles)
        {
            if (!TryParseScheduleTime(profile.ScheduleTime, out var scheduledTime))
            {
                continue;
            }

            if (now.TimeOfDay < scheduledTime)
            {
                continue;
            }

            if (string.Equals(profile.LastScheduledCheckDate, currentDateText, StringComparison.Ordinal))
            {
                continue;
            }

            profile.LastScheduledCheckDate = currentDateText;
            profile.LastScheduledCheckUtc = DateTime.UtcNow;
            profile.UpdatedUtc = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);

            if (profile.AgentNode is null || !profile.AgentNode.Enabled)
            {
                _logger.LogWarning("Scheduled check skipped for {ProfileName}; assigned agent is missing or disabled.", profile.Name);
                continue;
            }

            if (await commandService.HasActiveSyncCommandForModpackAsync(profile.Id, cancellationToken))
            {
                continue;
            }

            var resolution = await resolver.ResolveTargetVersionAsync(profile, null, cancellationToken);
            if (!resolution.Success || string.IsNullOrWhiteSpace(resolution.TargetVersion))
            {
                _logger.LogWarning("Scheduled update check failed for {ProfileName}: {Message}", profile.Name, resolution.Message);
                continue;
            }

            if (resolution.IsUpToDate(profile.CurrentVersion))
            {
                continue;
            }

            await commandService.QueueSyncCommandAsync(
                profile,
                profile.AgentNode,
                resolution.TargetVersion,
                profile.ForceFullSync,
                profile.SkipWarnings,
                ignoreCurrentVersion: false,
                "scheduler",
                $"scheduler:{timeZone.Id}",
                resolution.TargetVersion,
                resolution.TargetVersionDisplay,
                cancellationToken);
        }
    }

    private static bool TryParseScheduleTime(string? schedule, out TimeSpan parsedTime)
    {
        parsedTime = default;
        if (string.IsNullOrWhiteSpace(schedule))
        {
            return false;
        }

        return TimeSpan.TryParseExact(
                   schedule.Trim(),
                   "hh\\:mm",
                   CultureInfo.InvariantCulture,
                   out parsedTime) &&
               parsedTime.TotalHours < 24;
    }

    private async Task<UpdaterRuntimeSettings> GetRuntimeSettingsAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<UpdaterIdentityDbContext>();
        var settings = await dbContext.UpdaterRuntimeSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);
        if (settings is not null)
        {
            return settings;
        }

        var options = _options.CurrentValue;
        return new UpdaterRuntimeSettings
        {
            RunOnStartup = options.RunOnStartup,
            ExitAfterStartupRun = options.ExitAfterStartupRun,
            LoopDelaySeconds = Math.Clamp(options.LoopDelaySeconds, 5, 3600),
            ScheduleTimeZone = string.IsNullOrWhiteSpace(options.ScheduleTimeZone)
                ? "America/New_York"
                : options.ScheduleTimeZone.Trim()
        };
    }
}
