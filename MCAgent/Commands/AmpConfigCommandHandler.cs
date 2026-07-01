using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MCAgent.Models.AgentApi;
using MCAgent.Services;

namespace MCAgent.Commands;

public sealed class AmpConfigCommandHandler : IAgentCommandHandler
{
    private const int MaxSettingNodeLength = 250;
    private const int MaxSettingValueLength = 500;
    private static readonly TimeSpan AvailabilityPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan AvailabilityTimeout = TimeSpan.FromMinutes(2);

    private readonly ILogger<AmpConfigCommandHandler> _logger;
    private readonly IAgentApiClient _agentApiClient;
    private readonly IHttpClientFactory _httpClientFactory;

    public AmpConfigCommandHandler(
        ILogger<AmpConfigCommandHandler> logger,
        IAgentApiClient agentApiClient,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _agentApiClient = agentApiClient;
        _httpClientFactory = httpClientFactory;
    }

    public string CommandType => "amp_config";

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

            var beforeValue = await TryGetAmpConfigValueAsync(plan, payload.SettingNode, cancellationToken);
            var availableValuesBefore = await TryGetAmpSettingValuesAsync(
                plan,
                payload.SettingNode,
                payload.RefreshValues,
                cancellationToken);

            var writeAttempts = new List<AmpConfigWriteAttempt>();
            var writeAttempted = false;
            var writeSucceeded = false;
            string? writeError = null;
            string? verifiedByStrategy = null;

            if (!string.IsNullOrWhiteSpace(payload.SettingValue))
            {
                writeAttempted = true;
                var strategyErrors = new List<string>();

                if (await TryApplySettingValueWithStrategyAsync(
                        plan,
                        payload.SettingNode,
                        payload.SettingValue!,
                        "ADSModule/SetInstanceConfig",
                        writeAttempts,
                        strategyErrors,
                        () => ExecuteAmpControllerSetInstanceConfigAsync(
                            plan,
                            payload.SettingNode,
                            payload.SettingValue!,
                            cancellationToken),
                        cancellationToken))
                {
                    writeSucceeded = true;
                    verifiedByStrategy = "ADSModule/SetInstanceConfig";
                }
                else if (await TryApplySettingValueWithStrategyAsync(
                             plan,
                             payload.SettingNode,
                             payload.SettingValue!,
                             "ADSModule/ApplyInstanceConfiguration",
                             writeAttempts,
                             strategyErrors,
                             () => ExecuteAmpControllerApplySingleConfigAsync(
                                 plan,
                                 payload.SettingNode,
                                 payload.SettingValue!,
                                 cancellationToken),
                             cancellationToken))
                {
                    writeSucceeded = true;
                    verifiedByStrategy = "ADSModule/ApplyInstanceConfiguration";
                }
                else if (await TryApplySettingValueWithStrategyAsync(
                             plan,
                             payload.SettingNode,
                             payload.SettingValue!,
                             "Core/SetConfigs",
                             writeAttempts,
                             strategyErrors,
                             () => ExecuteAmpInstanceSetConfigsAsync(
                                 plan,
                                 payload.SettingNode,
                                 payload.SettingValue!,
                                 cancellationToken),
                             cancellationToken))
                {
                    writeSucceeded = true;
                    verifiedByStrategy = "Core/SetConfigs";
                }
                else if (await TryApplySettingValueWithStrategyAsync(
                             plan,
                             payload.SettingNode,
                             payload.SettingValue!,
                             "Core/SetConfig",
                             writeAttempts,
                             strategyErrors,
                             () => ExecuteAmpInstanceSetConfigAsync(
                                 plan,
                                 payload.SettingNode,
                                 payload.SettingValue!,
                                 cancellationToken),
                             cancellationToken))
                {
                    writeSucceeded = true;
                    verifiedByStrategy = "Core/SetConfig";
                }

                if (!writeSucceeded)
                {
                    writeError = string.Join(" | ", strategyErrors);
                }
            }

