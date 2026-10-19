using TeleThreads.Storage;

namespace TeleThreads.Api;

public sealed record PostDto(
    long Id,
    IReadOnlyList<int> TelegramMessageIds,
    DateTime TelegramDate,
    int ImageCount,
    IReadOnlyList<string> ImageUrls,
    string? OriginalText,
    string? RewrittenText,
    string Status,
    int Attempts,
    string? LastError,
    string? ThreadsPostId,
    DateTime ReceivedAt,
    DateTime? RewrittenAt,
    DateTime? PublishedAt)
{
    public static PostDto From(PostRecord post, MediaOptions media) => new(
        post.Id,
        post.TelegramMessageIds,
        post.TelegramDate,
        post.ImageCount,
        post.ImageFiles.Select(media.UrlFor).ToList(),
        post.OriginalText,
        post.RewrittenText,
        post.Status,
        post.Attempts,
        post.LastError,
        post.ThreadsPostId,
        post.ReceivedAt,
        post.RewrittenAt,
        post.PublishedAt);
}
