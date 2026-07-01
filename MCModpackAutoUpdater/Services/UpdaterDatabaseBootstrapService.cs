using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MCAgent.Options;
using MCModpackAutoUpdater.Data;
using MCModpackAutoUpdater.Options;

namespace MCModpackAutoUpdater.Services;

public sealed class UpdaterDatabaseBootstrapService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IOptionsMonitor<StandaloneUpdaterOptions> _options;
    private readonly IOptionsMonitor<AgentOptions> _agentOptions;

    public UpdaterDatabaseBootstrapService(
        IServiceProvider serviceProvider,
        IOptionsMonitor<StandaloneUpdaterOptions> options,
        IOptionsMonitor<AgentOptions> agentOptions)
    {
        _serviceProvider = serviceProvider;
        _options = options;
        _agentOptions = agentOptions;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<UpdaterIdentityDbContext>();
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);
        await EnsureUpdaterTablesAsync(dbContext, cancellationToken);

        await EnsureLocalAgentAsync(dbContext, cancellationToken);
        await EnsureRuntimeSettingsAsync(dbContext, cancellationToken);
        await EnsureAmpControllerSettingsAsync(dbContext, cancellationToken);
        await EnsureDirectAmpApiSettingsAsync(dbContext, cancellationToken);
        await EnsureDiscordSettingsAsync(dbContext, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private static async Task EnsureUpdaterTablesAsync(
        UpdaterIdentityDbContext dbContext,
        CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "UpdaterAmpControllerSettings" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_UpdaterAmpControllerSettings" PRIMARY KEY AUTOINCREMENT,
                "Enabled" INTEGER NOT NULL,
                "ControllerApiUrl" TEXT NOT NULL,
                "Username" TEXT NOT NULL,
                "Password" TEXT NOT NULL,
                "Token" TEXT NOT NULL,
                "RememberMe" INTEGER NOT NULL,
                "CreatedUtc" TEXT NOT NULL,
                "UpdatedUtc" TEXT NOT NULL
            );
            """,
            cancellationToken);

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "UpdaterDirectAmpApiSettings" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_UpdaterDirectAmpApiSettings" PRIMARY KEY AUTOINCREMENT,
                "Enabled" INTEGER NOT NULL,
                "Username" TEXT NOT NULL,
                "Password" TEXT NOT NULL,
                "Token" TEXT NOT NULL,
                "RememberMe" INTEGER NOT NULL,
                "WarningMessageTemplate" TEXT NOT NULL,
                "CreatedUtc" TEXT NOT NULL,
                "UpdatedUtc" TEXT NOT NULL
            );
            """,
            cancellationToken);

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "UpdaterRuntimeSettings" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_UpdaterRuntimeSettings" PRIMARY KEY AUTOINCREMENT,
                "RunOnStartup" INTEGER NOT NULL,
                "ExitAfterStartupRun" INTEGER NOT NULL,
                "LoopDelaySeconds" INTEGER NOT NULL,
                "ScheduleTimeZone" TEXT NOT NULL,
                "CreatedUtc" TEXT NOT NULL,
                "UpdatedUtc" TEXT NOT NULL
            );
            """,
            cancellationToken);

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "UpdaterDiscordSettings" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_UpdaterDiscordSettings" PRIMARY KEY AUTOINCREMENT,
                "Enabled" INTEGER NOT NULL,
                "BotToken" TEXT NOT NULL,
                "MessageTemplate" TEXT NOT NULL,
                "CreatedUtc" TEXT NOT NULL,
                "UpdatedUtc" TEXT NOT NULL
            );
            """,
            cancellationToken);

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "UpdaterAgentNodes" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_UpdaterAgentNodes" PRIMARY KEY AUTOINCREMENT,
                "Name" TEXT NOT NULL,
                "Host" TEXT NOT NULL,
                "ApiBaseUrl" TEXT NOT NULL,
                "Platform" TEXT NOT NULL,
                "ExecutionMode" TEXT NOT NULL,
                "Enabled" INTEGER NOT NULL,
                "AuthTokenHash" TEXT NOT NULL,
                "AuthTokenLastRotatedUtc" TEXT NOT NULL,
                "LastSeenUtc" TEXT NULL,
                "LastReportedStatus" TEXT NULL,
                "LastReportedVersion" TEXT NULL,
                "CreatedUtc" TEXT NOT NULL,
                "UpdatedUtc" TEXT NOT NULL
            );
            """,
            cancellationToken);

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "UpdaterModpackProfiles" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_UpdaterModpackProfiles" PRIMARY KEY AUTOINCREMENT,
                "AgentNodeId" INTEGER NULL,
                "Enabled" INTEGER NOT NULL,
                "RunOnStartup" INTEGER NULL,
                "Name" TEXT NOT NULL,
                "Provider" TEXT NOT NULL,
                "SourceReference" TEXT NOT NULL,
                "ServerPackUrl" TEXT NULL,
                "BuildServerPackFromClientFiles" INTEGER NOT NULL,
                "ServerPackExcludedPaths" TEXT NULL,
                "ServerPackExcludedCurseForgeProjectIds" TEXT NULL,
                "VersionLock" TEXT NULL,
                "CurrentVersion" TEXT NOT NULL,
                "CurrentVersionDisplay" TEXT NULL,
                "InstallRootPath" TEXT NOT NULL,
                "OverrideDirectory" TEXT NULL,
                "PreservedPaths" TEXT NULL,
                "ScheduleTime" TEXT NULL,
                "RestartMode" TEXT NOT NULL,
                "WarningMinutes" INTEGER NOT NULL,
                "AmpInstanceName" TEXT NULL,
                "AmpApiUrl" TEXT NULL,
                "AmpConfigValuesJson" TEXT NULL,
                "DiscordAnnouncementChannelId" TEXT NULL,
                "DiscordAnnouncementRoleId" TEXT NULL,
                "RequestedVersion" TEXT NULL,
                "ForceFullSync" INTEGER NOT NULL,
                "SkipWarnings" INTEGER NOT NULL,
                "IgnoreCurrentVersion" INTEGER NOT NULL,
                "LastScheduledCheckDate" TEXT NULL,
                "LastScheduledCheckUtc" TEXT NULL,
                "LastDryRunCheckUtc" TEXT NULL,
                "LastDryRunCheckSummary" TEXT NULL,
                "LastDryRunCheckTargetVersion" TEXT NULL,
                "LastDryRunCheckTargetVersionDisplay" TEXT NULL,
                "LastQueuedUtc" TEXT NULL,
                "LastRunUtc" TEXT NULL,
                "LastSuccessUtc" TEXT NULL,
                "LastSucceeded" INTEGER NOT NULL DEFAULT 0,
                "LastSkipped" INTEGER NOT NULL DEFAULT 0,
                "LastSummary" TEXT NULL,
                "LastResultPayloadJson" TEXT NULL,
                "CreatedUtc" TEXT NOT NULL,
                "UpdatedUtc" TEXT NOT NULL,
                CONSTRAINT "FK_UpdaterModpackProfiles_UpdaterAgentNodes_AgentNodeId" FOREIGN KEY ("AgentNodeId") REFERENCES "UpdaterAgentNodes" ("Id") ON DELETE SET NULL
            );
            """,
            cancellationToken);

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "UpdaterAgentCommands" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_UpdaterAgentCommands" PRIMARY KEY AUTOINCREMENT,
                "AgentNodeId" INTEGER NOT NULL,
                "CommandType" TEXT NOT NULL,
                "PayloadJson" TEXT NOT NULL,
                "Status" TEXT NOT NULL,
                "CreatedUtc" TEXT NOT NULL,
                "UpdatedUtc" TEXT NOT NULL,
                "AcknowledgedUtc" TEXT NULL,
                "CompletedUtc" TEXT NULL,
                "ResultSummary" TEXT NULL,
                "ResultPayloadJson" TEXT NULL,
                CONSTRAINT "FK_UpdaterAgentCommands_UpdaterAgentNodes_AgentNodeId" FOREIGN KEY ("AgentNodeId") REFERENCES "UpdaterAgentNodes" ("Id") ON DELETE CASCADE
            );
            """,
            cancellationToken);

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "UpdaterModpackUpdateAudits" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_UpdaterModpackUpdateAudits" PRIMARY KEY AUTOINCREMENT,
                "ModpackProfileId" INTEGER NOT NULL,
                "AgentNodeId" INTEGER NULL,
                "AgentCommandId" INTEGER NULL,
                "TriggerSource" TEXT NOT NULL,
                "RequestedVersion" TEXT NULL,
                "PreviousVersion" TEXT NULL,
                "TargetVersion" TEXT NULL,
                "TargetVersionDisplay" TEXT NULL,
                "AppliedVersion" TEXT NULL,
                "AppliedVersionDisplay" TEXT NULL,
                "Status" TEXT NOT NULL,
                "Summary" TEXT NULL,
                "ResultPayloadJson" TEXT NULL,
                "CreatedUtc" TEXT NOT NULL,
                "UpdatedUtc" TEXT NOT NULL,
                "CompletedUtc" TEXT NULL,
                CONSTRAINT "FK_UpdaterModpackUpdateAudits_UpdaterAgentCommands_AgentCommandId" FOREIGN KEY ("AgentCommandId") REFERENCES "UpdaterAgentCommands" ("Id") ON DELETE SET NULL,
                CONSTRAINT "FK_UpdaterModpackUpdateAudits_UpdaterAgentNodes_AgentNodeId" FOREIGN KEY ("AgentNodeId") REFERENCES "UpdaterAgentNodes" ("Id") ON DELETE SET NULL,
                CONSTRAINT "FK_UpdaterModpackUpdateAudits_UpdaterModpackProfiles_ModpackProfileId" FOREIGN KEY ("ModpackProfileId") REFERENCES "UpdaterModpackProfiles" ("Id") ON DELETE CASCADE
            );
            """,
            cancellationToken);

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "UpdaterDiscordAnnouncements" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_UpdaterDiscordAnnouncements" PRIMARY KEY AUTOINCREMENT,
                "ModpackProfileId" INTEGER NOT NULL,
                "ModpackUpdateAuditId" INTEGER NULL,
                "ChannelId" TEXT NOT NULL,
                "RoleId" TEXT NULL,
                "MessageContent" TEXT NOT NULL,
                "Status" TEXT NOT NULL,
                "RetryCount" INTEGER NOT NULL,
                "FailureReason" TEXT NULL,
                "DiscordMessageId" TEXT NULL,
                "CreatedUtc" TEXT NOT NULL,
                "UpdatedUtc" TEXT NOT NULL,
                "SentUtc" TEXT NULL,
                CONSTRAINT "FK_UpdaterDiscordAnnouncements_UpdaterModpackProfiles_ModpackProfileId" FOREIGN KEY ("ModpackProfileId") REFERENCES "UpdaterModpackProfiles" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_UpdaterDiscordAnnouncements_UpdaterModpackUpdateAudits_ModpackUpdateAuditId" FOREIGN KEY ("ModpackUpdateAuditId") REFERENCES "UpdaterModpackUpdateAudits" ("Id") ON DELETE SET NULL
            );
            """,
            cancellationToken);

        var indexStatements = new[]
        {
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_UpdaterAgentNodes_Name" ON "UpdaterAgentNodes" ("Name");""",
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_UpdaterAgentNodes_AuthTokenHash" ON "UpdaterAgentNodes" ("AuthTokenHash");""",
            """CREATE INDEX IF NOT EXISTS "IX_UpdaterModpackProfiles_AgentNodeId" ON "UpdaterModpackProfiles" ("AgentNodeId");""",
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_UpdaterModpackProfiles_Name" ON "UpdaterModpackProfiles" ("Name");""",
            """CREATE INDEX IF NOT EXISTS "IX_UpdaterAgentCommands_AgentNodeId_Status" ON "UpdaterAgentCommands" ("AgentNodeId", "Status");""",
            """CREATE INDEX IF NOT EXISTS "IX_UpdaterAgentCommands_CreatedUtc" ON "UpdaterAgentCommands" ("CreatedUtc");""",
            """CREATE INDEX IF NOT EXISTS "IX_UpdaterModpackUpdateAudits_ModpackProfileId_CreatedUtc" ON "UpdaterModpackUpdateAudits" ("ModpackProfileId", "CreatedUtc");""",
            """CREATE INDEX IF NOT EXISTS "IX_UpdaterModpackUpdateAudits_Status_CreatedUtc" ON "UpdaterModpackUpdateAudits" ("Status", "CreatedUtc");""",
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_UpdaterModpackUpdateAudits_AgentCommandId" ON "UpdaterModpackUpdateAudits" ("AgentCommandId") WHERE "AgentCommandId" IS NOT NULL;""",
            """CREATE INDEX IF NOT EXISTS "IX_UpdaterDiscordAnnouncements_Status_CreatedUtc" ON "UpdaterDiscordAnnouncements" ("Status", "CreatedUtc");"""
        };

        foreach (var statement in indexStatements)
        {
            await dbContext.Database.ExecuteSqlRawAsync(statement, cancellationToken);
        }

        await EnsureColumnAsync(dbContext, "UpdaterModpackProfiles", "LastDryRunCheckUtc", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(dbContext, "UpdaterModpackProfiles", "LastDryRunCheckSummary", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(dbContext, "UpdaterModpackProfiles", "LastDryRunCheckTargetVersion", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(dbContext, "UpdaterModpackProfiles", "LastDryRunCheckTargetVersionDisplay", "TEXT NULL", cancellationToken);
    }

    private static async Task EnsureColumnAsync(
        UpdaterIdentityDbContext dbContext,
        string tableName,
        string columnName,
        string columnDefinition,
        CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        var shouldClose = connection.State == System.Data.ConnectionState.Closed;
        if (shouldClose)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info(\"{tableName}\");";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync();
            }
        }

        var alterCommandText = $"""ALTER TABLE "{tableName}" ADD COLUMN "{columnName}" {columnDefinition};""";
        await using var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = alterCommandText;
        if (connection.State == System.Data.ConnectionState.Closed)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await alterCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<UpdaterAgentNode> EnsureLocalAgentAsync(
        UpdaterIdentityDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var localAgent = await dbContext.UpdaterAgentNodes
            .FirstOrDefaultAsync(agent => agent.ExecutionMode == UpdaterAgentExecutionMode.Local, cancellationToken);
        if (localAgent is not null)
        {
            return localAgent;
        }

        var utcNow = DateTime.UtcNow;
        var token = UpdaterAgentTokenUtility.GenerateToken();
        localAgent = new UpdaterAgentNode
        {
            Name = "Local Runner",
            Host = Environment.MachineName,
            ApiBaseUrl = "local://embedded",
            Platform = OperatingSystem.IsWindows() ? "Windows" : "Linux",
            ExecutionMode = UpdaterAgentExecutionMode.Local,
            Enabled = true,
            AuthTokenHash = UpdaterAgentTokenUtility.HashToken(token),
            AuthTokenLastRotatedUtc = utcNow,
            CreatedUtc = utcNow,
            UpdatedUtc = utcNow
        };

        dbContext.UpdaterAgentNodes.Add(localAgent);
        await dbContext.SaveChangesAsync(cancellationToken);
        return localAgent;
    }

    private async Task EnsureRuntimeSettingsAsync(
        UpdaterIdentityDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (await dbContext.UpdaterRuntimeSettings.AnyAsync(cancellationToken))
        {
            return;
        }

        var utcNow = DateTime.UtcNow;
        var configured = _options.CurrentValue;
        dbContext.UpdaterRuntimeSettings.Add(new UpdaterRuntimeSettings
        {
            RunOnStartup = configured.RunOnStartup,
            ExitAfterStartupRun = configured.ExitAfterStartupRun,
            LoopDelaySeconds = Math.Clamp(configured.LoopDelaySeconds, 5, 3600),
            ScheduleTimeZone = string.IsNullOrWhiteSpace(configured.ScheduleTimeZone)
                ? "America/New_York"
                : configured.ScheduleTimeZone.Trim(),
            CreatedUtc = utcNow,
            UpdatedUtc = utcNow
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureAmpControllerSettingsAsync(
        UpdaterIdentityDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (await dbContext.UpdaterAmpControllerSettings.AnyAsync(cancellationToken))
        {
            return;
        }

        var utcNow = DateTime.UtcNow;
        var configured = _options.CurrentValue.AmpController;
        dbContext.UpdaterAmpControllerSettings.Add(new UpdaterAmpControllerSettings
        {
            Enabled = configured.Enabled,
            ControllerApiUrl = configured.ControllerApiUrl.Trim(),
            Username = configured.Username.Trim(),
            Password = configured.Password,
            Token = configured.Token,
            RememberMe = configured.RememberMe,
            CreatedUtc = utcNow,
            UpdatedUtc = utcNow
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureDirectAmpApiSettingsAsync(
        UpdaterIdentityDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (await dbContext.UpdaterDirectAmpApiSettings.AnyAsync(cancellationToken))
        {
            return;
        }

        var utcNow = DateTime.UtcNow;
        var configured = _agentOptions.CurrentValue.ModpackSync.AmpApi;
        dbContext.UpdaterDirectAmpApiSettings.Add(new UpdaterDirectAmpApiSettings
        {
            Enabled = configured.Enabled,
            Username = configured.Username.Trim(),
            Password = configured.Password,
            Token = configured.Token,
            RememberMe = configured.RememberMe,
            WarningMessageTemplate = string.IsNullOrWhiteSpace(configured.WarningMessageTemplate)
                ? new AmpApiOptions().WarningMessageTemplate
                : configured.WarningMessageTemplate.Trim(),
            CreatedUtc = utcNow,
            UpdatedUtc = utcNow
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static async Task EnsureDiscordSettingsAsync(
        UpdaterIdentityDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (await dbContext.UpdaterDiscordSettings.AnyAsync(cancellationToken))
        {
            return;
        }

        var utcNow = DateTime.UtcNow;
        dbContext.UpdaterDiscordSettings.Add(new UpdaterDiscordSettings
        {
            Enabled = false,
            CreatedUtc = utcNow,
            UpdatedUtc = utcNow
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

}
