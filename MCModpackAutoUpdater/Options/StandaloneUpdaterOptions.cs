namespace MCModpackAutoUpdater.Options;

public sealed class StandaloneUpdaterOptions
{
    public bool RunOnStartup { get; set; }

    public bool ExitAfterStartupRun { get; set; }

    public int LoopDelaySeconds { get; set; } = 30;

    public string ScheduleTimeZone { get; set; } = "America/New_York";

    public StandaloneAmpControllerOptions AmpController { get; set; } = new();
}

public sealed class StandaloneAmpControllerOptions
{
    public bool Enabled { get; set; }

    public string ControllerApiUrl { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string Token { get; set; } = string.Empty;

    public bool RememberMe { get; set; } = true;
}
