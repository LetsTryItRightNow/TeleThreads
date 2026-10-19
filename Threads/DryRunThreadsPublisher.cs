namespace TeleThreads.Threads;

// Used while Threads:DryRun is on: shows what would be published without calling Threads.
public sealed class DryRunThreadsPublisher(ILogger<DryRunThreadsPublisher> logger) : IThreadsPublisher
{
    public const string PostId = "dry-run";

    public Task<string> PublishAsync(ThreadsPost post, CancellationToken ct)
    {
        // Mirror ThreadsPublisher: the type depends on the image count and extra images are dropped.
        var images = post.ImageUrls.Take(ThreadsPublisher.MaxCarouselItems).ToList();
        var type = images.Count switch { 0 => "TEXT", 1 => "IMAGE", _ => "CAROUSEL" };
        logger.LogInformation(
            "[dry-run] Would publish to Threads as {Type}: {Text} with {Count} image(s): {Urls}",
            type, post.Text, images.Count, string.Join(", ", images));
        return Task.FromResult(PostId);
    }
}
