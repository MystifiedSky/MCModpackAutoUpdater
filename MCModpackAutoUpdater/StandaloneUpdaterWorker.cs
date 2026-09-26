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

        UpdaterRuntimeSettings startupRuntimeSettings;
        try
        {
            startupRuntimeSettings = await GetRuntimeSettingsAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not read startup runtime settings; using configured defaults.");
            startupRuntimeSettings = CreateRuntimeSettingsFromOptions();
        }

        if (startupRuntimeSettings.ExitAfterStartupRun)
        {
            var commandIds = await RunStartupChecksAsync(forceAllEnabledProfiles: true, stoppingToken);
            await WaitForStartupCommandsAsync(commandIds, stoppingToken);
            _logger.LogInformation("ExitAfterStartupRun is enabled; startup commands have reached a final state.");
            _applicationLifetime.StopApplication();
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var delaySeconds = Math.Clamp(_options.CurrentValue.LoopDelaySeconds, 5, 3600);
            try
            {
                var runtimeSettings = await GetRuntimeSettingsAsync(stoppingToken);
                await RunStartupChecksAsync(forceAllEnabledProfiles: false, stoppingToken);
                await RunDueScheduledChecksAsync(runtimeSettings, stoppingToken);
                delaySeconds = Math.Clamp(runtimeSettings.LoopDelaySeconds, 5, 3600);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Standalone updater loop failed.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("MCModpackAutoUpdater stopping.");
    }

    internal async Task<IReadOnlyList<int>> RunStartupChecksAsync(
        bool forceAllEnabledProfiles,
        CancellationToken cancellationToken)
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
        var commandIds = new List<int>();

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
                if (forceAllEnabledProfiles && profile.AgentNode is not null)
                {
                    commandIds.AddRange(await ReadActiveSyncCommandIdsAsync(
                        dbContext,
                        profile.Id,
                        profile.AgentNode.Id,
                        cancellationToken));
                }

                continue;
            }

            try
            {
                var command = await commandService.QueueSyncCommandAsync(
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
                commandIds.Add(command.Id);
            }
            catch (DuplicateSyncCommandException)
            {
                _logger.LogInformation(
                    "Startup queue skipped for {ProfileName}; a sync command became active concurrently.",
                    profile.Name);
                if (forceAllEnabledProfiles && profile.AgentNode is not null)
                {
                    commandIds.AddRange(await ReadActiveSyncCommandIdsAsync(
                        dbContext,
                        profile.Id,
                        profile.AgentNode.Id,
                        cancellationToken));
                }
            }
        }

        return commandIds.Distinct().ToArray();
    }

    internal async Task RunDueScheduledChecksAsync(
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

            try
            {
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
            catch (DuplicateSyncCommandException)
            {
                _logger.LogInformation(
                    "Scheduled queue skipped for {ProfileName}; a sync command became active concurrently.",
                    profile.Name);
            }
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

        return CreateRuntimeSettingsFromOptions();
    }

    private UpdaterRuntimeSettings CreateRuntimeSettingsFromOptions()
    {
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

    private async Task WaitForStartupCommandsAsync(
        IReadOnlyList<int> commandIds,
        CancellationToken cancellationToken)
    {
        if (commandIds.Count == 0)
        {
            return;
        }

        _logger.LogInformation(
            "Waiting for {CommandCount} startup sync command(s) to finish before exiting.",
            commandIds.Count);

        while (!cancellationToken.IsCancellationRequested)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<UpdaterIdentityDbContext>();
            var hasActiveCommands = await dbContext.UpdaterAgentCommands
                .AsNoTracking()
                .AnyAsync(
                    command =>
                        commandIds.Contains(command.Id) &&
                        (command.Status == UpdaterAgentCommandStatus.Pending ||
                         command.Status == UpdaterAgentCommandStatus.InProgress),
                    cancellationToken);
            if (!hasActiveCommands)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    private static async Task<IReadOnlyList<int>> ReadActiveSyncCommandIdsAsync(
        UpdaterIdentityDbContext dbContext,
        int modpackId,
        int agentNodeId,
        CancellationToken cancellationToken)
    {
        var marker = $"\"modpack\":{{\"id\":{modpackId},";
        return await dbContext.UpdaterAgentCommands
            .AsNoTracking()
            .Where(command =>
                command.AgentNodeId == agentNodeId &&
                command.CommandType == "sync_modpack" &&
                (command.Status == UpdaterAgentCommandStatus.Pending ||
                 command.Status == UpdaterAgentCommandStatus.InProgress) &&
                EF.Functions.Like(command.PayloadJson, $"%{marker}%"))
            .Select(command => command.Id)
            .ToListAsync(cancellationToken);
    }
}
