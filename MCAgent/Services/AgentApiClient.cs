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

    private readonly HttpClient _httpClient;
    private readonly AgentOptions _options;

    public AgentApiClient(IOptions<AgentOptions> options)
    {
        _options = options.Value;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(_options.ApiBaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(_options.HttpTimeoutSeconds)
        };
    }

    public async Task<AgentHeartbeatResponse> SendHeartbeatAsync(
        AgentHeartbeatRequest request,
        CancellationToken cancellationToken)
    {
        using var message = CreateRequest(HttpMethod.Post, "api/agent/heartbeat");
        message.Content = JsonContent.Create(request, options: JsonOptions);

        using var response = await _httpClient.SendAsync(message, cancellationToken);
        return await ReadResponseAsync<AgentHeartbeatResponse>(response, cancellationToken);
    }

    public async Task<IReadOnlyList<AgentCommandPayload>> GetPendingCommandsAsync(
        int take,
        CancellationToken cancellationToken)
    {
        var boundedTake = Math.Clamp(take, 1, 100);
        using var message = CreateRequest(HttpMethod.Get, $"api/agent/commands/pending?take={boundedTake}");

        using var response = await _httpClient.SendAsync(message, cancellationToken);
        return await ReadResponseAsync<List<AgentCommandPayload>>(response, cancellationToken);
    }

    public async Task<AgentCommandAckResponse> AcknowledgeCommandAsync(
        int commandId,
        CancellationToken cancellationToken)
    {
        using var message = CreateRequest(HttpMethod.Post, $"api/agent/commands/{commandId}/ack");
        message.Content = JsonContent.Create(new { }, options: JsonOptions);

        using var response = await _httpClient.SendAsync(message, cancellationToken);
        return await ReadResponseAsync<AgentCommandAckResponse>(response, cancellationToken);
    }

    public async Task<AgentCommandAckResponse> CompleteCommandAsync(
        int commandId,
        AgentCommandCompletionRequest request,
        CancellationToken cancellationToken)
    {
        using var message = CreateRequest(HttpMethod.Post, $"api/agent/commands/{commandId}/complete");
        message.Content = JsonContent.Create(request, options: JsonOptions);

        using var response = await _httpClient.SendAsync(message, cancellationToken);
        return await ReadResponseAsync<AgentCommandAckResponse>(response, cancellationToken);
    }

    public async Task<AgentAmpRuntimeConfigResponse> GetAmpRuntimeConfigAsync(
        int modpackId,
        CancellationToken cancellationToken)
    {
        using var message = CreateRequest(HttpMethod.Get, $"api/agent/modpacks/{modpackId}/amp-runtime");

        using var response = await _httpClient.SendAsync(message, cancellationToken);
        return await ReadResponseAsync<AgentAmpRuntimeConfigResponse>(response, cancellationToken);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string uri)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add(AgentTokenHeaderName, _options.AuthToken);
        return request;
    }

    private static async Task<T> ReadResponseAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
        where T : class
    {
        if (!response.IsSuccessStatusCode)
        {
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            var message = string.IsNullOrWhiteSpace(responseText)
                ? $"{(int)response.StatusCode} {response.ReasonPhrase}"
                : $"{(int)response.StatusCode} {response.ReasonPhrase}: {responseText}";

            throw new AgentApiException(response.StatusCode, message);
        }

        var content = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
        return content ?? throw new AgentApiException(
            response.StatusCode,
            "API returned an empty response body.");
    }
}
