using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MCAgent.Models.AgentApi;
using MCAgent.Options;

namespace MCAgent.Commands;

public sealed class SelfUpdateCommandHandler : IAgentCommandHandler
{
    public const string UpdateHttpClientName = "MCAgent.UpdateDownloader";

    private readonly ILogger<SelfUpdateCommandHandler> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AgentOptions _options;

    public SelfUpdateCommandHandler(
        ILogger<SelfUpdateCommandHandler> logger,
        IHttpClientFactory httpClientFactory,
        IOptions<AgentOptions> options)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
    }

    public string CommandType => "self_update";

    public async Task<AgentCommandExecutionResult> ExecuteAsync(
        AgentCommandPayload command,
        CancellationToken cancellationToken)
    {
        if (!_options.SelfUpdate.Enabled)
        {
            return AgentCommandExecutionResult.Failed(
                "self_update command rejected because self-update is disabled.");
        }

        if (!TryParsePayload(command.PayloadJson, out var payload, out var parseError))
        {
            return AgentCommandExecutionResult.Failed(parseError);
        }

        var workingRoot = ResolveWorkingRoot(_options.SelfUpdate.WorkDirectory);
        Directory.CreateDirectory(workingRoot);

        var updateId = $"{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
        var archivePath = Path.Combine(workingRoot, $"{updateId}.zip");
        var stagingDirectory = Path.Combine(workingRoot, "staged", updateId);
        Directory.CreateDirectory(Path.GetDirectoryName(stagingDirectory)!);

        _logger.LogInformation(
            "Starting self_update for command #{CommandId}. Url={PackageUrl}, Version={Version}.",
            command.Id,
            payload.PackageUrl,
            payload.Version ?? "(unspecified)");

        await DownloadAsync(payload.PackageUrl, archivePath, cancellationToken);

        if (!string.IsNullOrWhiteSpace(payload.ExpectedSha256))
        {
            var actualSha256 = await ComputeSha256Async(archivePath, cancellationToken);
            if (!string.Equals(actualSha256, payload.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(archivePath);
                return AgentCommandExecutionResult.Failed(
                    "self_update hash verification failed.",
                    JsonSerializer.Serialize(new
                    {
                        expectedSha256 = payload.ExpectedSha256,
                        actualSha256
                    }));
            }
        }

        Directory.CreateDirectory(stagingDirectory);
        ZipFile.ExtractToDirectory(archivePath, stagingDirectory, overwriteFiles: true);

        var resultPayload = JsonSerializer.Serialize(new
        {
            updateId,
            version = payload.Version,
            packageUrl = payload.PackageUrl,
            archivePath,
            stagingDirectory
        });

        var applyNow = payload.ApplyNow;
        if (!applyNow)
        {
            return AgentCommandExecutionResult.Completed(
                "Update package downloaded and staged. Apply is pending manual restart/deploy step.",
                resultPayload);
        }

        var applyTemplate = ResolveApplyTemplate(payload.ApplyCommand);
        if (string.IsNullOrWhiteSpace(applyTemplate))
        {
            return AgentCommandExecutionResult.Completed(
                "Update package staged, but no apply command is configured.",
                resultPayload);
        }

        var applyCommand = applyTemplate
            .Replace("{stagingDir}", stagingDirectory, StringComparison.Ordinal)
            .Replace("{baseDir}", AppContext.BaseDirectory, StringComparison.Ordinal)
            .Replace("{pid}", Environment.ProcessId.ToString(), StringComparison.Ordinal);

        StartDetachedShell(applyCommand);

        _logger.LogInformation(
            "Launched self-update apply command for command #{CommandId}.",
            command.Id);

        return AgentCommandExecutionResult.Completed(
            "Update package staged and apply command launched. Agent restart may occur shortly.",
            resultPayload);
    }

    private async Task DownloadAsync(string packageUrl, string destinationPath, CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient(UpdateHttpClientName);
        using var response = await client.GetAsync(packageUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destinationStream = File.Create(destinationPath);
        await sourceStream.CopyToAsync(destinationStream, cancellationToken);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private string ResolveApplyTemplate(string? payloadApplyCommand)
    {
        if (_options.SelfUpdate.AllowApplyCommandFromPayload && !string.IsNullOrWhiteSpace(payloadApplyCommand))
        {
            return payloadApplyCommand.Trim();
        }

        return _options.SelfUpdate.ApplyCommandTemplate?.Trim() ?? string.Empty;
    }

    private static string ResolveWorkingRoot(string workDirectory)
    {
        if (Path.IsPathRooted(workDirectory))
        {
            return workDirectory;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, workDirectory));
    }

    private static void StartDetachedShell(string command)
    {
        if (OperatingSystem.IsWindows())
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false
                }
            };

            process.Start();
            return;
        }

        var linuxProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-lc \"{command}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = false,
                RedirectStandardError = false
            }
        };

        linuxProcess.Start();
    }

    private static bool TryParsePayload(string payloadJson, out SelfUpdatePayload payload, out string errorMessage)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;

            if (!root.TryGetProperty("packageUrl", out var packageUrlElement) ||
                packageUrlElement.ValueKind != JsonValueKind.String)
            {
                payload = default;
                errorMessage = "self_update requires 'packageUrl' (string).";
                return false;
            }

            var packageUrl = packageUrlElement.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(packageUrl) ||
                !Uri.TryCreate(packageUrl, UriKind.Absolute, out var parsedUrl) ||
                (parsedUrl.Scheme != Uri.UriSchemeHttp && parsedUrl.Scheme != Uri.UriSchemeHttps))
            {
                payload = default;
                errorMessage = "self_update packageUrl must be a valid absolute HTTP/HTTPS URL.";
                return false;
            }

            string? expectedSha256 = null;
            if (root.TryGetProperty("expectedSha256", out var expectedHashElement) &&
                expectedHashElement.ValueKind == JsonValueKind.String)
            {
                expectedSha256 = expectedHashElement.GetString()?.Trim();
            }

            string? version = null;
            if (root.TryGetProperty("version", out var versionElement) &&
                versionElement.ValueKind == JsonValueKind.String)
            {
                version = versionElement.GetString()?.Trim();
            }

            var applyNow = false;
            if (root.TryGetProperty("applyNow", out var applyNowElement) &&
                applyNowElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                applyNow = applyNowElement.GetBoolean();
            }

            string? applyCommand = null;
            if (root.TryGetProperty("applyCommand", out var applyCommandElement) &&
                applyCommandElement.ValueKind == JsonValueKind.String)
            {
                applyCommand = applyCommandElement.GetString()?.Trim();
            }

            payload = new SelfUpdatePayload(
                packageUrl,
                expectedSha256,
                version,
                applyNow,
                applyCommand);
            errorMessage = string.Empty;
            return true;
        }
        catch (JsonException)
        {
            payload = default;
            errorMessage = "self_update payload is invalid JSON.";
            return false;
        }
    }

    private readonly record struct SelfUpdatePayload(
        string PackageUrl,
        string? ExpectedSha256,
        string? Version,
        bool ApplyNow,
        string? ApplyCommand);
}
