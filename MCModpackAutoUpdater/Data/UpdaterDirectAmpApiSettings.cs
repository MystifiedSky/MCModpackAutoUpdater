namespace MCModpackAutoUpdater.Data;

public sealed class UpdaterDirectAmpApiSettings
{
    public int Id { get; set; }

    public bool Enabled { get; set; }

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string Token { get; set; } = string.Empty;

    public bool RememberMe { get; set; } = true;

    public string WarningMessageTemplate { get; set; } =
        "say Server will restart in {warningMinutes} minute(s) for an automatic modpack update to {targetVersionDisplay}. Please update your clients.";

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }
}
