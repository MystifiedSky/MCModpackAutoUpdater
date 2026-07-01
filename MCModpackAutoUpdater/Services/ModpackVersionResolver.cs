using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MCModpackAutoUpdater.Data;

namespace MCModpackAutoUpdater.Services;

public sealed class ModpackVersionResolver : IModpackVersionResolver
{
    private const int CurseForgePublishedStatus = 4;
    private static readonly Regex HumanVersionRegex = new(@"\d+(?:\.\d+)+", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<ModpackVersionResolver> _logger;

    public ModpackVersionResolver(HttpClient httpClient, ILogger<ModpackVersionResolver> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<ModpackVersionResolutionResult> ResolveTargetVersionAsync(
        UpdaterModpackProfile modpack,
        string? requestedVersion,
        CancellationToken cancellationToken)
    {
        var selectedVersion = NormalizeVersionSelector(requestedVersion ?? Normalize(modpack.VersionLock));
        var providerIsCurseForge = string.Equals(modpack.Provider, "CurseForge", StringComparison.OrdinalIgnoreCase);
        var providerIsFtb = string.Equals(modpack.Provider, "FTB", StringComparison.OrdinalIgnoreCase);
        var explicitServerPackUrl = Normalize(modpack.ServerPackUrl);

        if (providerIsCurseForge && TryParseCurseForgeProjectId(modpack.SourceReference, out var projectId))
        {
            try
            {
                var parentFile = modpack.BuildServerPackFromClientFiles
                    ? await ResolveCurseForgeClientFileAsync(projectId, selectedVersion, cancellationToken)
                    : await ResolveCurseForgeParentFileAsync(projectId, selectedVersion, cancellationToken);

                if (modpack.BuildServerPackFromClientFiles)
                {
                    var targetVersion = parentFile.Id.ToString(CultureInfo.InvariantCulture);
                    return new ModpackVersionResolutionResult(
                        true,
                        targetVersion,
                        ResolveCurseForgeClientDisplayVersion(parentFile, selectedVersion),
                        "curseforge_client_generated_server_pack",
                        $"Resolved CurseForge client file ID {targetVersion}.",
                        ProjectId: projectId,
                        ParentFileId: parentFile.Id,
                        SelectedVersion: selectedVersion);
                }

                var serverPackFile = await ResolveCurseForgeServerPackFileAsync(projectId, parentFile.Id, cancellationToken);
                var serverPackVersion = serverPackFile.Id.ToString(CultureInfo.InvariantCulture);
                return new ModpackVersionResolutionResult(
                    true,
                    serverPackVersion,
                    ResolveCurseForgeDisplayVersion(parentFile, serverPackFile, selectedVersion),
                    "curseforge_additional_server_pack",
                    $"Resolved CurseForge server pack file ID {serverPackVersion}.",
                    ProjectId: projectId,
                    ParentFileId: parentFile.Id,
                    ServerPackFileId: serverPackFile.Id,
                    SelectedVersion: selectedVersion);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to resolve CurseForge version for modpack {ModpackId}.", modpack.Id);
                return ModpackVersionResolutionResult.Failure(exception.Message);
            }
        }

        if (providerIsFtb && TryParseFtbPackId(modpack.SourceReference, out var packId))
        {
            try
            {
                var version = await ResolveFtbVersionAsync(packId, selectedVersion, cancellationToken);
                var targetVersion = version.Id.ToString(CultureInfo.InvariantCulture);
                return new ModpackVersionResolutionResult(
                    true,
                    targetVersion,
                    Normalize(version.Name) ?? ResolveDisplayVersionFromSelector(selectedVersion),
                    "ftb_public_version",
                    $"Resolved FTB version ID {targetVersion}.",
                    SelectedVersion: selectedVersion);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to resolve FTB version for modpack {ModpackId}.", modpack.Id);
                return ModpackVersionResolutionResult.Failure(exception.Message);
            }
        }

        if (explicitServerPackUrl is not null)
        {
            if (!Uri.TryCreate(explicitServerPackUrl, UriKind.Absolute, out var parsedUrl) ||
                (parsedUrl.Scheme != Uri.UriSchemeHttp && parsedUrl.Scheme != Uri.UriSchemeHttps))
            {
                return ModpackVersionResolutionResult.Failure("Server pack URL is not a valid HTTP/HTTPS absolute URL.");
            }

            if (TryExtractTrailingNumericSegment(parsedUrl.AbsolutePath, out var extractedFileId))
            {
                var target = extractedFileId.ToString(CultureInfo.InvariantCulture);
                return new ModpackVersionResolutionResult(
                    true,
                    target,
                    ResolveDisplayVersionFromDirectUrl(parsedUrl, selectedVersion),
                    "direct_url_numeric_segment",
                    $"Resolved version {target} from server pack URL.");
            }

            if (!string.IsNullOrWhiteSpace(selectedVersion))
            {
                return new ModpackVersionResolutionResult(
                    true,
                    selectedVersion,
                    ResolveDisplayVersionFromSelector(selectedVersion),
                    "direct_url_selected_version",
                    $"Using selected version '{selectedVersion}' for direct URL.");
            }

            return ModpackVersionResolutionResult.Failure(
                "Cannot determine target version from direct server pack URL. Use Version Lock or a URL with a numeric file ID.");
        }

        return ModpackVersionResolutionResult.Failure(
            providerIsFtb
                ? "Configure an FTB pack ID in source reference or provide server pack URL."
                : "Configure CurseForge source reference or server pack URL.");
    }

    private async Task<CurseForgeFileEntry> ResolveCurseForgeParentFileAsync(
        int projectId,
        string? selectedVersion,
        CancellationToken cancellationToken)
    {
        var files = await GetCurseForgePublishedFilesAsync(projectId, cancellationToken);
        if (string.IsNullOrWhiteSpace(selectedVersion))
        {
            foreach (var file in files)
            {
                if (await HasDownloadableCurseForgeServerPackAsync(projectId, file, cancellationToken))
                {
                    return file;
                }
            }

            throw new InvalidOperationException($"No downloadable server packs were found for CurseForge project {projectId}.");
        }

        var normalizedSelector = selectedVersion.Trim();
        if (int.TryParse(normalizedSelector, NumberStyles.Integer, CultureInfo.InvariantCulture, out var exactFileId))
        {
            var exactMatch = files.FirstOrDefault(file => file.Id == exactFileId);
            if (exactMatch is not null &&
                await HasDownloadableCurseForgeServerPackAsync(projectId, exactMatch, cancellationToken))
            {
                return exactMatch;
            }

            throw new InvalidOperationException($"CurseForge file ID {exactFileId} was not found or has no server pack.");
        }

        foreach (var file in files.Where(file => ContainsInvariant(file.DisplayName, normalizedSelector) || ContainsInvariant(file.FileName, normalizedSelector)))
        {
            if (await HasDownloadableCurseForgeServerPackAsync(projectId, file, cancellationToken))
            {
                return file;
            }
        }

        throw new InvalidOperationException($"No CurseForge file matched selector '{normalizedSelector}' with a downloadable server pack.");
    }

    private async Task<CurseForgeFileEntry> ResolveCurseForgeClientFileAsync(
        int projectId,
        string? selectedVersion,
        CancellationToken cancellationToken)
    {
        var files = await GetCurseForgePublishedFilesAsync(projectId, cancellationToken);
        if (string.IsNullOrWhiteSpace(selectedVersion))
        {
            return files[0];
        }

        var normalizedSelector = selectedVersion.Trim();
        if (int.TryParse(normalizedSelector, NumberStyles.Integer, CultureInfo.InvariantCulture, out var exactFileId))
        {
            return files.FirstOrDefault(file => file.Id == exactFileId)
                   ?? throw new InvalidOperationException($"CurseForge client file ID {exactFileId} was not found.");
        }

        return files.FirstOrDefault(file => ContainsInvariant(file.DisplayName, normalizedSelector) || ContainsInvariant(file.FileName, normalizedSelector))
               ?? throw new InvalidOperationException($"No CurseForge client file matched selector '{normalizedSelector}'.");
    }

    private async Task<CurseForgeFileEntry> ResolveCurseForgeServerPackFileAsync(
        int projectId,
        int parentFileId,
        CancellationToken cancellationToken)
    {
        var additionalFiles = await GetCurseForgeAdditionalFilesAsync(projectId, parentFileId, cancellationToken);
        return additionalFiles.Count == 0
            ? throw new InvalidOperationException($"No downloadable additional ZIP files were found for CurseForge file {parentFileId}.")
            : additionalFiles[0];
    }

    private async Task<List<CurseForgeFileEntry>> GetCurseForgePublishedFilesAsync(
        int projectId,
        CancellationToken cancellationToken)
    {
        var response = await GetJsonAsync<CurseForgeListResponse<CurseForgeFileEntry>>(
            $"https://www.curseforge.com/api/v1/mods/{projectId}/files?pageSize=50",
            cancellationToken);
        var files = response.Data?
            .Where(file => file.Status == CurseForgePublishedStatus)
            .OrderByDescending(file => file.DateCreated)
            .ToList()
            ?? [];

        return files.Count == 0
            ? throw new InvalidOperationException($"No published files found for CurseForge project {projectId}.")
            : files;
    }

    private async Task<bool> HasDownloadableCurseForgeServerPackAsync(
        int projectId,
        CurseForgeFileEntry parentFile,
        CancellationToken cancellationToken)
    {
        return parentFile.HasServerPack ||
               parentFile.AdditionalServerPackFilesCount > 0 ||
               (await GetCurseForgeAdditionalFilesAsync(projectId, parentFile.Id, cancellationToken)).Count > 0;
    }

    private async Task<List<CurseForgeFileEntry>> GetCurseForgeAdditionalFilesAsync(
        int projectId,
        int parentFileId,
        CancellationToken cancellationToken)
    {
        var response = await GetJsonAsync<CurseForgeListResponse<CurseForgeFileEntry>>(
            $"https://www.curseforge.com/api/v1/mods/{projectId}/files/{parentFileId}/additional-files",
            cancellationToken);
        return response.Data?
            .Where(file => file.Status == CurseForgePublishedStatus)
            .Where(file => file.FileName?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true)
            .OrderByDescending(LooksLikeServerPack)
            .ThenByDescending(file => file.DateCreated)
            .ToList()
            ?? [];
    }

    private async Task<FtbVersionEntry> ResolveFtbVersionAsync(
        int packId,
        string? selectedVersion,
        CancellationToken cancellationToken)
    {
        var response = await GetJsonAsync<FtbPublicModpackResponse>(
            $"https://api.feed-the-beast.com/v1/modpacks/public/modpack/{packId}",
            cancellationToken);
        var versions = response.Versions?
            .Where(version => !version.Private)
            .OrderByDescending(version => version.Released)
            .ThenByDescending(version => version.Updated)
            .ToList()
            ?? [];

        if (versions.Count == 0)
        {
            throw new InvalidOperationException($"No public versions found for FTB pack {packId}.");
        }

        if (string.IsNullOrWhiteSpace(selectedVersion))
        {
            return versions.FirstOrDefault(version => string.Equals(version.Type, "release", StringComparison.OrdinalIgnoreCase))
                   ?? versions[0];
        }

        var normalizedSelector = selectedVersion.Trim();
        if (TryParseFtbVersionId(normalizedSelector, out var exactVersionId))
        {
            return versions.FirstOrDefault(version => version.Id == exactVersionId)
                   ?? throw new InvalidOperationException($"FTB version ID {exactVersionId} was not found for pack {packId}.");
        }

        return versions.FirstOrDefault(version => ContainsInvariant(version.Name, normalizedSelector))
               ?? throw new InvalidOperationException($"No FTB version matched selector '{normalizedSelector}' for pack {packId}.");
    }

    private async Task<T> GetJsonAsync<T>(string requestUrl, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(requestUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
               ?? throw new InvalidOperationException($"Unexpected empty response for {requestUrl}.");
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? NormalizeVersionSelector(string? value)
    {
        var normalized = Normalize(value);
        return string.Equals(normalized, "latest", StringComparison.OrdinalIgnoreCase) ? null : normalized;
    }

    private static bool TryParseCurseForgeProjectId(string? sourceReference, out int projectId)
    {
        projectId = 0;
        if (string.IsNullOrWhiteSpace(sourceReference))
        {
            return false;
        }

        var normalized = sourceReference.Trim();
        if (int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out projectId) && projectId > 0)
        {
            return true;
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            return false;
        }

        foreach (var segment in uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            if (int.TryParse(segment, NumberStyles.Integer, CultureInfo.InvariantCulture, out projectId) && projectId > 0)
            {
                return true;
            }
        }

        projectId = 0;
        return false;
    }

    private static bool TryParseFtbPackId(string? sourceReference, out int packId)
    {
        return TryParseFtbNumericReference(sourceReference, out packId);
    }

    private static bool TryParseFtbVersionId(string? versionReference, out int versionId)
    {
        return TryParseFtbNumericReference(versionReference, out versionId);
    }

    private static bool TryParseFtbNumericReference(string? value, out int parsedId)
    {
        parsedId = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        if (int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedId) && parsedId > 0)
        {
            return true;
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            return false;
        }

        foreach (var segment in uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            if (TryParseLeadingPositiveInteger(segment, out parsedId))
            {
                return true;
            }
        }

        parsedId = 0;
        return false;
    }

    private static bool TryExtractTrailingNumericSegment(string absolutePath, out int value)
    {
        value = 0;
        foreach (var segment in absolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            if (int.TryParse(segment, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseLeadingPositiveInteger(string value, out int parsedId)
    {
        parsedId = 0;
        var digitCount = 0;
        while (digitCount < value.Length && char.IsDigit(value[digitCount]))
        {
            digitCount++;
        }

        return digitCount > 0 &&
               int.TryParse(value[..digitCount], NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedId) &&
               parsedId > 0;
    }

    private static bool ContainsInvariant(string? source, string value)
    {
        return !string.IsNullOrWhiteSpace(source) &&
               source.Contains(value, StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeServerPack(CurseForgeFileEntry file)
    {
        return ContainsInvariant(file.DisplayName, "server") || ContainsInvariant(file.FileName, "server");
    }

    private static string? ResolveCurseForgeDisplayVersion(
        CurseForgeFileEntry parentFile,
        CurseForgeFileEntry serverPackFile,
        string? selectedVersion)
    {
        foreach (var candidate in new[] { parentFile.DisplayName, parentFile.FileName, serverPackFile.DisplayName, serverPackFile.FileName })
        {
            var resolved = TryExtractHumanReadableVersion(candidate);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }
        }

        return ResolveDisplayVersionFromSelector(selectedVersion);
    }

    private static string? ResolveCurseForgeClientDisplayVersion(CurseForgeFileEntry parentFile, string? selectedVersion)
    {
        foreach (var candidate in new[] { parentFile.DisplayName, parentFile.FileName })
        {
            var resolved = TryExtractHumanReadableVersion(candidate);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }
        }

        return ResolveDisplayVersionFromSelector(selectedVersion);
    }

    private static string? ResolveDisplayVersionFromDirectUrl(Uri parsedUrl, string? selectedVersion)
    {
        return TryExtractHumanReadableVersion(Path.GetFileName(parsedUrl.AbsolutePath)) ??
               ResolveDisplayVersionFromSelector(selectedVersion);
    }

    private static string? ResolveDisplayVersionFromSelector(string? selectedVersion)
    {
        var normalized = Normalize(selectedVersion);
        if (string.IsNullOrWhiteSpace(normalized) ||
            int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return null;
        }

        return TryExtractHumanReadableVersion(normalized) ??
               (normalized.Contains('.', StringComparison.Ordinal) ? normalized : null);
    }

    private static string? TryExtractHumanReadableVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var matches = HumanVersionRegex.Matches(value);
        return matches.Count == 0 ? null : matches[^1].Value;
    }

    private sealed class CurseForgeListResponse<TItem>
    {
        public List<TItem>? Data { get; set; }
    }

    private sealed class CurseForgeFileEntry
    {
        public int Id { get; set; }
        public int Status { get; set; }
        public string? DisplayName { get; set; }
        public string? FileName { get; set; }
        public DateTimeOffset DateCreated { get; set; }
        public bool HasServerPack { get; set; }
        public int AdditionalServerPackFilesCount { get; set; }
    }

    private sealed class FtbPublicModpackResponse
    {
        public List<FtbVersionEntry>? Versions { get; set; }
    }

    private sealed class FtbVersionEntry
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public string? Type { get; set; }
        public long Updated { get; set; }
        public long Released { get; set; }
        public bool Private { get; set; }
    }
}
