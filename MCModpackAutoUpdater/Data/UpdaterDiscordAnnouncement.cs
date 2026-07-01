namespace MCModpackAutoUpdater.Data;

public sealed class UpdaterDiscordAnnouncement
{
    public int Id { get; set; }

    public int ModpackProfileId { get; set; }

    public UpdaterModpackProfile? ModpackProfile { get; set; }

    public int? ModpackUpdateAuditId { get; set; }

    public UpdaterModpackUpdateAudit? ModpackUpdateAudit { get; set; }

    public string ChannelId { get; set; } = string.Empty;

    public string? RoleId { get; set; }

    public string MessageContent { get; set; } = string.Empty;

    public string Status { get; set; } = UpdaterDiscordAnnouncementStatus.Pending;

    public int RetryCount { get; set; }

    public string? FailureReason { get; set; }

    public string? DiscordMessageId { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    public DateTime? SentUtc { get; set; }
}
