namespace TeleThreads.Threads;

public sealed class ThreadsOptions
{
    public bool DryRun { get; set; } = true;
    public string UserId { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public TimeSpan StatusPollInterval { get; set; } = TimeSpan.FromSeconds(3);
    public int StatusPollAttempts { get; set; } = 20;
}
