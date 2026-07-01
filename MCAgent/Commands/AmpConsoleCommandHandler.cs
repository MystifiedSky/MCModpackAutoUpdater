using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MCAgent.Models.AgentApi;
using MCAgent.Services;

namespace MCAgent.Commands;

public sealed class AmpConsoleCommandHandler : IAgentCommandHandler
{
    private const int MaxConsoleCommandLength = 500;

    private readonly ILogger<AmpConsoleCommandHandler> _logger;
    private readonly IAgentApiClient _agentApiClient;
    private readonly IHttpClientFactory _httpClientFactory;

    public AmpConsoleCommandHandler(
        ILogger<AmpConsoleCommandHandler> logger,
        IAgentApiClient agentApiClient,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _agentApiClient = agentApiClient;
        _httpClientFactory = httpClientFactory;
    }

    public string CommandType => "amp_console";

    public async Task<AgentCommandExecutionResult> ExecuteAsync(
        AgentCommandPayload command,
        CancellationToken cancellationToken)
    {
        if (!TryParsePayload(command.PayloadJson, out var payload, out var parseError))
        {
            return AgentCommandExecutionResult.Failed(parseError);
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var runtimeConfig = await _agentApiClient.GetAmpRuntimeConfigAsync(payload.ModpackId, cancellationToken);
            var plan = BuildPlan(runtimeConfig);
            var attempts = new List<AmpConsoleAttempt>();

            var dispatched = await TryDispatchConsoleCommandAsync(
                plan,
                payload.ConsoleCommand,
                attempts,
                cancellationToken);

            var resultPayloadJson = JsonSerializer.Serialize(new
            {
                handler = CommandType,
                modpackId = payload.ModpackId,
                modpackName = payload.ModpackName,
                instanceName = plan.InstanceName,
                command = payload.ConsoleCommand,
                attempts = attempts.Select(attempt => new
                {
                    apiMethod = attempt.ApiMethod,
                    parameterKey = attempt.ParameterKey,
                    success = attempt.Success,
                    error = attempt.Error
                }),
                durationSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 2)
            });

            if (!dispatched)
            {
                return AgentCommandExecutionResult.Failed(
                    "amp_console failed: could not execute AMP console command with available API methods.",
                    resultPayloadJson);
            }

            return AgentCommandExecutionResult.Completed(
                $"amp_console command sent for '{payload.ModpackName ?? $"modpack #{payload.ModpackId}"}'.",
                resultPayloadJson);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "amp_console #{CommandId} failed for modpack #{ModpackId}.",
                command.Id,
                payload.ModpackId);

