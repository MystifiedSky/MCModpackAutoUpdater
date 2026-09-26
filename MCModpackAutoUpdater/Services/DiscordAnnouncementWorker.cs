using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Globalization;
using System.Net;
using Microsoft.EntityFrameworkCore;
using MCModpackAutoUpdater.Data;

namespace MCModpackAutoUpdater.Services;

public sealed class DiscordAnnouncementWorker : BackgroundService
{
    public const string HttpClientName = "MCModpackAutoUpdater.Discord";
    private const int MaxRetries = 5;

    private readonly IServiceProvider _serviceProvider;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DiscordAnnouncementWorker> _logger;

    public DiscordAnnouncementWorker(
        IServiceProvider serviceProvider,
        IHttpClientFactory httpClientFactory,
        ILogger<DiscordAnnouncementWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SendPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Discord announcement worker loop failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }

    internal async Task SendPendingAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<UpdaterIdentityDbContext>();
        var settings = await dbContext.UpdaterDiscordSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);
        if (settings is null || !settings.Enabled || string.IsNullOrWhiteSpace(settings.BotToken))
        {
            return;
        }

        var utcNow = DateTime.UtcNow;
        var announcements = await dbContext.UpdaterDiscordAnnouncements
            .Where(announcement => announcement.Status == UpdaterDiscordAnnouncementStatus.Pending &&
                (announcement.NextAttemptUtc == null || announcement.NextAttemptUtc <= utcNow))
            .OrderBy(announcement => announcement.CreatedUtc)
            .Take(10)
            .ToListAsync(cancellationToken);

        foreach (var announcement in announcements)
        {
            var rateLimited = await SendOneAsync(dbContext, settings.BotToken, announcement, cancellationToken);
            if (rateLimited)
            {
                // Persist the pause across the whole queue, including restarts. Being
                // conservative for a route limit also honors Discord's global limits.
                await dbContext.UpdaterDiscordAnnouncements
                    .Where(item => item.Status == UpdaterDiscordAnnouncementStatus.Pending &&
                        (item.NextAttemptUtc == null || item.NextAttemptUtc < announcement.NextAttemptUtc))
                    .ExecuteUpdateAsync(update => update.SetProperty(item => item.NextAttemptUtc, announcement.NextAttemptUtc), cancellationToken);
                break;
            }
        }
    }

    internal async Task<bool> SendOneAsync(
        UpdaterIdentityDbContext dbContext,
        string botToken,
        UpdaterDiscordAnnouncement announcement,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient(HttpClientName);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", botToken);

            using var response = await client.PostAsJsonAsync(
                $"channels/{Uri.EscapeDataString(announcement.ChannelId)}/messages",
                new
                {
                    content = announcement.MessageContent,
                    nonce = $"mcupdate-{announcement.Id}",
                    enforce_nonce = true,
                    allowed_mentions = new
                    {
                        parse = Array.Empty<string>(),
                        roles = string.IsNullOrWhiteSpace(announcement.RoleId)
                            ? Array.Empty<string>()
                            : new[] { announcement.RoleId }
                    }
                },
                cancellationToken);

            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = ReadRetryAfter(response, responseText);
                announcement.NextAttemptUtc = DateTime.UtcNow.Add(retryAfter);
                announcement.FailureReason = "Discord rate limit; delivery will resume after the requested delay.";
                announcement.UpdatedUtc = DateTime.UtcNow;
                await dbContext.SaveChangesAsync(cancellationToken);
                return true;
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(responseText)
                        ? $"Discord returned HTTP {(int)response.StatusCode}."
                        : $"Discord returned HTTP {(int)response.StatusCode}: {Truncate(responseText.Trim(), 500)}");
            }

            announcement.Status = UpdaterDiscordAnnouncementStatus.Sent;
            announcement.DiscordMessageId = TryReadDiscordMessageId(responseText);
            announcement.SentUtc = DateTime.UtcNow;
            announcement.FailureReason = null;
            announcement.NextAttemptUtc = null;
            announcement.UpdatedUtc = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            announcement.RetryCount++;
            announcement.Status = announcement.RetryCount >= MaxRetries
                ? UpdaterDiscordAnnouncementStatus.Failed
                : UpdaterDiscordAnnouncementStatus.Pending;
            announcement.FailureReason = Truncate(exception.Message, 1000);
            announcement.UpdatedUtc = DateTime.UtcNow;
            announcement.NextAttemptUtc = announcement.Status == UpdaterDiscordAnnouncementStatus.Pending
                ? DateTime.UtcNow.AddSeconds(Math.Min(900, 15 * Math.Pow(2, announcement.RetryCount)))
                : null;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        return false;
    }

    internal static TimeSpan ReadRetryAfter(HttpResponseMessage response, string body)
    {
        var seconds = response.Headers.RetryAfter?.Delta?.TotalSeconds ??
            (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow)?.TotalSeconds ?? 0;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("retry_after", out var retry) &&
                double.TryParse(retry.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value))
            {
                seconds = Math.Max(seconds, value);
            }
        }
        catch (JsonException) { }
        // Invalid/missing responses still get a backoff. Never shorten a valid limit.
        return TimeSpan.FromSeconds(seconds > 0 ? Math.Min(seconds, (DateTime.MaxValue - DateTime.UtcNow).TotalSeconds - 1) : 30);
    }

    private static string? TryReadDiscordMessageId(string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(responseText);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                ? idElement.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
