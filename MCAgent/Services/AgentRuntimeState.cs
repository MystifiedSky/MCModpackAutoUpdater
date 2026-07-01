namespace MCAgent.Services;

public sealed class AgentRuntimeState
{
    private readonly object _sync = new();
    private string _currentStatus = "Booting";

    public string CurrentStatus
    {
        get
        {
            lock (_sync)
            {
                return _currentStatus;
            }
        }
    }

    public void SetStatus(string status)
    {
        var normalizedStatus = string.IsNullOrWhiteSpace(status) ? "Unknown" : status.Trim();
        lock (_sync)
        {
            _currentStatus = normalizedStatus;
        }
    }
}
