using Microsoft.EntityFrameworkCore;
using MCAgent.Models.AgentApi;
using MCModpackAutoUpdater.Data;
using System.Text.Json;

namespace MCModpackAutoUpdater.Services;

public sealed class UpdaterCommandService
{
    private readonly UpdaterIdentityDbContext _dbContext;

    public UpdaterCommandService(UpdaterIdentityDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<bool> HasActiveSyncCommandForModpackAsync(
        int modpackId,
        CancellationToken cancellationToken)
    {
        var active = await _dbContext.UpdaterAgentCommands
            .AsNoTracking()
            .Where(
                command =>
                    command.CommandType == "sync_modpack" &&
                    (command.Status == UpdaterAgentCommandStatus.Pending ||
                     command.Status == UpdaterAgentCommandStatus.InProgress))
            .Select(command => new { command.PayloadJson, ProfileId = command.ModpackUpdateAudit == null ? (int?)null : command.ModpackUpdateAudit.ModpackProfileId })
            .ToListAsync(cancellationToken);
        return active.Any(command => command.ProfileId == modpackId || ReadModpackId(command.PayloadJson) == modpackId);
    }

    public async Task<UpdaterAgentCommand> QueueSyncCommandAsync(
        UpdaterModpackProfile modpack,
        UpdaterAgentNode agent,
        string? requestedVersion,
        bool forceFullSync,
        bool skipWarnings,
        bool ignoreCurrentVersion,
        string queuedBy,
        string triggerSource,
        string? resolvedTargetVersion,
        string? resolvedTargetVersionDisplay,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        if (!await _dbContext.UpdaterModpackProfiles.AsNoTracking()
                .AnyAsync(profile => profile.Id == modpack.Id && profile.AgentNodeId == agent.Id, cancellationToken))
        {
            throw new InvalidOperationException("The profile no longer exists or its assigned agent changed. Refresh before queueing an update.");
        }
        if (await HasActiveSyncCommandForModpackAsync(modpack.Id, cancellationToken))
        {
            throw new DuplicateSyncCommandException();
        }
        if (!agent.Enabled)
        {
            throw new InvalidOperationException("The assigned agent is disabled.");
        }
        var utcNow = DateTime.UtcNow;
        var payload = SyncModpackCommandPayloadBuilder.Build(
            modpack,
            agent,
            requestedVersion,
            forceFullSync,
            skipWarnings,
            ignoreCurrentVersion,
            queuedBy,
            utcNow);

        var command = new UpdaterAgentCommand
        {
            AgentNodeId = agent.Id,
            CommandType = "sync_modpack",
            PayloadJson = payload,
            Status = UpdaterAgentCommandStatus.Pending,
            CreatedUtc = utcNow,
            UpdatedUtc = utcNow
        };

        _dbContext.UpdaterAgentCommands.Add(command);
        _dbContext.UpdaterModpackUpdateAudits.Add(new UpdaterModpackUpdateAudit
        {
            ModpackProfileId = modpack.Id,
            AgentNodeId = agent.Id,
            AgentCommand = command,
            TriggerSource = triggerSource,
            RequestedVersion = requestedVersion,
            PreviousVersion = modpack.CurrentVersion,
            TargetVersion = resolvedTargetVersion,
            TargetVersionDisplay = resolvedTargetVersionDisplay,
            Status = UpdaterModpackUpdateAuditStatus.Queued,
            Summary = $"Queued by {queuedBy}.",
            CreatedUtc = utcNow,
            UpdatedUtc = utcNow
        });

        modpack.LastQueuedUtc = utcNow;
        modpack.UpdatedUtc = utcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return command;
    }

    public async Task<AgentCommandAckResponse?> AcknowledgeCommandAsync(
        int commandId,
        int agentId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        var command = await _dbContext.UpdaterAgentCommands
            .FirstOrDefaultAsync(
                current => current.Id == commandId && current.AgentNodeId == agentId,
                cancellationToken);

        if (command is null)
        {
            return null;
        }

        await _dbContext.Entry(command).ReloadAsync(cancellationToken);

        if (UpdaterAgentCommandStatus.FinalStatuses.Contains(command.Status, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Command is already finalized.");
        }

        var utcNow = DateTime.UtcNow;
        if (command.Status == UpdaterAgentCommandStatus.Pending)
        {
            command.Status = UpdaterAgentCommandStatus.InProgress;
            command.AcknowledgedUtc = utcNow;
            command.UpdatedUtc = utcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new AgentCommandAckResponse
        {
            CommandId = command.Id,
            Status = command.Status,
            ServerTimeUtc = utcNow
        };
    }

    public async Task<AgentCommandAckResponse?> CompleteCommandAsync(
        int commandId,
        int agentId,
        AgentCommandCompletionRequest request,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        var command = await _dbContext.UpdaterAgentCommands
            .Include(current => current.ModpackUpdateAudit)
            .FirstOrDefaultAsync(
                current => current.Id == commandId && current.AgentNodeId == agentId,
                cancellationToken);

        if (command is null)
        {
            return null;
        }

        await _dbContext.Entry(command).ReloadAsync(cancellationToken);

        if (UpdaterAgentCommandStatus.FinalStatuses.Contains(command.Status, StringComparer.Ordinal))
        {
            // An agent can lose the HTTP response after a committed completion. A retry
            // of that same result must not execute another update or announcement.
            var expectedStatus = request.Success ? UpdaterAgentCommandStatus.Completed : UpdaterAgentCommandStatus.Failed;
            if (command.Status == expectedStatus &&
                command.ResultSummary == TruncateOrNull(request.Summary, 500) &&
                command.ResultPayloadJson == TruncateOrNull(request.ResultPayloadJson, 20000))
            {
                return new AgentCommandAckResponse { CommandId = command.Id, Status = command.Status, ServerTimeUtc = DateTime.UtcNow };
            }
            throw new InvalidOperationException("Command is already finalized.");
        }

        var utcNow = DateTime.UtcNow;
        command.Status = request.Success
            ? UpdaterAgentCommandStatus.Completed
            : UpdaterAgentCommandStatus.Failed;
        command.CompletedUtc = utcNow;
        command.UpdatedUtc = utcNow;
        command.ResultSummary = TruncateOrNull(request.Summary, 500);
        command.ResultPayloadJson = TruncateOrNull(request.ResultPayloadJson, 20000);

        if (string.Equals(command.CommandType, "sync_modpack", StringComparison.OrdinalIgnoreCase))
        {
            await CompleteSyncCommandAsync(command, request, utcNow, cancellationToken);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AgentCommandAckResponse
        {
            CommandId = command.Id,
            Status = command.Status,
            ServerTimeUtc = utcNow
        };
    }

    public async Task CancelCommandAsync(int commandId, string cancelledBy, CancellationToken cancellationToken)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        var command = await _dbContext.UpdaterAgentCommands
            .Include(current => current.ModpackUpdateAudit)
            .FirstOrDefaultAsync(current => current.Id == commandId, cancellationToken);

        if (command is null)
        {
            return;
        }
        await _dbContext.Entry(command).ReloadAsync(cancellationToken);
        if (command.Status != UpdaterAgentCommandStatus.Pending)
        {
            return;
        }

        var utcNow = DateTime.UtcNow;
        command.Status = UpdaterAgentCommandStatus.Cancelled;
        command.CompletedUtc = utcNow;
        command.UpdatedUtc = utcNow;
        command.ResultSummary = $"Cancelled by {cancelledBy} at {utcNow:u}.";

        if (command.ModpackUpdateAudit is not null)
        {
            command.ModpackUpdateAudit.Status = UpdaterModpackUpdateAuditStatus.Cancelled;
            command.ModpackUpdateAudit.CompletedUtc = utcNow;
            command.ModpackUpdateAudit.UpdatedUtc = utcNow;
            command.ModpackUpdateAudit.Summary = command.ResultSummary;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task CompleteSyncCommandAsync(
        UpdaterAgentCommand command,
        AgentCommandCompletionRequest request,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        var audit = command.ModpackUpdateAudit;
        if (audit is null)
        {
            audit = await _dbContext.UpdaterModpackUpdateAudits
                .FirstOrDefaultAsync(current => current.AgentCommandId == command.Id, cancellationToken);
        }

        if (audit is null)
        {
            var modpackId = ReadModpackId(command.PayloadJson);
            if (modpackId is null || !await _dbContext.UpdaterModpackProfiles.AnyAsync(profile => profile.Id == modpackId, cancellationToken))
            {
                return;
            }
            audit = new UpdaterModpackUpdateAudit
            {
                ModpackProfileId = modpackId.Value,
                AgentNodeId = command.AgentNodeId,
                AgentCommandId = command.Id,
                TriggerSource = "command:completion-fallback",
                RequestedVersion = ReadPayloadString(command.PayloadJson, "options", "requestedVersion"),
                PreviousVersion = ReadPayloadString(command.PayloadJson, "modpack", "currentVersion"),
                CreatedUtc = command.CreatedUtc == default ? utcNow : command.CreatedUtc
            };
            _dbContext.UpdaterModpackUpdateAudits.Add(audit);
            command.ModpackUpdateAudit = audit;
        }

        var profile = await _dbContext.UpdaterModpackProfiles
            .FirstOrDefaultAsync(current => current.Id == audit.ModpackProfileId, cancellationToken);

        var skipped = StandaloneSyncResultParser.IsSkipped(request.ResultPayloadJson);
        var resolvedVersion = StandaloneSyncResultParser.ReadResolvedVersionReference(request.ResultPayloadJson);
        var resolvedVersionDisplay = StandaloneSyncResultParser.ReadResolvedVersionDisplay(request.ResultPayloadJson);
        var finalStatus = request.Success
            ? skipped ? UpdaterModpackUpdateAuditStatus.Skipped : UpdaterModpackUpdateAuditStatus.Completed
            : UpdaterModpackUpdateAuditStatus.Failed;

        audit.Status = finalStatus;
        audit.Summary = TruncateOrNull(request.Summary, 500);
        audit.ResultPayloadJson = TruncateOrNull(request.ResultPayloadJson, 20000);
        audit.CompletedUtc = utcNow;
        audit.UpdatedUtc = utcNow;

        if (!string.IsNullOrWhiteSpace(resolvedVersion))
        {
            audit.TargetVersion ??= resolvedVersion;
            if (request.Success && !skipped)
            {
                audit.AppliedVersion = resolvedVersion;
            }
        }

        if (!string.IsNullOrWhiteSpace(resolvedVersionDisplay))
        {
            audit.TargetVersionDisplay ??= resolvedVersionDisplay;
            if (request.Success && !skipped)
            {
                audit.AppliedVersionDisplay = resolvedVersionDisplay;
            }
        }

        if (profile is null)
        {
            return;
        }

        // A profile can be reassigned while an old agent is still running. Its
        // audit belongs to the old command, but the newly assigned installation
        // must not inherit that agent's version or receive its announcement.
        if (profile.AgentNodeId != command.AgentNodeId)
        {
            audit.Summary = TruncateOrNull(
                $"{audit.Summary} Profile state was not updated because the assigned agent changed during this command.",
                500);
            return;
        }

        profile.LastRunUtc = utcNow;
        profile.LastSucceeded = request.Success;
        profile.LastSkipped = request.Success && skipped;
        profile.LastSummary = TruncateOrNull(request.Summary, 500);
        profile.LastResultPayloadJson = TruncateOrNull(request.ResultPayloadJson, 20000);
        profile.UpdatedUtc = utcNow;

        if (request.Success)
        {
            profile.LastSuccessUtc = utcNow;
            if (!skipped && !string.IsNullOrWhiteSpace(resolvedVersion))
            {
                profile.CurrentVersion = resolvedVersion;
                profile.CurrentVersionDisplay = resolvedVersionDisplay;
            }
        }

        if (request.Success && !skipped && !string.IsNullOrWhiteSpace(profile.DiscordAnnouncementChannelId))
        {
            await QueueDiscordAnnouncementAsync(profile, audit, resolvedVersion, resolvedVersionDisplay, utcNow, cancellationToken);
        }
    }

    private async Task QueueDiscordAnnouncementAsync(
        UpdaterModpackProfile profile,
        UpdaterModpackUpdateAudit audit,
        string? version,
        string? versionDisplay,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        var settings = await _dbContext.UpdaterDiscordSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);
        if (settings is null || !settings.Enabled)
        {
            return;
        }

        var message = ExpandDiscordMessageTemplate(
            settings.MessageTemplate,
            profile,
            versionDisplay ?? version ?? audit.TargetVersionDisplay ?? audit.TargetVersion ?? "unknown");

        _dbContext.UpdaterDiscordAnnouncements.Add(new UpdaterDiscordAnnouncement
        {
            ModpackProfileId = profile.Id,
            ModpackUpdateAudit = audit,
            ChannelId = profile.DiscordAnnouncementChannelId!.Trim(),
            RoleId = TruncateOrNull(profile.DiscordAnnouncementRoleId, 50),
            MessageContent = TruncateOrNull(message, 2000) ?? message,
            Status = UpdaterDiscordAnnouncementStatus.Pending,
            CreatedUtc = utcNow,
            UpdatedUtc = utcNow
        });
    }

    private static string ExpandDiscordMessageTemplate(
        string? template,
        UpdaterModpackProfile profile,
        string version)
    {
        var roleMention = string.IsNullOrWhiteSpace(profile.DiscordAnnouncementRoleId)
            ? string.Empty
            : $"<@&{profile.DiscordAnnouncementRoleId.Trim()}> ";
        var value = string.IsNullOrWhiteSpace(template)
            ? new UpdaterDiscordSettings().MessageTemplate
            : template;

        return value
            .Replace("{roleMention}", roleMention, StringComparison.OrdinalIgnoreCase)
            .Replace("{modpackName}", profile.Name, StringComparison.OrdinalIgnoreCase)
            .Replace("{version}", version, StringComparison.OrdinalIgnoreCase)
            .Replace("{currentVersion}", profile.CurrentVersion, StringComparison.OrdinalIgnoreCase);
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

    public static int? ReadModpackId(string payloadJson)
    {
        var value = ReadPayloadString(payloadJson, "modpack", "id");
        return int.TryParse(value, out var id) && id > 0 ? id : null;
    }

    private static string? ReadPayloadString(string payloadJson, string section, string property)
    {
        try
        {
            using var json = JsonDocument.Parse(payloadJson);
            if (json.RootElement.ValueKind == JsonValueKind.Object &&
                json.RootElement.TryGetProperty(section, out var element) && element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty(property, out var value))
            {
                return value.ValueKind == JsonValueKind.String ? value.GetString() :
                    value.ValueKind == JsonValueKind.Number ? value.GetRawText() : null;
            }
        }
        catch (JsonException) { }
        return null;
    }
}
