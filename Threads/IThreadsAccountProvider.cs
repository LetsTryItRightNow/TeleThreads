namespace TeleThreads.Threads;

public interface IThreadsAccountProvider
{
    // Throws when no account has been connected via /threads/login yet.
    Task<ThreadsAccount> GetAccountAsync(CancellationToken ct);
}
