using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MCAgent.Models.AgentApi;
using MCAgent.Options;

namespace MCAgent.Services;

/// <summary>
/// Persists the remote command boundary so a restarted agent never repeats a
/// handler whose side effects may already have happened.
/// </summary>
public sealed class AgentCommandCheckpointStore
{
    public const string ReadyState = "Ready";
    public const string ExecutingState = "Executing";
    public const string ResultPendingState = "ResultPending";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly ILogger<AgentCommandCheckpointStore> _logger;
    private readonly string _filePath;
    private readonly string _scopeHash;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<int, AgentCommandCheckpoint> _commands = new();
    private bool _loaded;

    public AgentCommandCheckpointStore(
        IOptions<AgentOptions> options,
        ILogger<AgentCommandCheckpointStore> logger)
    {
        var agentOptions = options.Value;
        _logger = logger;
        _scopeHash = CreateScopeHash(agentOptions.ApiBaseUrl, agentOptions.AuthToken);
        _filePath = ResolveFilePath(agentOptions.CommandStatePath, _scopeHash);
    }

    public string FilePath => _filePath;

    public async Task<AgentCommandCheckpoint?> GetAsync(AgentCommandPayload command)
    {
        await _gate.WaitAsync();
        try
        {
            EnsureLoaded();
            if (!_commands.TryGetValue(command.Id, out var checkpoint))
            {
                return null;
            }

            var fingerprint = CreateCommandFingerprint(command);
            if (!string.Equals(checkpoint.PayloadFingerprint, fingerprint, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "Discarding a stale MCAgent command checkpoint for reused command ID {CommandId}.",
                    command.Id);
                _commands.Remove(command.Id);
                Persist();
                return null;
            }

            return Copy(checkpoint);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task MarkReadyAsync(AgentCommandPayload command) =>
        UpsertAsync(new AgentCommandCheckpoint
        {
            CommandId = command.Id,
            CommandType = command.CommandType,
            PayloadFingerprint = CreateCommandFingerprint(command),
            State = ReadyState,
            UpdatedUtc = DateTimeOffset.UtcNow
        });

    public Task MarkExecutingAsync(AgentCommandPayload command) =>
        UpsertAsync(new AgentCommandCheckpoint
        {
            CommandId = command.Id,
            CommandType = command.CommandType,
            PayloadFingerprint = CreateCommandFingerprint(command),
            State = ExecutingState,
            UpdatedUtc = DateTimeOffset.UtcNow
        });

    public Task SaveResultAsync(
        AgentCommandPayload command,
        AgentCommandCompletionRequest result) =>
        UpsertAsync(new AgentCommandCheckpoint
        {
            CommandId = command.Id,
            CommandType = command.CommandType,
            PayloadFingerprint = CreateCommandFingerprint(command),
            State = ResultPendingState,
            UpdatedUtc = DateTimeOffset.UtcNow,
            Success = result.Success,
            Summary = result.Summary,
            ResultPayloadJson = result.ResultPayloadJson
        });

    public async Task RemoveAsync(int commandId)
    {
        await _gate.WaitAsync();
        try
        {
            EnsureLoaded();
            if (_commands.Remove(commandId))
            {
                Persist();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task UpsertAsync(AgentCommandCheckpoint checkpoint)
    {
        await _gate.WaitAsync();
        try
        {
            EnsureLoaded();
            _commands[checkpoint.CommandId] = Copy(checkpoint);
            Persist();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        if (!File.Exists(_filePath))
        {
            return;
        }

        try
        {
            var document = JsonSerializer.Deserialize<JournalDocument>(
                File.ReadAllText(_filePath),
                JsonOptions);
            if (document is null || document.Version != 1)
            {
                _logger.LogWarning(
                    "Ignoring unsupported MCAgent command journal at {JournalPath}.",
                    _filePath);
                return;
            }

            if (!string.Equals(document.ScopeHash, _scopeHash, StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "Ignoring MCAgent command journal scoped to a different runner or agent token at {JournalPath}.",
                    _filePath);
                return;
            }

            foreach (var checkpoint in document.Commands ?? [])
            {
                if (checkpoint is not null &&
                    checkpoint.CommandId > 0 &&
                    !string.IsNullOrWhiteSpace(checkpoint.PayloadFingerprint) &&
                    checkpoint.State is ReadyState or ExecutingState or ResultPendingState)
                {
                    _commands[checkpoint.CommandId] = Copy(checkpoint);
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogError(
                exception,
                "Could not read MCAgent command journal at {JournalPath}; in-progress commands will be treated as uncheckpointed.",
                _filePath);
        }
    }

    private void Persist()
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var document = new JournalDocument
        {
            Version = 1,
            ScopeHash = _scopeHash,
            Commands = _commands.Values
                .OrderBy(static checkpoint => checkpoint.CommandId)
                .Select(Copy)
                .ToList()
        };
        var temporaryPath = _filePath + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);

        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        catch
        {
            TryDeleteTemporaryFile(temporaryPath);
            throw;
        }
    }

    private static string ResolveFilePath(string configuredPath, string scopeHash)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.GetFullPath(Path.IsPathRooted(configuredPath)
                ? configuredPath
                : Path.Combine(AppContext.BaseDirectory, configuredPath));
        }

        var localDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localDataRoot))
        {
            localDataRoot = AppContext.BaseDirectory;
        }

        return Path.GetFullPath(Path.Combine(
            localDataRoot,
            "MCModpackAutoUpdater",
            "agent-command-state-" + scopeHash[..16] + ".json"));
    }

    private static string CreateCommandFingerprint(AgentCommandPayload command)
    {
        var createdUtc = command.CreatedUtc.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(command.CreatedUtc, DateTimeKind.Utc)
            : command.CreatedUtc.ToUniversalTime();
        var identity = JsonSerializer.Serialize(new
        {
            command.Id,
            CreatedUtcTicks = createdUtc.Ticks,
            CommandType = command.CommandType?.Trim() ?? string.Empty,
            command.PayloadJson
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private static string CreateScopeHash(string apiBaseUrl, string authToken)
    {
        var normalizedApiBaseUrl = Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var apiUri)
            ? apiUri.AbsoluteUri.TrimEnd('/')
            : apiBaseUrl.Trim().TrimEnd('/');
        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(authToken)));
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(normalizedApiBaseUrl + "\n" + tokenHash)));
    }

