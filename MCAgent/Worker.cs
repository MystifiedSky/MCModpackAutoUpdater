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
    private readonly AgentOptions _options;
    private readonly IReadOnlyDictionary<string, IAgentCommandHandler> _commandHandlers;

    public Worker(
        ILogger<Worker> logger,
        IAgentApiClient agentApiClient,
        AgentRuntimeState runtimeState,
        IOptions<AgentOptions> options,
        IEnumerable<IAgentCommandHandler> commandHandlers)
    {
        _logger = logger;
        _agentApiClient = agentApiClient;
        _runtimeState = runtimeState;
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
        _runtimeState.SetStatus($"Running {command.CommandType}#{command.Id}");
        _logger.LogInformation(
            "Processing command #{CommandId} type={CommandType}, created={CreatedUtc}.",
            command.Id,
            command.CommandType,
            command.CreatedUtc);

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
            _runtimeState.SetStatus("Idle");
            return;
        }

        AgentCommandExecutionResult result;
        if (!_commandHandlers.TryGetValue(command.CommandType, out var commandHandler))
        {
            result = AgentCommandExecutionResult.Failed(
                $"No handler registered for command type '{command.CommandType}'.");
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
                    command.CommandType);

                var errorPayload = JsonSerializer.Serialize(new
                {
                    exception = exception.GetType().FullName,
                    exception.Message
                });

                result = AgentCommandExecutionResult.Failed(
                    $"Unhandled exception while executing {command.CommandType}.",
                    errorPayload);
            }
        }

        try
        {
            await _agentApiClient.CompleteCommandAsync(
                command.Id,
                new AgentCommandCompletionRequest
                {
                    Success = result.Success,
                    Summary = result.Summary,
                    ResultPayloadJson = result.ResultPayloadJson
                },
                cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to submit completion for command #{CommandId}.", command.Id);
        }

        _runtimeState.SetStatus("Idle");
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
