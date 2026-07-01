namespace MCModpackAutoUpdater.Data;

public sealed class UpdaterAgentNode
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Host { get; set; } = string.Empty;

    public string ApiBaseUrl { get; set; } = string.Empty;

    public string Platform { get; set; } = "Linux";

    public string ExecutionMode { get; set; } = UpdaterAgentExecutionMode.Remote;

    public bool Enabled { get; set; } = true;

    public string AuthTokenHash { get; set; } = string.Empty;

    public DateTime AuthTokenLastRotatedUtc { get; set; }

    public DateTime? LastSeenUtc { get; set; }

    public string? LastReportedStatus { get; set; }

    public string? LastReportedVersion { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    public ICollection<UpdaterAgentCommand> Commands { get; set; } = new List<UpdaterAgentCommand>();

    public ICollection<UpdaterModpackProfile> ModpackProfiles { get; set; } = new List<UpdaterModpackProfile>();

    public ICollection<UpdaterModpackUpdateAudit> ModpackUpdateAudits { get; set; } = new List<UpdaterModpackUpdateAudit>();
}
