using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MCAgent.Models.AgentApi;
using MCAgent.Options;

namespace MCAgent.Services;

public sealed class AgentApiClient : IAgentApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string AgentTokenHeaderName = "X-Agent-Token";
    private const string AgentInstanceHeaderName = "X-Agent-Instance";

    private readonly HttpClient _httpClient;
    private readonly AgentOptions _options;
    private readonly AgentCommandCheckpointStore _checkpointStore;

    public AgentApiClient(
        IOptions<AgentOptions> options,
        AgentCommandCheckpointStore checkpointStore)
        : this(options, checkpointStore, new HttpClient())
    {
    }

    public AgentApiClient(
        IOptions<AgentOptions> options,
        AgentCommandCheckpointStore checkpointStore,
        HttpClient httpClient)
    {
        _options = options.Value;
        _checkpointStore = checkpointStore;
        _httpClient = httpClient;
        _httpClient.BaseAddress ??= new Uri(_options.ApiBaseUrl.TrimEnd('/') + "/");
        _httpClient.Timeout = TimeSpan.FromSeconds(_options.HttpTimeoutSeconds);
    }

    public async Task<AgentHeartbeatResponse> SendHeartbeatAsync(
        AgentHeartbeatRequest request,
        CancellationToken cancellationToken)
    {
        using var message = await CreateRequestAsync(HttpMethod.Post, "api/agent/heartbeat", cancellationToken);
        message.Content = JsonContent.Create(request, options: JsonOptions);

        using var response = await _httpClient.SendAsync(message, cancellationToken);
        return await ReadResponseAsync<AgentHeartbeatResponse>(response, cancellationToken);
    }

    public async Task<IReadOnlyList<AgentCommandPayload>> GetPendingCommandsAsync(
        int take,
        CancellationToken cancellationToken)
    {
        var boundedTake = Math.Clamp(take, 1, 100);
        using var message = await CreateRequestAsync(
            HttpMethod.Get,
            $"api/agent/commands/pending?take={boundedTake}",
            cancellationToken);

        using var response = await _httpClient.SendAsync(message, cancellationToken);
        return await ReadResponseAsync<List<AgentCommandPayload>>(response, cancellationToken);
    }

    public async Task<AgentCommandAckResponse> AcknowledgeCommandAsync(
        int commandId,
        CancellationToken cancellationToken)
    {
        using var message = await CreateRequestAsync(
            HttpMethod.Post,
            $"api/agent/commands/{commandId}/ack",
            cancellationToken);
        message.Content = JsonContent.Create(new { }, options: JsonOptions);

        using var response = await _httpClient.SendAsync(message, cancellationToken);
        return await ReadResponseAsync<AgentCommandAckResponse>(response, cancellationToken);
    }

    public async Task<AgentCommandAckResponse> CompleteCommandAsync(
        int commandId,
        AgentCommandCompletionRequest request,
        CancellationToken cancellationToken)
    {
        using var message = await CreateRequestAsync(
            HttpMethod.Post,
            $"api/agent/commands/{commandId}/complete",
            cancellationToken);
        message.Content = JsonContent.Create(request, options: JsonOptions);

        using var response = await _httpClient.SendAsync(message, cancellationToken);
        return await ReadResponseAsync<AgentCommandAckResponse>(response, cancellationToken);
    }

    public async Task<AgentAmpRuntimeConfigResponse> GetAmpRuntimeConfigAsync(
        int modpackId,
        CancellationToken cancellationToken)
    {
        using var message = await CreateRequestAsync(
            HttpMethod.Get,
            $"api/agent/modpacks/{modpackId}/amp-runtime",
            cancellationToken);

        using var response = await _httpClient.SendAsync(message, cancellationToken);
        return await ReadResponseAsync<AgentAmpRuntimeConfigResponse>(response, cancellationToken);
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(
        HttpMethod method,
        string uri,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, uri);
        try
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Add(AgentTokenHeaderName, _options.AuthToken);
            request.Headers.Add(
                AgentInstanceHeaderName,
                await _checkpointStore.GetExecutionOwnerIdAsync(cancellationToken));
            return request;
        }
        catch
        {
            request.Dispose();
            throw;
        }
    }

    private static async Task<T> ReadResponseAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
        where T : class
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new AgentApiException(
                response.StatusCode,
                $"{(int)response.StatusCode} {response.ReasonPhrase}");
        }

        var content = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
        return content ?? throw new AgentApiException(
            response.StatusCode,
            "API returned an empty response body.");
    }
}
