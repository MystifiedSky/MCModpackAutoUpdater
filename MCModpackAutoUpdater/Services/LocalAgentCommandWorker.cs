using Microsoft.EntityFrameworkCore;
using MCAgent.Commands;
using MCAgent.Models.AgentApi;
using MCModpackAutoUpdater.Data;

namespace MCModpackAutoUpdater.Services;

public sealed class LocalAgentCommandWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<LocalAgentCommandWorker> _logger;
    private readonly IReadOnlyDictionary<string, IAgentCommandHandler> _handlers;

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

    private async Task ProcessPendingLocalCommandsAsync(CancellationToken cancellationToken)
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
            await ProcessCommandAsync(command, commandService, cancellationToken);
        }
    }

    private async Task ProcessCommandAsync(
        UpdaterAgentCommand command,
        UpdaterCommandService commandService,
        CancellationToken cancellationToken)
    {
        try
        {
            await commandService.AcknowledgeCommandAsync(command.Id, command.AgentNodeId, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        AgentCommandExecutionResult result;
        if (!_handlers.TryGetValue(command.CommandType, out var handler))
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

        await commandService.CompleteCommandAsync(
            command.Id,
            command.AgentNodeId,
            new AgentCommandCompletionRequest
            {
                Success = result.Success,
                Summary = result.Summary,
                ResultPayloadJson = result.ResultPayloadJson
            },
            cancellationToken);
    }
}
