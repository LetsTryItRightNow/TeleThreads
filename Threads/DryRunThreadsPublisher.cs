namespace TeleThreads.Threads;

// Used while Threads:DryRun is on: shows what would be published without calling Threads.
public sealed class DryRunThreadsPublisher(ILogger<DryRunThreadsPublisher> logger) : IThreadsPublisher
{
    public const string PostId = "dry-run";

    public Task<string> PublishAsync(ThreadsPost post, CancellationToken ct)
    {
        logger.LogInformation(
            "[dry-run] Would publish to Threads: {Text} with {Count} image(s): {Urls}",
            post.Text, post.ImageUrls.Count, string.Join(", ", post.ImageUrls));
        return Task.FromResult(PostId);
    }
}
