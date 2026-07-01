namespace MCModpackAutoUpdater.Data;

public sealed class UpdaterRuntimeSettings
{
    public int Id { get; set; }

    public bool RunOnStartup { get; set; }

    public bool ExitAfterStartupRun { get; set; }

    public int LoopDelaySeconds { get; set; } = 30;

    public string ScheduleTimeZone { get; set; } = "America/New_York";

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }
}
