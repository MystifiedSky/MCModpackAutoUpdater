namespace MCModpackAutoUpdater.Data;

public sealed class UpdaterAmpControllerSettings
{
    public int Id { get; set; }

    public bool Enabled { get; set; }

    public string ControllerApiUrl { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string Token { get; set; } = string.Empty;

    public bool RememberMe { get; set; } = true;

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }
}