            var afterValue = await TryGetAmpConfigValueAsync(plan, payload.SettingNode, cancellationToken);
            var availableValuesAfter = await TryGetAmpSettingValuesAsync(
                plan,
                payload.SettingNode,
                refreshValues: true,
                cancellationToken);

            var verified = string.IsNullOrWhiteSpace(payload.SettingValue) ||
                           IsRequestedValueMatch(afterValue, payload.SettingValue!);

            var resultPayloadJson = JsonSerializer.Serialize(new
            {
                handler = CommandType,
                modpackId = payload.ModpackId,
                modpackName = payload.ModpackName,
                instanceName = plan.InstanceName,
                settingNode = payload.SettingNode,
                requestedValue = payload.SettingValue,
                refreshValues = payload.RefreshValues,
                beforeValue,
                afterValue,
                availableValuesBefore,
                availableValuesAfter,
                writeAttempted,
                writeSucceeded,
                verifiedByStrategy,
                verified,
                writeError,
                writeAttempts,
                durationSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 2)
            });

            if (!string.IsNullOrWhiteSpace(payload.SettingValue) && (!writeSucceeded || !verified))
            {
                return AgentCommandExecutionResult.Failed(
                    $"amp_config failed: could not verify '{payload.SettingNode}' was set to '{payload.SettingValue}'.",
                    resultPayloadJson);
            }

            var summary = string.IsNullOrWhiteSpace(payload.SettingValue)
                ? $"amp_config read completed for '{payload.ModpackName ?? $"modpack #{payload.ModpackId}"}'."
                : $"amp_config set '{payload.SettingNode}' for '{payload.ModpackName ?? $"modpack #{payload.ModpackId}"}'.";
            return AgentCommandExecutionResult.Completed(summary, resultPayloadJson);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "amp_config #{CommandId} failed for modpack #{ModpackId}.",
                command.Id,
                payload.ModpackId);

            return AgentCommandExecutionResult.Failed(
                $"amp_config failed: {exception.Message}",
                JsonSerializer.Serialize(new
                {
                    commandId = command.Id,
                    modpackId = payload.ModpackId,
                    modpackName = payload.ModpackName,
                    settingNode = payload.SettingNode,
                    requestedValue = payload.SettingValue,
                    exception = exception.GetType().FullName,
                    message = exception.Message
                }));
        }
    }

    private static AmpConfigPlan BuildPlan(AgentAmpRuntimeConfigResponse runtimeConfig)
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

        return new AmpConfigPlan(
            NormalizeAmpApiBaseUrl(runtimeConfig.ControllerApiUrl),
            runtimeConfig.InstanceName.Trim(),
            runtimeConfig.Username.Trim(),
            runtimeConfig.Password,
            runtimeConfig.Token ?? string.Empty,
            runtimeConfig.RememberMe);
    }

    private async Task ExecuteAmpControllerSetInstanceConfigAsync(
        AmpConfigPlan plan,
        string settingNode,
        string settingValue,
        CancellationToken cancellationToken)
    {
        var instanceReference = await ResolveInstanceReferenceAsync(plan, cancellationToken);
        if (string.IsNullOrWhiteSpace(instanceReference.InstanceName))
        {
            throw new InvalidOperationException("AMP instance name is required for SetInstanceConfig.");
        }

        await ExecuteAmpApiMethodAsync(
            plan,
            module: "ADSModule",
            method: "SetInstanceConfig",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["InstanceName"] = instanceReference.InstanceName,
                ["SettingNode"] = settingNode,
                ["Value"] = settingValue
            },
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(instanceReference.InstanceId))
        {
            await ExecuteAmpApiMethodAsync(
                plan,
                module: "ADSModule",
                method: "RefreshInstanceConfig",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["InstanceId"] = instanceReference.InstanceId
                },
                cancellationToken);
        }

        await WaitForAmpInstanceApiAvailabilityAsync(plan, cancellationToken);
    }

    private async Task ExecuteAmpControllerApplySingleConfigAsync(
        AmpConfigPlan plan,
        string settingNode,
        string settingValue,
        CancellationToken cancellationToken)
    {
        var instanceReference = await ResolveInstanceReferenceAsync(plan, cancellationToken);
        if (string.IsNullOrWhiteSpace(instanceReference.InstanceId))
        {
            throw new InvalidOperationException(
                $"AMP controller instance reference for '{plan.InstanceName}' did not include an InstanceID.");
        }

        await ExecuteAmpApiMethodAsync(
            plan,
            module: "ADSModule",
            method: "ApplyInstanceConfiguration",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["InstanceID"] = instanceReference.InstanceId,
                ["Args"] = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [settingNode] = settingValue
                },
                ["RebuildConfiguration"] = true
            },
            cancellationToken);

        await WaitForAmpInstanceApiAvailabilityAsync(plan, cancellationToken);
    }

    private async Task ExecuteAmpInstanceSetConfigsAsync(
        AmpConfigPlan plan,
        string settingNode,
        string settingValue,
        CancellationToken cancellationToken)
    {
        await InvokeAmpInstanceCoreMethodAsync(
            plan,
            module: "Core",
            method: "SetConfigs",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["data"] = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [settingNode] = settingValue
                }
            },
            cancellationToken);
    }

    private async Task ExecuteAmpInstanceSetConfigAsync(
        AmpConfigPlan plan,
        string settingNode,
        string settingValue,
        CancellationToken cancellationToken)
    {
        await InvokeAmpInstanceCoreMethodAsync(
            plan,
            module: "Core",
            method: "SetConfig",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["node"] = settingNode,
                ["value"] = settingValue
            },
            cancellationToken);
    }

    private async Task<string?> TryGetAmpConfigValueAsync(
        AmpConfigPlan plan,
        string settingNode,
        CancellationToken cancellationToken)
    {
        try
        {
            var configElement = await InvokeAmpInstanceCoreMethodAsync(
                plan,
                module: "Core",
                method: "GetConfig",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["node"] = settingNode
                },
                cancellationToken);

            var configValue = TryExtractAmpConfigValue(configElement, settingNode);
            if (!string.IsNullOrWhiteSpace(configValue))
            {
                return configValue;
            }
        }
        catch (Exception exception)
        {
            _logger.LogDebug(
                exception,
                "amp_config could not read current value for {SettingNode}.",
                settingNode);
        }

        try
        {
            var configsElement = await InvokeAmpInstanceCoreMethodAsync(
                plan,
                module: "Core",
                method: "GetConfigs",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["nodes"] = new[] { settingNode }
                },
                cancellationToken);

            return TryExtractAmpConfigValue(configsElement, settingNode);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(
                exception,
                "amp_config could not read current value from GetConfigs for {SettingNode}.",
                settingNode);
            return null;
        }
    }

    private async Task<IReadOnlyList<string>> TryGetAmpSettingValuesAsync(
        AmpConfigPlan plan,
        string settingNode,
        bool refreshValues,
        CancellationToken cancellationToken)
    {
        try
        {
            var valuesElement = await InvokeAmpInstanceCoreMethodAsync(
                plan,
                module: "Core",
                method: "GetSettingValues",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["SettingNode"] = settingNode,
                    ["WithRefresh"] = refreshValues
                },
                cancellationToken);

            var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectJsonStringTokens(valuesElement, values);
            return values
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim())
                .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception)
        {
            _logger.LogDebug(
                exception,
                "amp_config could not read available values for {SettingNode}.",
                settingNode);
            return [];
        }
    }

    private async Task WaitForAmpInstanceApiAvailabilityAsync(
        AmpConfigPlan plan,
        CancellationToken cancellationToken)
    {
        var timeoutAt = DateTime.UtcNow + AvailabilityTimeout;
        plan.ProxySessionIds.Clear();

        while (DateTime.UtcNow < timeoutAt)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await InvokeAmpInstanceCoreMethodAsync(
                    plan,
                    module: "Core",
                    method: "GetStatus",
                    new Dictionary<string, object?>(StringComparer.Ordinal),
                    cancellationToken);
                return;
            }
            catch
            {
                plan.ProxySessionIds.Clear();
                await Task.Delay(AvailabilityPollInterval, cancellationToken);
            }
        }

        throw new InvalidOperationException(
            $"AMP instance '{plan.InstanceName}' did not become available after configuration update.");
    }

    private async Task<bool> TryApplySettingValueWithStrategyAsync(
        AmpConfigPlan plan,
        string settingNode,
        string settingValue,
        string strategyName,
        ICollection<AmpConfigWriteAttempt> writeAttempts,
        ICollection<string> strategyErrors,
        Func<Task> applyAsync,
        CancellationToken cancellationToken)
    {
        try
        {
            await applyAsync();

            var observedValue = await TryGetAmpConfigValueAsync(plan, settingNode, cancellationToken);
            var verified = IsRequestedValueMatch(observedValue, settingValue);
            writeAttempts.Add(new AmpConfigWriteAttempt(strategyName, true, verified, observedValue, null));

            if (verified)
            {
                return true;
            }

            strategyErrors.Add(
                $"{strategyName} wrote '{settingValue}' but observed '{observedValue ?? "(unknown)"}'");
            return false;
        }
        catch (Exception exception)
        {
            writeAttempts.Add(new AmpConfigWriteAttempt(strategyName, false, false, null, exception.Message));
            strategyErrors.Add($"{strategyName}: {exception.Message}");
            return false;
        }
    }

    private async Task<JsonElement> InvokeAmpInstanceCoreMethodAsync(
        AmpConfigPlan plan,
        string module,
        string method,
        IDictionary<string, object?> parameters,
        CancellationToken cancellationToken)
    {
        var proxyServerKeys = await ResolveProxyServerKeysAsync(plan, cancellationToken);
        var proxyErrors = new List<string>();

        foreach (var proxyServerKey in proxyServerKeys)
        {
            try
            {
                var proxySessionId = await EnsureAmpProxySessionIdAsync(plan, proxyServerKey, cancellationToken);
                var proxyResult = await ExecuteAmpProxyMethodWithResultAsync(
                    plan,
                    proxyServerKey,
                    module,
                    method,
                    parameters,
                    proxySessionId,
                    cancellationToken);
                return UnwrapAmpApiResultElement(proxyResult);
            }
            catch (Exception exception)
            {
                proxyErrors.Add($"{proxyServerKey}: {exception.Message}");
            }
        }

        var controllerSessionId = await EnsureAmpApiSessionIdAsync(plan, cancellationToken);
        foreach (var proxyServerKey in proxyServerKeys)
        {
            try
            {
                var proxyResult = await ExecuteAmpProxyMethodWithResultAsync(
                    plan,
                    proxyServerKey,
                    module,
                    method,
                    parameters,
                    controllerSessionId,
                    cancellationToken);
                return UnwrapAmpApiResultElement(proxyResult);
            }
            catch (Exception exception)
            {
                proxyErrors.Add($"{proxyServerKey} (controller-session): {exception.Message}");
            }
        }

        throw new InvalidOperationException(
            $"AMP instance API {module}/{method} failed via proxy ({string.Join(" | ", proxyErrors)}).");
    }

    private async Task ExecuteAmpApiMethodAsync(
        AmpConfigPlan plan,
        string module,
        string method,
        IDictionary<string, object?> parameters,
        CancellationToken cancellationToken)
    {
        var sessionId = await EnsureAmpApiSessionIdAsync(plan, cancellationToken);
        var payload = new Dictionary<string, object?>(parameters, StringComparer.Ordinal)
        {
            ["SESSIONID"] = sessionId
        };

        using var responseDocument = await PostAmpApiRequestAsync(
            plan.ApiBaseUrl,
            module,
            method,
            payload,
            cancellationToken);

        EnsureAmpApiResponseSucceeded(responseDocument.RootElement, module, method);
    }

    private async Task<JsonElement> ExecuteAmpProxyMethodWithResultAsync(
        AmpConfigPlan plan,
        string proxyServerKey,
        string module,
        string method,
        IDictionary<string, object?> parameters,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>(parameters, StringComparer.Ordinal)
        {
            ["SESSIONID"] = sessionId
        };

        using var responseDocument = await PostAmpProxyRequestAsync(
            plan.ApiBaseUrl,
            proxyServerKey,
            module,
            method,
            payload,
            cancellationToken);

        EnsureAmpApiResponseSucceeded(
            responseDocument.RootElement,
            $"ADSModule/Servers/{proxyServerKey}/API/{module}",
            method);
        return responseDocument.RootElement.Clone();
    }

    private async Task<IReadOnlyList<string>> ResolveProxyServerKeysAsync(
        AmpConfigPlan plan,
        CancellationToken cancellationToken)
    {
        var instanceReference = await ResolveInstanceReferenceAsync(plan, cancellationToken);
        var keys = new List<string>();
        AddUniqueServerKey(keys, instanceReference.InstanceId);
        AddUniqueServerKey(keys, instanceReference.InstanceName);
        AddUniqueServerKey(keys, instanceReference.FriendlyName);
        AddUniqueServerKey(keys, plan.InstanceName);
        return keys;
    }

    private async Task<AmpInstanceReference> ResolveInstanceReferenceAsync(
        AmpConfigPlan plan,
        CancellationToken cancellationToken)
    {
        if (plan.InstanceReference is not null)
        {
            return plan.InstanceReference;
        }

        var sessionId = await EnsureAmpApiSessionIdAsync(plan, cancellationToken);
        using var responseDocument = await PostAmpApiRequestAsync(
            plan.ApiBaseUrl,
            module: "ADSModule",
            method: "GetInstances",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["SESSIONID"] = sessionId
            },
            cancellationToken);

        var references = new List<AmpInstanceReference>();
        CollectInstanceReferences(responseDocument.RootElement, references);

        var resolved = references.FirstOrDefault(reference =>
            string.Equals(reference.InstanceName, plan.InstanceName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(reference.FriendlyName, plan.InstanceName, StringComparison.OrdinalIgnoreCase));

        plan.InstanceReference = resolved ?? new AmpInstanceReference(null, plan.InstanceName, null);
        return plan.InstanceReference;
    }

    private async Task<string> EnsureAmpApiSessionIdAsync(
        AmpConfigPlan plan,
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
        AmpConfigPlan plan,
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
            $"ADSModule/Servers/{proxyServerKey}/API/Core",
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

    private static string? TryExtractAmpConfigValue(JsonElement element, string settingNode)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return Normalize(element.GetString());
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var itemValue = TryExtractAmpConfigValue(item, settingNode);
                if (!string.IsNullOrWhiteSpace(itemValue))
                {
                    return itemValue;
                }
            }

            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (TryGetPropertyIgnoreCase(element, settingNode, out var nodeValue))
        {
            var exactNodeValue = ReadJsonString(nodeValue);
            if (!string.IsNullOrWhiteSpace(exactNodeValue))
            {
                return exactNodeValue.Trim();
            }
        }

        foreach (var propertyName in new[]
                 {
                     "Value",
                     "value",
                     "CurrentValue",
                     "currentValue",
                     "SettingValue",
                     "settingValue",
                     "Result",
                     "result",
                     "Data",
                     "data"
                 })
        {
            if (TryGetPropertyIgnoreCase(element, propertyName, out var propertyValue))
            {
                var resolvedValue = ReadJsonString(propertyValue);
                if (!string.IsNullOrWhiteSpace(resolvedValue))
                {
                    return resolvedValue.Trim();
                }
            }
        }

        var nestedValue = TryFindStringByPropertyNames(
            element,
            "CurrentValue",
            "currentValue",
            "SettingValue",
            "settingValue",
            "SelectedValue",
            "selectedValue",
            "Value",
            "value");
        if (!string.IsNullOrWhiteSpace(nestedValue))
        {
            return nestedValue.Trim();
        }

        return null;
    }

    private static bool IsRequestedValueMatch(string? currentValue, string requestedValue)
    {
        var normalizedCurrentValue = Normalize(currentValue);
        var normalizedRequestedValue = Normalize(requestedValue);
        if (string.IsNullOrWhiteSpace(normalizedCurrentValue) ||
            string.IsNullOrWhiteSpace(normalizedRequestedValue))
        {
            return false;
        }

        return string.Equals(normalizedCurrentValue, normalizedRequestedValue, StringComparison.OrdinalIgnoreCase) ||
               normalizedCurrentValue.Contains(normalizedRequestedValue, StringComparison.OrdinalIgnoreCase);
    }

    private static void CollectJsonStringTokens(JsonElement element, ISet<string> values)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (!string.IsNullOrWhiteSpace(property.Name))
                    {
                        values.Add(property.Name);
                    }

                    CollectJsonStringTokens(property.Value, values);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectJsonStringTokens(item, values);
                }

                break;
            case JsonValueKind.String:
                var value = element.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    values.Add(value);
                }

                break;
        }
    }

    private static void CollectInstanceReferences(JsonElement element, ICollection<AmpInstanceReference> references)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectInstanceReferences(item, references);
                }

                break;
            case JsonValueKind.Object:
                TryCollectInstanceReference(element, references);
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        CollectInstanceReferences(property.Value, references);
                    }
                }

                break;
        }
    }

    private static void TryCollectInstanceReference(JsonElement element, ICollection<AmpInstanceReference> references)
    {
        if (!TryGetPropertyIgnoreCase(element, "InstanceName", out var instanceNameElement))
        {
            return;
        }

        var instanceName = ReadJsonString(instanceNameElement)?.Trim();
        if (string.IsNullOrWhiteSpace(instanceName))
        {
            return;
        }

        var friendlyName = TryGetPropertyIgnoreCase(element, "FriendlyName", out var friendlyNameElement)
            ? ReadJsonString(friendlyNameElement)?.Trim()
            : null;
        var instanceId = TryGetPropertyIgnoreCase(element, "InstanceID", out var instanceIdElement)
            ? ReadJsonString(instanceIdElement)?.Trim()
            : null;

        if (references.Any(reference =>
                string.Equals(reference.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(reference.InstanceName, instanceName, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        references.Add(new AmpInstanceReference(instanceId, instanceName, friendlyName));
    }

    private static JsonElement UnwrapAmpApiResultElement(JsonElement rootElement)
    {
        if (rootElement.ValueKind != JsonValueKind.Object)
        {
            return rootElement.Clone();
        }

        if (TryGetPropertyIgnoreCase(rootElement, "result", out var resultElement))
        {
            if (resultElement.ValueKind == JsonValueKind.String)
            {
                var rawResult = resultElement.GetString();
                if (!string.IsNullOrWhiteSpace(rawResult) &&
                    TryParseEmbeddedJson(rawResult, out var parsedResult))
                {
                    return parsedResult;
                }
            }

            return resultElement.Clone();
        }

        if (TryGetPropertyIgnoreCase(rootElement, "data", out var dataElement))
        {
            if (dataElement.ValueKind == JsonValueKind.String)
            {
                var rawData = dataElement.GetString();
                if (!string.IsNullOrWhiteSpace(rawData) &&
                    TryParseEmbeddedJson(rawData, out var parsedData))
                {
                    return parsedData;
                }
            }

            return dataElement.Clone();
        }

        return rootElement.Clone();
    }

    private static bool TryParseEmbeddedJson(string value, out JsonElement parsedElement)
    {
        parsedElement = default;
        var trimmed = value.Trim();
        if (trimmed.Length < 2 ||
            (trimmed[0] is not '{' and not '['))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            parsedElement = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
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
            var errorTitle = TryFindStringByPropertyNames(rootElement, "Title", "title");
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
            JsonValueKind.Number when value.TryGetInt64(out var numericValue) => numericValue.ToString(CultureInfo.InvariantCulture),
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

        return candidate.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
    }

    private static bool TryParsePayload(
        string payloadJson,
        out AmpConfigPayload payload,
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
                errorMessage = "amp_config requires a 'modpack' object.";
                return false;
            }

            if (!modpackElement.TryGetProperty("id", out var modpackIdElement) ||
                modpackIdElement.ValueKind != JsonValueKind.Number ||
                !modpackIdElement.TryGetInt32(out var modpackId) ||
                modpackId <= 0)
            {
                payload = default;
                errorMessage = "amp_config requires modpack.id (positive integer).";
                return false;
            }

            string? modpackName = null;
            if (modpackElement.TryGetProperty("name", out var modpackNameElement) &&
                modpackNameElement.ValueKind == JsonValueKind.String)
            {
                modpackName = modpackNameElement.GetString()?.Trim();
            }

            if (!root.TryGetProperty("options", out var optionsElement) ||
                optionsElement.ValueKind != JsonValueKind.Object)
            {
                payload = default;
                errorMessage = "amp_config requires an 'options' object.";
                return false;
            }

            if (!optionsElement.TryGetProperty("settingNode", out var settingNodeElement) ||
                settingNodeElement.ValueKind != JsonValueKind.String)
            {
                payload = default;
                errorMessage = "amp_config requires options.settingNode.";
                return false;
            }

            var settingNode = settingNodeElement.GetString()?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(settingNode))
            {
                payload = default;
                errorMessage = "amp_config requires options.settingNode.";
                return false;
            }

            if (settingNode.Length > MaxSettingNodeLength)
            {
                payload = default;
                errorMessage = $"amp_config setting node must be {MaxSettingNodeLength} characters or fewer.";
                return false;
            }

            string? settingValue = null;
            if (optionsElement.TryGetProperty("settingValue", out var settingValueElement) &&
                settingValueElement.ValueKind == JsonValueKind.String)
            {
                settingValue = settingValueElement.GetString()?.Trim();
            }

            if (!string.IsNullOrWhiteSpace(settingValue) &&
                settingValue.Length > MaxSettingValueLength)
            {
                payload = default;
                errorMessage = $"amp_config setting value must be {MaxSettingValueLength} characters or fewer.";
                return false;
            }

            var refreshValues = true;
            if (optionsElement.TryGetProperty("refreshValues", out var refreshValuesElement) &&
                refreshValuesElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                refreshValues = refreshValuesElement.GetBoolean();
            }

            payload = new AmpConfigPayload(modpackId, modpackName, settingNode, settingValue, refreshValues);
            errorMessage = string.Empty;
            return true;
        }
        catch (JsonException)
        {
            payload = default;
            errorMessage = "amp_config payload is invalid JSON.";
            return false;
        }
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

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string TruncateForLog(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength];
    }

    private sealed class AmpConfigPlan(
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
        public AmpInstanceReference? InstanceReference { get; set; }
        public IDictionary<string, string> ProxySessionIds { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private readonly record struct AmpConfigPayload(
        int ModpackId,
        string? ModpackName,
        string SettingNode,
        string? SettingValue,
        bool RefreshValues);

    private sealed record AmpInstanceReference(
        string? InstanceId,
        string InstanceName,
        string? FriendlyName);

    private sealed record AmpConfigWriteAttempt(
        string Strategy,
        bool Applied,
        bool Verified,
        string? ObservedValue,
        string? Error);
}
