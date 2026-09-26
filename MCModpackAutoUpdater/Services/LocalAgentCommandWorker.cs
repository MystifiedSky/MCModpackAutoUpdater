using Microsoft.EntityFrameworkCore;
using MCAgent.Commands;
using MCAgent.Models.AgentApi;
using MCModpackAutoUpdater.Data;
using System.Text.Json;

namespace MCModpackAutoUpdater.Services;

public sealed class LocalAgentCommandWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<LocalAgentCommandWorker> _logger;
    private readonly IReadOnlyDictionary<string, IAgentCommandHandler> _handlers;
    private readonly Dictionary<int, AgentCommandCompletionRequest> _completedResults = new();

    public LocalAgentCommandWorker(
        IServiceProvider serviceProvider,
        ILogger<LocalAgentCommandWorker> logger,
        IEnumerable<IAgentCommandHandler> handlers)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _handlers = handlers
            .Where(static handler => !string.IsNullOrWhiteSpace(handler.CommandType))
            .ToDictionary(static handler => handler.CommandType, StringComparer.OrdinalIgnoreCase);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingLocalCommandsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Local command worker loop failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    internal async Task ProcessPendingLocalCommandsAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<UpdaterIdentityDbContext>();
        var commandService = scope.ServiceProvider.GetRequiredService<UpdaterCommandService>();

        var commands = await dbContext.UpdaterAgentCommands
            .AsNoTracking()
            .Include(command => command.AgentNode)
            .Where(command =>
                command.AgentNode != null &&
                command.AgentNode.Enabled &&
                command.AgentNode.ExecutionMode == UpdaterAgentExecutionMode.Local &&
                (command.Status == UpdaterAgentCommandStatus.Pending ||
                 command.Status == UpdaterAgentCommandStatus.InProgress))
            .OrderBy(command => command.CreatedUtc)
            .Take(10)
            .ToListAsync(cancellationToken);

        foreach (var command in commands)
        {
            await ProcessCommandAsync(command, commandService, dbContext, cancellationToken);
        }
    }

    private async Task ProcessCommandAsync(
        UpdaterAgentCommand command,
        UpdaterCommandService commandService,
        UpdaterIdentityDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (!_completedResults.TryGetValue(command.Id, out var completion) && command.LocalExecutionResultJson is not null)
        {
            try { completion = JsonSerializer.Deserialize<AgentCommandCompletionRequest>(command.LocalExecutionResultJson); }
            catch (JsonException) { }
        }
        if (completion is not null)
        {
            await commandService.CompleteCommandAsync(command.Id, command.AgentNodeId, completion, cancellationToken);
            _completedResults.Remove(command.Id);
            return;
        }
        if (command.Status == UpdaterAgentCommandStatus.InProgress)
        {
            // An interrupted update may have changed files or stopped a server. Never
            // repeat those side effects automatically without a recorded result.
            await commandService.CompleteCommandAsync(command.Id, command.AgentNodeId,
                new AgentCommandCompletionRequest { Success = false, Summary = "Local runner stopped during execution without a recorded result. Inspect the server before retrying." },
                cancellationToken);
            return;
        }
        try
        {
            var ack = await commandService.AcknowledgeCommandAsync(command.Id, command.AgentNodeId, cancellationToken);
            if (ack is null) return;
        }
        catch (InvalidOperationException)
        {
            return;
        }

        AgentCommandExecutionResult result;
        if (string.Equals(command.CommandType, "self_update", StringComparison.OrdinalIgnoreCase))
        {
            result = AgentCommandExecutionResult.Failed("Self-update is supported only by remote MCAgent installations. Publish and redeploy the web runner to update the local agent.");
        }
        else if (!_handlers.TryGetValue(command.CommandType, out var handler))
        {
            result = AgentCommandExecutionResult.Failed($"No local handler registered for command type '{command.CommandType}'.");
        }
        else
        {
            try
            {
                result = await handler.ExecuteAsync(
                    new AgentCommandPayload
                    {
                        Id = command.Id,
                        CommandType = command.CommandType,
                        PayloadJson = command.PayloadJson,
                        CreatedUtc = command.CreatedUtc
                    },
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogError(exception, "Local command handler threw for command #{CommandId}.", command.Id);
                result = AgentCommandExecutionResult.Failed(
                    $"Unhandled exception while executing {command.CommandType}: {exception.Message}");
            }
        }

        completion = new AgentCommandCompletionRequest
        {
            Success = result.Success,
            Summary = result.Summary,
            ResultPayloadJson = result.ResultPayloadJson
        };
        _completedResults[command.Id] = completion;
        // Save the outcome before completion bookkeeping. A failed database write is
        // retried from memory; a process restart can replay this durable result only.
        var resultJson = JsonSerializer.Serialize(completion);
        await dbContext.UpdaterAgentCommands.Where(item => item.Id == command.Id)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.LocalExecutionResultJson, resultJson), cancellationToken);
        await commandService.CompleteCommandAsync(command.Id, command.AgentNodeId, completion, cancellationToken);
        _completedResults.Remove(command.Id);
    }
}
