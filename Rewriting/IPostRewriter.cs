namespace TeleThreads.Rewriting;

public interface IPostRewriter
{
    Task<string> RewriteAsync(string text, CancellationToken ct);
}
