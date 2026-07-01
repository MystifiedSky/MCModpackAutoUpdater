namespace MCModpackAutoUpdater.Data;

public sealed class UpdaterDiscordSettings
{
    public int Id { get; set; }

    public bool Enabled { get; set; }

    public string BotToken { get; set; } = string.Empty;

    public string MessageTemplate { get; set; } =
        "{roleMention}{modpackName} updated to {version}.";

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }
}
