using System.Globalization;
using System.Text.Json;

namespace MCModpackAutoUpdater.Services;

internal static class StandaloneSyncResultParser
{
    public static bool IsSkipped(string? resultPayloadJson)
    {
        if (string.IsNullOrWhiteSpace(resultPayloadJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(resultPayloadJson);
            return document.RootElement.TryGetProperty("skipped", out var skippedElement) &&
                   skippedElement.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string? ReadResolvedVersionReference(string? resultPayloadJson)
    {
        if (string.IsNullOrWhiteSpace(resultPayloadJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(resultPayloadJson);
            var root = document.RootElement;

            if (root.TryGetProperty("version", out var versionElement) &&
                versionElement.ValueKind == JsonValueKind.Object &&
                versionElement.TryGetProperty("target", out var targetElement))
            {
                var targetValue = ReadJsonStringOrNumber(targetElement);
                if (!string.IsNullOrWhiteSpace(targetValue))
                {
                    return targetValue;
                }
            }

            if (root.TryGetProperty("source", out var sourceElement) &&
                sourceElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var propertyName in new[] { "serverPackFileId", "parentFileId", "selectedVersion" })
                {
                    if (sourceElement.TryGetProperty(propertyName, out var valueElement))
                    {
                        var value = ReadJsonStringOrNumber(valueElement);
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            return value;
                        }
                    }
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string? ReadResolvedVersionDisplay(string? resultPayloadJson)
    {
        if (string.IsNullOrWhiteSpace(resultPayloadJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(resultPayloadJson);
            var root = document.RootElement;

            if (root.TryGetProperty("version", out var versionElement) &&
                versionElement.ValueKind == JsonValueKind.Object &&
                versionElement.TryGetProperty("targetDisplay", out var targetDisplayElement))
            {
                var targetDisplay = ReadJsonStringOrNumber(targetDisplayElement);
                if (!string.IsNullOrWhiteSpace(targetDisplay))
                {
                    return targetDisplay;
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadJsonStringOrNumber(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt64(out var numericValue) => numericValue.ToString(CultureInfo.InvariantCulture),
            JsonValueKind.String => element.GetString()?.Trim(),
            _ => null
        };
    }
}
