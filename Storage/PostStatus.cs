namespace TeleThreads.Storage;

// Received → Rewriting → Rewritten → Publishing → Published, or Failed after too many attempts.
public static class PostStatus
{
    public const string Received = "Received";
    public const string Rewriting = "Rewriting";
    public const string Rewritten = "Rewritten";
    public const string Publishing = "Publishing";
    public const string Published = "Published";
    public const string Failed = "Failed";
}
