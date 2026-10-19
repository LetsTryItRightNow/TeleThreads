using Microsoft.Extensions.Options;
using TeleThreads.Rewriting;
using TeleThreads.Storage;
using TeleThreads.Threads;

namespace TeleThreads.Pipeline;

// Moves posts through the stages: Received → (Gemini) → Rewritten → (Threads) → Published.
public sealed class PostProcessor(
    PostRepository repository,
    IServiceScopeFactory scopes,
    IOptions<MediaOptions> media,
    IOptions<StorageOptions> storage,
    IOptions<ThreadsOptions> threads,
    TimeProvider time,
    ILogger<PostProcessor> logger) : BackgroundService
{
    public const int MaxAttempts = 3;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Post processing loop failed");
            }
            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    public async Task ProcessPendingAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var rewriter = scope.ServiceProvider.GetRequiredService<IPostRewriter>();
        var publisher = scope.ServiceProvider.GetRequiredService<IThreadsPublisher>();

        while (repository.ClaimNext(PostStatus.Received, PostStatus.Rewriting, Now) is { } post)
            await RunStageAsync(post, PostStatus.Received, () => RewriteAsync(post, rewriter, ct), ct);

        while (repository.ClaimNext(PostStatus.Rewritten, PostStatus.Publishing, Now) is { } post)
            await RunStageAsync(post, PostStatus.Rewritten, () => PublishAsync(post, publisher, ct), ct);
    }

    private async Task RunStageAsync(PostRecord post, string retryStatus, Func<Task> stage, CancellationToken ct)
    {
        try
        {
            await stage();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var delay = post.Attempts == 0 ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(2);
            var status = repository.RecordFailure(post.Id, retryStatus, ex.Message, Now, MaxAttempts, delay);
            logger.LogError(ex, "Post {Id} failed while {Stage}; status is now {Status}", post.Id, post.Status, status);
        }
    }

    private async Task RewriteAsync(PostRecord post, IPostRewriter rewriter, CancellationToken ct)
    {
        var text = string.IsNullOrWhiteSpace(post.OriginalText)
            ? ""
            : await rewriter.RewriteAsync(post.OriginalText, ct);
        repository.MarkRewritten(post.Id, text, Now);
        logger.LogInformation("Post {Id} rewritten: {Length} chars", post.Id, text.Length);
    }

    private async Task PublishAsync(PostRecord post, IThreadsPublisher publisher, CancellationToken ct)
    {
        var threadsPost = new ThreadsPost(
            string.IsNullOrEmpty(post.RewrittenText) ? null : post.RewrittenText,
            post.ImageFiles.Select(media.Value.UrlFor).ToList());
        // A crash between the Threads publish and MarkPublished can lead to a re-publish after restart (accepted for now).
        var threadsId = await publisher.PublishAsync(threadsPost, ct);
        repository.MarkPublished(post.Id, threadsId, Now);
        logger.LogInformation("Post {Id} published to Threads as {ThreadsId}", post.Id, threadsId);

        // In dry-run the photos stay, so their public URLs can be checked by hand.
        if (!threads.Value.DryRun)
        {
            try
            {
                DeleteMedia(post);
            }
            catch (Exception ex)
            {
                // The post is already published; a cleanup failure must not change its status.
                logger.LogWarning(ex, "Post {Id}: failed to delete media", post.Id);
            }
        }
    }

    private void DeleteMedia(PostRecord post)
    {
        var root = Path.GetFullPath(storage.Value.MediaRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var folder in post.ImageFiles.Select(Path.GetDirectoryName).Distinct())
        {
            if (string.IsNullOrEmpty(folder))
                continue;
            var path = Path.GetFullPath(Path.Combine(storage.Value.MediaRoot, folder));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                continue;
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
    }
}
