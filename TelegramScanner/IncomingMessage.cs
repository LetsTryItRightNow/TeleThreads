namespace TeleThreads.TelegramScanner;

// One channel message, reduced to what the pipeline needs.
public sealed record IncomingMessage(
    long ChatId,
    int MessageId,
    string? MediaGroupId,
    DateTime Date,
    string? Text,
    string? PhotoFileId);
