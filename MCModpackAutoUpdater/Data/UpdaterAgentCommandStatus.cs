namespace MCModpackAutoUpdater.Data;

public static class UpdaterAgentCommandStatus
{
    public const string Pending = "Pending";
    public const string InProgress = "InProgress";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";

    public static readonly string[] FinalStatuses = [Completed, Failed, Cancelled];
}
