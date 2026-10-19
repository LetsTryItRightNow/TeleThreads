namespace TeleThreads.TelegramScanner;

// A complete channel post: a single message or a whole album.
public sealed record IncomingPost(
    long ChatId,
    IReadOnlyList<int> MessageIds,
    string? MediaGroupId,
    DateTime Date,
    string? Text,
    IReadOnlyList<string> PhotoFileIds)
{
    public int FirstMessageId => MessageIds[0];
}
