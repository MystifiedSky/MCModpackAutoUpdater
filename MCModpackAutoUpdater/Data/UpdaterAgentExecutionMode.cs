namespace MCModpackAutoUpdater.Data;

public static class UpdaterAgentExecutionMode
{
    public const string Local = "Local";
    public const string Remote = "Remote";

    public static readonly string[] All = [Local, Remote];
}
