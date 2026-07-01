using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
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

    private async Task SendPendingAsync(CancellationToken cancellationToken)
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

        var announcements = await dbContext.UpdaterDiscordAnnouncements
            .Where(announcement => announcement.Status == UpdaterDiscordAnnouncementStatus.Pending)
            .OrderBy(announcement => announcement.CreatedUtc)
            .Take(10)
            .ToListAsync(cancellationToken);

        foreach (var announcement in announcements)
        {
            await SendOneAsync(dbContext, settings.BotToken, announcement, cancellationToken);
        }
    }

    private async Task SendOneAsync(
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
            announcement.UpdatedUtc = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            announcement.RetryCount++;
            announcement.Status = announcement.RetryCount >= MaxRetries
                ? UpdaterDiscordAnnouncementStatus.Failed
                : UpdaterDiscordAnnouncementStatus.Pending;
            announcement.FailureReason = Truncate(exception.Message, 1000);
            announcement.UpdatedUtc = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
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
            return document.RootElement.TryGetProperty("id", out var idElement)
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
