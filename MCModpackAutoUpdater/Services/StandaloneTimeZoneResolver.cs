namespace MCModpackAutoUpdater.Services;

internal static class StandaloneTimeZoneResolver
{
    public static TimeZoneInfo Resolve(string? configuredTimeZone)
    {
        if (string.IsNullOrWhiteSpace(configuredTimeZone) ||
            string.Equals(configuredTimeZone, "Local", StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.Local;
        }

        if (string.Equals(configuredTimeZone, "UTC", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(configuredTimeZone, "Etc/UTC", StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.Utc;
        }

        var candidates = new List<string> { configuredTimeZone.Trim() };
        if (string.Equals(configuredTimeZone.Trim(), "America/New_York", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add("Eastern Standard Time");
        }
        else if (string.Equals(configuredTimeZone.Trim(), "Eastern Standard Time", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add("America/New_York");
        }

        foreach (var candidate in candidates)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(candidate);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        throw new InvalidOperationException($"Time zone '{configuredTimeZone}' was not found on this host.");
    }
}