            return AgentCommandExecutionResult.Failed(
                $"amp_console failed: {exception.Message}",
                JsonSerializer.Serialize(new
                {
                    commandId = command.Id,
                    modpackId = payload.ModpackId,
                    modpackName = payload.ModpackName,
                    exception = exception.GetType().FullName,
                    message = exception.Message
                }));
        }
    }

    private static AmpConsolePlan BuildPlan(AgentAmpRuntimeConfigResponse runtimeConfig)
    {
        if (string.IsNullOrWhiteSpace(runtimeConfig.ControllerApiUrl))
        {
            throw new InvalidOperationException("AMP controller API URL is missing from runtime config.");
        }

        if (string.IsNullOrWhiteSpace(runtimeConfig.InstanceName))
        {
            throw new InvalidOperationException("AMP instance name is missing from runtime config.");
        }

        if (string.IsNullOrWhiteSpace(runtimeConfig.Username) ||
            string.IsNullOrWhiteSpace(runtimeConfig.Password))
        {
            throw new InvalidOperationException("AMP runtime credentials are incomplete.");
        }

        EnsureAbsoluteHttpUrl(runtimeConfig.ControllerApiUrl, "amp.controllerApiUrl");

        return new AmpConsolePlan(
            NormalizeAmpApiBaseUrl(runtimeConfig.ControllerApiUrl),
            runtimeConfig.InstanceName.Trim(),
            runtimeConfig.Username.Trim(),
            runtimeConfig.Password,
            runtimeConfig.Token ?? string.Empty,
            runtimeConfig.RememberMe);
    }

    private async Task<bool> TryDispatchConsoleCommandAsync(
        AmpConsolePlan plan,
        string consoleCommand,
        ICollection<AmpConsoleAttempt> attempts,
        CancellationToken cancellationToken)
    {
        var controllerSessionId = await EnsureAmpApiSessionIdAsync(plan, cancellationToken);
        var proxyServerKeys = await ResolveProxyServerKeysAsync(plan, controllerSessionId, cancellationToken);

        var commandCandidates = new[]
        {
            new ConsoleApiCandidate("Core", "SendConsoleMessage", "message"),
            new ConsoleApiCandidate("Core", "SendConsoleCommand", "command"),
            new ConsoleApiCandidate("Core", "SendConsoleCommand", "message"),
            new ConsoleApiCandidate("MinecraftModule", "SendConsoleMessage", "message"),
            new ConsoleApiCandidate("MinecraftModule", "SendConsoleCommand", "command"),
            new ConsoleApiCandidate("MinecraftModule", "SendConsoleCommand", "message")
        };

        foreach (var proxyServerKey in proxyServerKeys)
        {
            try
            {
                var proxySessionId = await EnsureAmpProxySessionIdAsync(
                    plan,
                    proxyServerKey,
                    cancellationToken);

                if (await TryProxyCandidatesAsync(
                        plan,
                        proxyServerKey,
                        proxySessionId,
                        consoleCommand,
                        commandCandidates,
                        sessionLabel: "proxy-session",
                        attempts,
                        cancellationToken))
                {
                    return true;
                }
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "amp_console proxy login/call failed for server key {ProxyServerKey}.",
                    proxyServerKey);

                attempts.Add(new AmpConsoleAttempt(
                    $"Core/Login (proxy-session:{proxyServerKey})",
                    "credentials",
                    false,
                    TruncateForLog(exception.Message, 500)));
            }

            try
            {
                // Last resort: some setups may allow proxy APIs with controller session directly.
                if (await TryProxyCandidatesAsync(
                        plan,
                        proxyServerKey,
                        controllerSessionId,
                        consoleCommand,
                        commandCandidates,
                        sessionLabel: "controller-session",
                        attempts,
                        cancellationToken))
                {
                    return true;
                }
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "amp_console proxy call with controller session failed for server key {ProxyServerKey}.",
                    proxyServerKey);

                attempts.Add(new AmpConsoleAttempt(
                    $"Core/SendConsoleCommand (controller-session:{proxyServerKey})",
                    "command",
                    false,
                    TruncateForLog(exception.Message, 500)));
            }
        }

        return false;
    }

    private async Task<bool> TryProxyCandidatesAsync(
        AmpConsolePlan plan,
        string proxyServerKey,
        string sessionId,
        string consoleCommand,
        IEnumerable<ConsoleApiCandidate> candidates,
        string sessionLabel,
        ICollection<AmpConsoleAttempt> attempts,
        CancellationToken cancellationToken)
    {
        foreach (var candidate in candidates)
        {
            try
            {
                using var responseDocument = await PostAmpProxyRequestAsync(
                    plan.ApiBaseUrl,
                    proxyServerKey,
                    module: candidate.Module,
                    method: candidate.Method,
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["SESSIONID"] = sessionId,
                        [candidate.ParameterKey] = consoleCommand
                    },
                    cancellationToken);

                EnsureAmpApiResponseSucceeded(
                    responseDocument.RootElement,
                    $"ADSModule/Servers/{proxyServerKey}/{candidate.Module}",
                    candidate.Method);

                attempts.Add(new AmpConsoleAttempt(
                    $"{candidate.Module}/{candidate.Method} ({sessionLabel}:{proxyServerKey})",
                    candidate.ParameterKey,
                    true,
                    null));
                return true;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "amp_console call failed via {Module}/{Method} ({ParameterKey}) for server key {ProxyServerKey} [{SessionLabel}].",
                    candidate.Module,
                    candidate.Method,
                    candidate.ParameterKey,
                    proxyServerKey,
                    sessionLabel);

                attempts.Add(new AmpConsoleAttempt(
                    $"{candidate.Module}/{candidate.Method} ({sessionLabel}:{proxyServerKey})",
                    candidate.ParameterKey,
                    false,
                    TruncateForLog(exception.Message, 500)));
            }
        }

        return false;
    }

    private async Task<string> EnsureAmpApiSessionIdAsync(
        AmpConsolePlan plan,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(plan.SessionId))
        {
            return plan.SessionId!;
        }

        using var responseDocument = await PostAmpApiRequestAsync(
            plan.ApiBaseUrl,
            module: "Core",
            method: "Login",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["username"] = plan.Username,
                ["password"] = plan.Password,
                ["token"] = plan.Token,
                ["rememberMe"] = plan.RememberMe
            },
            cancellationToken);

        EnsureAmpApiResponseSucceeded(responseDocument.RootElement, "Core", "Login");

        var sessionId = TryFindStringByPropertyNames(
            responseDocument.RootElement,
            "sessionID",
            "sessionId",
            "session_id",
            "SESSIONID");
        sessionId ??= TryExtractAmpSessionId(responseDocument.RootElement);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new InvalidOperationException("AMP API login succeeded but no session ID was returned.");
        }

        plan.SessionId = sessionId;
        return sessionId;
    }

    private async Task<string> EnsureAmpProxySessionIdAsync(
        AmpConsolePlan plan,
        string proxyServerKey,
        CancellationToken cancellationToken)
    {
        if (plan.ProxySessionIds.TryGetValue(proxyServerKey, out var proxySessionId) &&
            !string.IsNullOrWhiteSpace(proxySessionId))
        {
            return proxySessionId;
        }

        using var responseDocument = await PostAmpProxyRequestAsync(
            plan.ApiBaseUrl,
            proxyServerKey,
            module: "Core",
            method: "Login",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["username"] = plan.Username,
                ["password"] = plan.Password,
                ["token"] = plan.Token,
                ["rememberMe"] = plan.RememberMe
            },
            cancellationToken);

        EnsureAmpApiResponseSucceeded(
            responseDocument.RootElement,
            $"ADSModule/Servers/{proxyServerKey}/Core",
            "Login");

        proxySessionId = TryFindStringByPropertyNames(
            responseDocument.RootElement,
            "sessionID",
            "sessionId",
            "session_id",
            "SESSIONID");
        proxySessionId ??= TryExtractAmpSessionId(responseDocument.RootElement);
        if (string.IsNullOrWhiteSpace(proxySessionId))
        {
            throw new InvalidOperationException(
                $"AMP proxy login succeeded but no session ID was returned for '{proxyServerKey}'.");
        }

        plan.ProxySessionIds[proxyServerKey] = proxySessionId;
        return proxySessionId;
    }

    private async Task<IReadOnlyList<string>> ResolveProxyServerKeysAsync(
        AmpConsolePlan plan,
        string controllerSessionId,
        CancellationToken cancellationToken)
    {
        var proxyServerKeys = new List<string>();

        using var responseDocument = await PostAmpApiRequestAsync(
            plan.ApiBaseUrl,
            module: "ADSModule",
            method: "GetInstances",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["SESSIONID"] = controllerSessionId
            },
            cancellationToken);

        CollectProxyServerKeys(
            responseDocument.RootElement,
            plan.InstanceName,
            proxyServerKeys);

        if (proxyServerKeys.Count == 0)
        {
            proxyServerKeys.Add(plan.InstanceName);
        }

        return proxyServerKeys;
    }

    private async Task<JsonDocument> PostAmpApiRequestAsync(
        string ampApiBaseUrl,
        string module,
        string method,
        IDictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        var requestUrl = $"{ampApiBaseUrl}/API/{module}/{method}";
        return await PostJsonRequestAsync(requestUrl, $"{module}/{method}", payload, cancellationToken);
    }

    private async Task<JsonDocument> PostAmpProxyRequestAsync(
        string ampApiBaseUrl,
        string proxyServerKey,
        string module,
        string method,
        IDictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        var encodedServerKey = Uri.EscapeDataString(proxyServerKey);
        var requestUrl = $"{ampApiBaseUrl}/API/ADSModule/Servers/{encodedServerKey}/API/{module}/{method}";
        return await PostJsonRequestAsync(
            requestUrl,
            $"ADSModule/Servers/{proxyServerKey}/API/{module}/{method}",
            payload,
            cancellationToken);
    }

    private async Task<JsonDocument> PostJsonRequestAsync(
        string requestUrl,
        string operationName,
        IDictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient(SyncModpackCommandHandler.AmpApiHttpClientName);
        if (!client.DefaultRequestHeaders.Accept.Any())
        {
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.cubecoders-ampapi"));
        }

        using var content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");
        using var response = await client.PostAsync(requestUrl, content, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var responseSummary = string.IsNullOrWhiteSpace(responseBody)
                ? $"HTTP {(int)response.StatusCode}"
                : TruncateForLog(responseBody.Trim(), 500);
            throw new InvalidOperationException(
                $"AMP API call {operationName} failed: {responseSummary}");
        }

        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return JsonDocument.Parse("{}");
        }

        return JsonDocument.Parse(responseBody);
    }

    private static void CollectProxyServerKeys(
        JsonElement element,
        string desiredInstanceName,
        ICollection<string> proxyServerKeys)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
            {
                foreach (var item in element.EnumerateArray())
                {
                    CollectProxyServerKeys(item, desiredInstanceName, proxyServerKeys);
                }

                break;
            }
            case JsonValueKind.Object:
            {
                TryCollectProxyServerKeysFromObject(element, desiredInstanceName, proxyServerKeys);
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        CollectProxyServerKeys(property.Value, desiredInstanceName, proxyServerKeys);
                    }
                }

                break;
            }
        }
    }

    private static void TryCollectProxyServerKeysFromObject(
        JsonElement element,
        string desiredInstanceName,
        ICollection<string> proxyServerKeys)
    {
        if (!TryGetPropertyIgnoreCase(element, "InstanceName", out var instanceNameElement))
        {
            return;
        }

        var instanceName = ReadJsonString(instanceNameElement);
        var friendlyName = TryGetPropertyIgnoreCase(element, "FriendlyName", out var friendlyNameElement)
            ? ReadJsonString(friendlyNameElement)
            : null;

        if (!IsMatchingInstanceName(desiredInstanceName, instanceName, friendlyName))
        {
            return;
        }

        if (TryGetPropertyIgnoreCase(element, "InstanceID", out var instanceIdElement))
        {
            AddUniqueServerKey(proxyServerKeys, ReadJsonString(instanceIdElement));
        }

        AddUniqueServerKey(proxyServerKeys, instanceName);
        AddUniqueServerKey(proxyServerKeys, friendlyName);
    }

    private static bool TryGetPropertyIgnoreCase(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            value = property.Value;
            return true;
        }

        return false;
    }

    private static bool IsMatchingInstanceName(
        string desiredInstanceName,
        string? instanceName,
        string? friendlyName)
    {
        return string.Equals(desiredInstanceName, instanceName, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(desiredInstanceName, friendlyName, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddUniqueServerKey(ICollection<string> proxyServerKeys, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return;
        }

        if (proxyServerKeys.Any(existing => string.Equals(existing, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        proxyServerKeys.Add(candidate);
    }

    private static void EnsureAmpApiResponseSucceeded(JsonElement rootElement, string module, string method)
    {
        if (rootElement.ValueKind == JsonValueKind.True)
        {
            return;
        }

        if (rootElement.ValueKind == JsonValueKind.False)
        {
            throw new InvalidOperationException(
                $"AMP API call {module}/{method} returned false.");
        }

        if (rootElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (TryReadBooleanProperty(rootElement, "success", out var success))
        {
            if (success)
            {
                return;
            }

            var successDescription = TryFindStringByPropertyNames(
                rootElement,
                "resultReason",
                "ResultReason",
                "Description",
                "description",
                "Message",
                "message",
                "Title",
                "title") ?? "unknown AMP API error";
            throw new InvalidOperationException(
                $"AMP API call {module}/{method} returned failure: {successDescription}");
        }

        if (!TryReadBooleanProperty(rootElement, "Status", out var status))
        {
            var errorTitle = TryFindStringByPropertyNames(
                rootElement,
                "Title",
                "title");
            var errorMessage = TryFindStringByPropertyNames(
                rootElement,
                "Message",
                "message",
                "Description",
                "description");

            if (!string.IsNullOrWhiteSpace(errorTitle) || !string.IsNullOrWhiteSpace(errorMessage))
            {
                throw new InvalidOperationException(
                    $"AMP API call {module}/{method} returned error: {string.Join(" - ", new[] { errorTitle, errorMessage }.Where(static value => !string.IsNullOrWhiteSpace(value)))}");
            }

            return;
        }

        if (status)
        {
            return;
        }

        var description = TryFindStringByPropertyNames(
            rootElement,
            "Description",
            "description",
            "Message",
            "message",
            "Title",
            "title") ?? "unknown AMP API error";
        throw new InvalidOperationException(
            $"AMP API call {module}/{method} returned failure status: {description}");
    }

    private static bool TryReadBooleanProperty(JsonElement rootElement, string propertyName, out bool value)
    {
        value = false;
        if (rootElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in rootElement.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.True)
            {
                value = true;
                return true;
            }

            if (property.Value.ValueKind == JsonValueKind.False)
            {
                value = false;
                return true;
            }

            return false;
        }

        return false;
    }

    private static string? TryFindStringByPropertyNames(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (propertyNames.Any(name => string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    var foundValue = ReadJsonString(property.Value);
                    if (!string.IsNullOrWhiteSpace(foundValue))
                    {
                        return foundValue;
                    }
                }

                var nested = TryFindStringByPropertyNames(property.Value, propertyNames);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = TryFindStringByPropertyNames(item, propertyNames);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static string? ReadJsonString(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out var numericValue) =>
                numericValue.ToString(CultureInfo.InvariantCulture),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static string? TryExtractAmpSessionId(JsonElement element)
    {
        var unwrapped = element;
        while (unwrapped.ValueKind == JsonValueKind.Object &&
               unwrapped.EnumerateObject().Count() == 1 &&
               unwrapped.EnumerateObject().First().Name.Equals("result", StringComparison.OrdinalIgnoreCase))
        {
            unwrapped = unwrapped.EnumerateObject().First().Value;
        }

        if (TryReadSessionCandidate(unwrapped, out var directSessionId))
        {
            return directSessionId;
        }

        if (unwrapped.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in unwrapped.EnumerateObject())
        {
            if (!property.Name.Equals("result", StringComparison.OrdinalIgnoreCase) &&
                !property.Name.Equals("data", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryReadSessionCandidate(property.Value, out var nestedSessionId))
            {
                return nestedSessionId;
            }
        }

        return null;
    }

    private static bool TryReadSessionCandidate(JsonElement value, out string sessionId)
    {
        sessionId = string.Empty;

        if (value.ValueKind == JsonValueKind.String)
        {
            var candidate = value.GetString()?.Trim();
            if (LooksLikeSessionId(candidate))
            {
                sessionId = candidate!;
                return true;
            }

            return false;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var candidate = property.Value.GetString()?.Trim();
            if (!LooksLikeSessionId(candidate))
            {
                continue;
            }

            sessionId = candidate!;
            return true;
        }

        return false;
    }

    private static bool LooksLikeSessionId(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        if (Guid.TryParse(candidate, out _))
        {
            return true;
        }

        if (candidate.Length is < 16 or > 128)
        {
            return false;
        }

        return candidate.All(static c =>
            char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
    }

    private static bool TryParsePayload(
        string payloadJson,
        out AmpConsolePayload payload,
        out string errorMessage)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;

            if (!root.TryGetProperty("modpack", out var modpackElement) ||
                modpackElement.ValueKind != JsonValueKind.Object)
            {
                payload = default;
                errorMessage = "amp_console requires a 'modpack' object.";
                return false;
            }

            if (!modpackElement.TryGetProperty("id", out var modpackIdElement) ||
                modpackIdElement.ValueKind != JsonValueKind.Number ||
                !modpackIdElement.TryGetInt32(out var modpackId) ||
                modpackId <= 0)
            {
                payload = default;
                errorMessage = "amp_console requires modpack.id (positive integer).";
                return false;
            }

            string? modpackName = null;
            if (modpackElement.TryGetProperty("name", out var modpackNameElement) &&
                modpackNameElement.ValueKind == JsonValueKind.String)
            {
                modpackName = modpackNameElement.GetString()?.Trim();
            }

            var consoleCommand = ReadConsoleCommand(root)?.Trim();
            if (string.IsNullOrWhiteSpace(consoleCommand))
            {
                payload = default;
                errorMessage = "amp_console requires options.consoleCommand (string).";
                return false;
            }

            if (consoleCommand.Length > MaxConsoleCommandLength)
            {
                payload = default;
                errorMessage = $"amp_console command must be {MaxConsoleCommandLength} characters or fewer.";
                return false;
            }

            payload = new AmpConsolePayload(modpackId, modpackName, consoleCommand);
            errorMessage = string.Empty;
            return true;
        }
        catch (JsonException)
        {
            payload = default;
            errorMessage = "amp_console payload is invalid JSON.";
            return false;
        }
    }

    private static string? ReadConsoleCommand(JsonElement rootElement)
    {
        if (rootElement.TryGetProperty("options", out var optionsElement) &&
            optionsElement.ValueKind == JsonValueKind.Object &&
            optionsElement.TryGetProperty("consoleCommand", out var optionsCommandElement) &&
            optionsCommandElement.ValueKind == JsonValueKind.String)
        {
            return optionsCommandElement.GetString();
        }

        if (rootElement.TryGetProperty("consoleCommand", out var commandElement) &&
            commandElement.ValueKind == JsonValueKind.String)
        {
            return commandElement.GetString();
        }

        return null;
    }

    private static void EnsureAbsoluteHttpUrl(string url, string fieldName)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsedUrl) ||
            (parsedUrl.Scheme != Uri.UriSchemeHttp && parsedUrl.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"{fieldName} must be an absolute HTTP/HTTPS URL.");
        }
    }

    private static string NormalizeAmpApiBaseUrl(string ampApiUrl)
    {
        var normalized = ampApiUrl.Trim().TrimEnd('/');
        return normalized.EndsWith("/API", StringComparison.OrdinalIgnoreCase)
            ? normalized[..^4]
            : normalized;
    }

    private static string TruncateForLog(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength];
    }

    private sealed class AmpConsolePlan(
        string apiBaseUrl,
        string instanceName,
        string username,
        string password,
        string token,
        bool rememberMe)
    {
        public string ApiBaseUrl { get; } = apiBaseUrl;

        public string InstanceName { get; } = instanceName;

        public string Username { get; } = username;

        public string Password { get; } = password;

        public string Token { get; } = token;

        public bool RememberMe { get; } = rememberMe;

        public string? SessionId { get; set; }

        public IDictionary<string, string> ProxySessionIds { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private readonly record struct AmpConsolePayload(
        int ModpackId,
        string? ModpackName,
        string ConsoleCommand);

    private readonly record struct AmpConsoleAttempt(
        string ApiMethod,
        string ParameterKey,
        bool Success,
        string? Error);

    private readonly record struct ConsoleApiCandidate(
        string Module,
        string Method,
        string ParameterKey);
}
