using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TeleThreads.Storage;

namespace TeleThreads.TelegramScanner;

// Long-polls the bot for channel posts, downloads their photos and stores them as Received posts.
public sealed class TelegramScannerService(
    IHttpClientFactory httpClientFactory,
    PostRepository repository,
    IOptions<TelegramOptions> telegram,
    IOptions<StorageOptions> storage,
    TimeProvider time,
    ILogger<TelegramScannerService> logger) : BackgroundService
{
    private static readonly TimeSpan AlbumQuietPeriod = TimeSpan.FromSeconds(3);
    // Album parts still being collected are lost if the service stops within the ~3 s quiet period.
    private readonly MediaGroupAggregator _aggregator = new(AlbumQuietPeriod);

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(telegram.Value.BotToken))
        {
            logger.LogWarning("Telegram:BotToken is not set; the Telegram scanner is disabled");
            return;
        }

        var bot = new TelegramBotClient(telegram.Value.BotToken, httpClientFactory.CreateClient("telegram"), stoppingToken);
        var offset = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Poll briefly while an album is being collected, so it is saved soon after its last part.
                var updates = await bot.GetUpdates(
                    offset,
                    timeout: _aggregator.HasPending ? 1 : 25,
                    allowedUpdates: [UpdateType.ChannelPost],
                    cancellationToken: stoppingToken);
                foreach (var update in updates)
                {
                    offset = update.Id + 1;
                    if (update.ChannelPost is { } message)
                        await HandleAsync(bot, message, stoppingToken);
                }
                foreach (var post in _aggregator.TakeCompleted(Now))
                    await SaveAsync(bot, post, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Telegram polling failed");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task HandleAsync(ITelegramBotClient bot, Message message, CancellationToken ct)
    {
        if (message.Chat.Id != telegram.Value.ChannelId)
        {
            // Also the easiest way to find out the channel id when setting the bot up.
            logger.LogInformation(
                "Ignoring post from chat {ChatId} ({Title}); Telegram:ChannelId is '{ChannelId}'",
                message.Chat.Id, message.Chat.Title, telegram.Value.ChannelId);
            return;
        }

        var incoming = new IncomingMessage(
            message.Chat.Id,
            message.Id,
            message.MediaGroupId,
            message.Date,
            message.Text ?? message.Caption,
            message.Photo?.MaxBy(p => p.Width * p.Height)?.FileId);
        // Album parts without a photo (e.g. a video) are kept: one of them may carry the caption.
        if (incoming.Text is null && incoming.PhotoFileId is null && incoming.MediaGroupId is null)
        {
            logger.LogInformation("Skipping message {MessageId}: no text or photo", message.Id);
            return;
        }

        if (_aggregator.Add(incoming, Now) is { } post)
            await SaveAsync(bot, post, ct);
    }

    private async Task SaveAsync(ITelegramBotClient bot, IncomingPost post, CancellationToken ct)
    {
        var folderPath = Path.Combine(storage.Value.MediaRoot, $"{post.ChatId}_{post.FirstMessageId}");
        try
        {
            if (string.IsNullOrWhiteSpace(post.Text) && post.PhotoFileIds.Count == 0)
            {
                logger.LogInformation("Skipping post {MessageId}: no text and no photos", post.FirstMessageId);
                return;
            }

            if (repository.Exists(post.ChatId, post.FirstMessageId))
                return;

            var folder = $"{post.ChatId}_{post.FirstMessageId}";
            Directory.CreateDirectory(Path.Combine(storage.Value.MediaRoot, folder));
            var files = new List<string>();
            for (var i = 0; i < post.PhotoFileIds.Count; i++)
            {
                var relativePath = $"{folder}/{i}.jpg";
                await DownloadAsync(bot, post.PhotoFileIds[i], Path.Combine(storage.Value.MediaRoot, relativePath), ct);
                files.Add(relativePath);
            }

            var id = repository.Insert(post, files, Now);
            logger.LogInformation("Saved post {Id} from Telegram with {Count} image(s)", id, files.Count);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            try
            {
                if (Directory.Exists(folderPath))
                    Directory.Delete(folderPath, recursive: true);
            }
            catch (Exception cleanupEx)
            {
                logger.LogWarning(cleanupEx, "Failed to remove partial media folder {Folder}", folderPath);
            }
            logger.LogError(ex, "Failed to save Telegram post {MessageId}", post.FirstMessageId);
        }
    }

    private async Task DownloadAsync(ITelegramBotClient bot, string fileId, string path, CancellationToken ct)
    {
        const int attempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var file = File.Create(path);
                await bot.GetInfoAndDownloadFile(fileId, file, ct);
                return;
            }
            catch (Exception ex) when (attempt < attempts && !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Photo download failed (attempt {Attempt}/{Max}); retrying", attempt, attempts);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }
}
