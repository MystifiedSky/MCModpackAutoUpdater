namespace MCModpackAutoUpdater.Models;

public sealed class StandaloneUpdaterState
{
    public int LastCommandId { get; set; }

    public Dictionary<string, StandaloneProfileState> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class StandaloneProfileState
{
    public string? CurrentVersion { get; set; }

    public string? CurrentVersionDisplay { get; set; }

    public string? LastScheduledCheckDate { get; set; }

    public DateTime? LastRunUtc { get; set; }

    public DateTime? LastSuccessUtc { get; set; }

    public int? LastCommandId { get; set; }

    public string? LastTriggerSource { get; set; }

    public bool LastSkipped { get; set; }

    public bool LastSucceeded { get; set; }

    public string? LastSummary { get; set; }

    public string? LastResultPayloadJson { get; set; }
}
