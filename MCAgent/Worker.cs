using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MCAgent.Commands;
using MCAgent.Models.AgentApi;
using MCAgent.Options;
using MCAgent.Services;

namespace MCAgent;

public sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IAgentApiClient _agentApiClient;
    private readonly AgentRuntimeState _runtimeState;
    private readonly AgentCommandCheckpointStore _checkpointStore;
    private readonly AgentOptions _options;
    private readonly IReadOnlyDictionary<string, IAgentCommandHandler> _commandHandlers;

    public Worker(
        ILogger<Worker> logger,
        IAgentApiClient agentApiClient,
        AgentRuntimeState runtimeState,
        AgentCommandCheckpointStore checkpointStore,
        IOptions<AgentOptions> options,
        IEnumerable<IAgentCommandHandler> commandHandlers)
    {
        _logger = logger;
        _agentApiClient = agentApiClient;
        _runtimeState = runtimeState;
        _checkpointStore = checkpointStore;
        _options = options.Value;
        _commandHandlers = BuildHandlerMap(commandHandlers);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _runtimeState.SetStatus("Starting");
        _logger.LogInformation(
            "MCAgent starting. Version={Version}, ApiBaseUrl={ApiBaseUrl}, PollInterval={PollInterval}s, BatchSize={BatchSize}",
            _options.AgentVersion,
            _options.ApiBaseUrl,
            _options.PollIntervalSeconds,
            _options.CommandBatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            var nextDelay = TimeSpan.FromSeconds(_options.PollIntervalSeconds);

            try
            {
                nextDelay = await SendHeartbeatAsync(stoppingToken);
                var pendingCommands = await _agentApiClient.GetPendingCommandsAsync(_options.CommandBatchSize, stoppingToken);

                if (pendingCommands.Count == 0)
                {
                    _runtimeState.SetStatus("Idle");
                }

                foreach (var command in pendingCommands)
                {
                    if (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }

                    await ProcessCommandAsync(command, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _runtimeState.SetStatus("Error");
                _logger.LogError(exception, "Agent polling loop failed.");
                nextDelay = TimeSpan.FromSeconds(_options.ErrorBackoffSeconds);
            }

            if (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(nextDelay, stoppingToken);
            }
        }

        _runtimeState.SetStatus("Stopping");
        _logger.LogInformation("MCAgent stopping.");
    }

    private async Task<TimeSpan> SendHeartbeatAsync(CancellationToken cancellationToken)
    {
        var heartbeatResponse = await _agentApiClient.SendHeartbeatAsync(
            new AgentHeartbeatRequest
            {
                AgentVersion = _options.AgentVersion,
                Status = _runtimeState.CurrentStatus
            },
            cancellationToken);

        var boundedPoll = Math.Clamp(heartbeatResponse.NextPollSeconds, 5, 300);
        if (boundedPoll != _options.PollIntervalSeconds)
        {
            _logger.LogDebug(
                "Runner requested next poll interval {PollSeconds}s (default {DefaultSeconds}s).",
                boundedPoll,
                _options.PollIntervalSeconds);
        }

        return TimeSpan.FromSeconds(boundedPoll);
    }

    private async Task ProcessCommandAsync(AgentCommandPayload command, CancellationToken cancellationToken)
    {
        var commandType = command.CommandType?.Trim() ?? string.Empty;
        var displayCommandType = string.IsNullOrWhiteSpace(commandType) ? "(missing type)" : commandType;
        _runtimeState.SetStatus($"Running {displayCommandType}#{command.Id}");
        _logger.LogInformation(
            "Processing command #{CommandId} type={CommandType}, created={CreatedUtc}.",
            command.Id,
            displayCommandType,
            command.CreatedUtc);

        if (command.Id <= 0)
        {
            _logger.LogError("Skipping runner command with invalid ID {CommandId}.", command.Id);
            _runtimeState.SetStatus("Idle");
            return;
        }

        var checkpoint = await _checkpointStore.GetAsync(command);
        if (checkpoint?.State == AgentCommandCheckpointStore.ResultPendingState)
        {
            await SubmitCheckpointResultAsync(command.Id, checkpoint, cancellationToken);
            _runtimeState.SetStatus("Idle");
            return;
        }

        if (checkpoint?.State == AgentCommandCheckpointStore.ExecutingState)
        {
            await CompleteInterruptedCommandAsync(
                command,
                displayCommandType,
                "The agent restarted while this command was in progress. Its side effects are unknown, so the agent did not run it again. Inspect the target server and logs before retrying this command.",
                cancellationToken);
            _runtimeState.SetStatus("Idle");
            return;
        }

        var remoteStatus = command.Status?.Trim() ?? string.Empty;
        if (string.Equals(remoteStatus, "InProgress", StringComparison.OrdinalIgnoreCase))
        {
            var reason = checkpoint?.State == AgentCommandCheckpointStore.ReadyState
                ? "The runner reports this command in progress, but the agent stopped before recording that its handler had started. Its execution state is ambiguous, so the agent did not run it again. Inspect the target server and logs before retrying this command."
                : "The runner reports this command in progress, but this agent has no matching local checkpoint. The prior execution state is unknown, so the agent did not run it again. Inspect the target server and logs before retrying this command.";
            await CompleteInterruptedCommandAsync(command, displayCommandType, reason, cancellationToken);
            _runtimeState.SetStatus("Idle");
            return;
        }

        if (!string.IsNullOrWhiteSpace(remoteStatus) &&
            !string.Equals(remoteStatus, "Pending", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Skipping command #{CommandId} because runner returned unsupported status '{CommandStatus}'.",
                command.Id,
                remoteStatus);
            _runtimeState.SetStatus("Idle");
            return;
        }

        var missingRemoteStatus = string.IsNullOrWhiteSpace(remoteStatus);
        await _checkpointStore.MarkReadyAsync(command);

        try
        {
            await _agentApiClient.AcknowledgeCommandAsync(command.Id, cancellationToken);
        }
        catch (AgentApiException exception) when (
            exception.StatusCode == HttpStatusCode.Conflict ||
            exception.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogWarning(
                "Skipping command #{CommandId}; ack returned {StatusCode}. Message: {Message}",
                command.Id,
                (int)exception.StatusCode,
                exception.Message);
            await _checkpointStore.RemoveAsync(command.Id);
            _runtimeState.SetStatus("Idle");
            return;
        }

        await _checkpointStore.MarkExecutingAsync(command);

        AgentCommandExecutionResult result;
        if (missingRemoteStatus)
        {
            result = AgentCommandExecutionResult.Failed(
                "The runner did not include the command status, so this agent acknowledged the command but did not execute it. Update the runner and inspect the target before retrying.");
        }
        else if (string.IsNullOrWhiteSpace(commandType))
        {
            result = AgentCommandExecutionResult.Failed("Runner command is missing a command type.");
        }
        else if (!_commandHandlers.TryGetValue(commandType, out var commandHandler))
        {
            result = AgentCommandExecutionResult.Failed(
                $"No handler registered for command type '{commandType}'.");
        }
        else
        {
            try
            {
                result = await commandHandler.ExecuteAsync(command, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Command handler threw for command #{CommandId} type={CommandType}.",
                    command.Id,
                    commandType);

                var errorPayload = JsonSerializer.Serialize(new
                {
                    exception = exception.GetType().FullName,
                    exception.Message
                });

                result = AgentCommandExecutionResult.Failed(
                    $"Unhandled exception while executing {commandType}.",
                    errorPayload);
            }
        }

        var completionRequest = new AgentCommandCompletionRequest
        {
            Success = result.Success,
            Summary = result.Summary,
            ResultPayloadJson = result.ResultPayloadJson
        };
        await _checkpointStore.SaveResultAsync(command, completionRequest);
        var savedResult = await _checkpointStore.GetAsync(command);
        if (savedResult is not null)
        {
            await SubmitCheckpointResultAsync(command.Id, savedResult, cancellationToken);
        }

        _runtimeState.SetStatus("Idle");
    }

    private async Task CompleteInterruptedCommandAsync(
        AgentCommandPayload command,
        string commandType,
        string reason,
        CancellationToken cancellationToken)
    {
        var result = AgentCommandExecutionResult.Failed(
            reason,
            JsonSerializer.Serialize(new
            {
                commandId = command.Id,
                commandType,
                interrupted = true,
                handlerWasReplayed = false
            }));
        var completionRequest = new AgentCommandCompletionRequest
        {
            Success = result.Success,
            Summary = result.Summary,
            ResultPayloadJson = result.ResultPayloadJson
        };

        await _checkpointStore.SaveResultAsync(command, completionRequest);
        var checkpoint = await _checkpointStore.GetAsync(command);
        if (checkpoint is not null)
        {
            await SubmitCheckpointResultAsync(command.Id, checkpoint, cancellationToken);
        }
    }

    private async Task SubmitCheckpointResultAsync(
        int commandId,
        AgentCommandCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        var completionRequest = checkpoint.ToCompletionRequest();
        if (completionRequest is null)
        {
            _logger.LogError(
                "Command #{CommandId} has a result-pending checkpoint without a final result; it will not be re-executed.",
                commandId);
            return;
        }

        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await _agentApiClient.CompleteCommandAsync(commandId, completionRequest, cancellationToken);
                await _checkpointStore.RemoveAsync(commandId);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsTransientCompletionFailure(exception))
            {
                if (attempt == maxAttempts)
                {
                    _logger.LogError(
                        exception,
                        "Could not submit completion for command #{CommandId} after {AttemptCount} attempts. Its final result is checkpointed and will be retried without re-running the handler.",
                        commandId,
                        attempt);
                    return;
                }

                _logger.LogWarning(
                    exception,
                    "Transient completion failure for command #{CommandId}; retrying attempt {AttemptNumber} of {AttemptCount}.",
                    commandId,
                    attempt + 1,
                    maxAttempts);
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Could not submit completion for command #{CommandId}. Its final result is checkpointed and will be retried without re-running the handler.",
                    commandId);
                return;
            }
        }
    }

    private static bool IsTransientCompletionFailure(Exception exception)
    {
        if (exception is AgentApiException apiException)
        {
            return apiException.StatusCode is HttpStatusCode.RequestTimeout or
                HttpStatusCode.TooManyRequests or
                HttpStatusCode.InternalServerError or
                HttpStatusCode.BadGateway or
                HttpStatusCode.ServiceUnavailable or
                HttpStatusCode.GatewayTimeout;
        }

        return exception is HttpRequestException or TimeoutException;
    }

    private static IReadOnlyDictionary<string, IAgentCommandHandler> BuildHandlerMap(
        IEnumerable<IAgentCommandHandler> handlers)
    {
        var map = new Dictionary<string, IAgentCommandHandler>(StringComparer.OrdinalIgnoreCase);

        foreach (var handler in handlers)
        {
            if (string.IsNullOrWhiteSpace(handler.CommandType))
            {
                continue;
            }

            map[handler.CommandType.Trim()] = handler;
        }

        return map;
    }
}
