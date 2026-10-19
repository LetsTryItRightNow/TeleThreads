namespace TeleThreads.Threads;

public sealed class ThreadsOptions
{
    public bool DryRun { get; set; } = true;
    // Threads app id/secret from the Meta app's Threads use case settings (used for OAuth).
    public string AppId { get; set; } = "";
    public string AppSecret { get; set; } = "";
    public TimeSpan StatusPollInterval { get; set; } = TimeSpan.FromSeconds(3);
    public int StatusPollAttempts { get; set; } = 20;
}
