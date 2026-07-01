namespace MCAgent.Options;

public sealed class AgentOptions
{
    public string ApiBaseUrl { get; set; } = string.Empty;

    public string AuthToken { get; set; } = string.Empty;

    public string AgentVersion { get; set; } = string.Empty;

    public int PollIntervalSeconds { get; set; } = 30;

    public int ErrorBackoffSeconds { get; set; } = 15;

    public int CommandBatchSize { get; set; } = 20;

    public int HttpTimeoutSeconds { get; set; } = 30;

    public ModpackSyncOptions ModpackSync { get; set; } = new();

    public SelfUpdateOptions SelfUpdate { get; set; } = new();
}

public sealed class SelfUpdateOptions
{
    public bool Enabled { get; set; } = true;

    public string WorkDirectory { get; set; } = "updates";

    public string? ApplyCommandTemplate { get; set; }

    public bool AllowApplyCommandFromPayload { get; set; } = false;
}

public sealed class ModpackSyncOptions
{
    public int MaxWarningMinutes { get; set; } = 240;

    public bool FailIfRestartModeUnconfigured { get; set; }

    public AmpApiOptions AmpApi { get; set; } = new();

    public Dictionary<string, ModpackRestartModeCommandSet> RestartModes { get; set; } = new();
}

public sealed class ModpackRestartModeCommandSet
{
    public string? WarningCommandTemplate { get; set; }

    public string? StopCommandTemplate { get; set; }

    public string? StartCommandTemplate { get; set; }
}

public sealed class AmpApiOptions
{
    public bool Enabled { get; set; }

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string Token { get; set; } = string.Empty;

    public bool RememberMe { get; set; } = true;

    public string WarningMessageTemplate { get; set; } =
        "say Server will restart in {warningMinutes} minute(s) for an automatic modpack update to {targetVersionDisplay}. Please update your clients.";
}
