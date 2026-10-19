namespace TeleThreads.Storage;

public sealed record PostRecord(
    long Id,
    long TelegramChatId,
    IReadOnlyList<int> TelegramMessageIds,
    string? MediaGroupId,
    DateTime TelegramDate,
    string? OriginalText,
    IReadOnlyList<string> ImageFiles,
    string? RewrittenText,
    string Status,
    int Attempts,
    string? LastError,
    string? ThreadsPostId,
    DateTime ReceivedAt,
    DateTime? RewrittenAt,
    DateTime? PublishedAt,
    DateTime UpdatedAt)
{
    public int ImageCount => ImageFiles.Count;
}
