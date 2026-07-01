using System.Globalization;

namespace MCModpackAutoUpdater.Services;

internal static class StandaloneStringListParser
{
    private static readonly char[] TextSeparators = ['\r', '\n', ';'];
    private static readonly char[] NumberSeparators = ['\r', '\n', ',', ';'];

    public static string[]? ResolveStringList(string[]? configuredValues, string? textValues)
    {
        var values = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (configuredValues is not null)
        {
            foreach (var value in configuredValues)
            {
                Add(value);
            }
        }

        if (!string.IsNullOrWhiteSpace(textValues))
        {
            foreach (var value in textValues.Split(TextSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                Add(value);
            }
        }

        return values.Count == 0 ? null : values.ToArray();

        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var normalized = value.Trim();
            if (seen.Add(normalized))
            {
                values.Add(normalized);
            }
        }
    }

    public static int[]? ResolvePositiveIntegerList(int[]? configuredValues, string? textValues)
    {
        var values = new List<int>();
        var seen = new HashSet<int>();

        if (configuredValues is not null)
        {
            foreach (var value in configuredValues.Where(static value => value > 0))
            {
                Add(value);
            }
        }

        if (!string.IsNullOrWhiteSpace(textValues))
        {
            foreach (var value in textValues.Split(NumberSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
                    parsed > 0)
                {
                    Add(parsed);
                }
            }
        }

        return values.Count == 0 ? null : values.ToArray();

        void Add(int value)
        {
            if (seen.Add(value))
            {
                values.Add(value);
            }
        }
    }
}
