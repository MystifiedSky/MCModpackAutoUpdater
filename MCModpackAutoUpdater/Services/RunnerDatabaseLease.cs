using Microsoft.Extensions.Options;
using MCModpackAutoUpdater.Options;

namespace MCModpackAutoUpdater.Services;

/// <summary>Allows one runner to recover and execute commands for a database at a time.</summary>
public sealed class RunnerDatabaseLease(IOptions<WebUiOptions> options) : IHostedService, IDisposable
{
    private FileStream? _lease;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var databasePath = Path.GetFullPath(options.Value.DatabasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        try
        {
            _lease = new FileStream(databasePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                $"Could not acquire the runner lock for '{databasePath}'. Stop any other runner using this database and ensure its directory is writable.",
                exception);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose()
    {
        _lease?.Dispose();
        _lease = null;
    }
}
