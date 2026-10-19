namespace TeleThreads.Threads;

public interface IThreadsPublisher
{
    // Returns the id of the published Threads post.
    Task<string> PublishAsync(ThreadsPost post, CancellationToken ct);
}
