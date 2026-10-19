namespace TeleThreads.Threads;

public interface IThreadsTokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken ct);
}