    private static AgentCommandCheckpoint Copy(AgentCommandCheckpoint checkpoint) => new()
    {
        CommandId = checkpoint.CommandId,
        CommandType = checkpoint.CommandType,
        PayloadFingerprint = checkpoint.PayloadFingerprint,
        State = checkpoint.State,
        UpdatedUtc = checkpoint.UpdatedUtc,
        Success = checkpoint.Success,
        Summary = checkpoint.Summary,
        ResultPayloadJson = checkpoint.ResultPayloadJson
    };

    private void TryDeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(exception, "Could not remove temporary MCAgent command journal file.");
        }
    }

    private sealed class JournalDocument
    {
        public int Version { get; set; }

        public string ScopeHash { get; set; } = string.Empty;

        public List<AgentCommandCheckpoint> Commands { get; set; } = [];
    }
}

public sealed class AgentCommandCheckpoint
{
    public int CommandId { get; set; }

    public string CommandType { get; set; } = string.Empty;

    public string PayloadFingerprint { get; set; } = string.Empty;

    public string State { get; set; } = string.Empty;

    public DateTimeOffset UpdatedUtc { get; set; }

    public bool? Success { get; set; }

    public string? Summary { get; set; }

    public string? ResultPayloadJson { get; set; }

    public AgentCommandCompletionRequest? ToCompletionRequest()
    {
        return State == AgentCommandCheckpointStore.ResultPendingState && Success.HasValue
            ? new AgentCommandCompletionRequest
            {
                Success = Success.Value,
                Summary = Summary,
                ResultPayloadJson = ResultPayloadJson
            }
            : null;
    }
}
