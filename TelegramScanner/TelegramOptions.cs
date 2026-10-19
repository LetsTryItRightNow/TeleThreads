namespace TeleThreads.TelegramScanner;

public sealed class TelegramOptions
{
    public string BotToken { get; set; } = "";
    // Empty until known: posts from every channel are then ignored and their ids logged.
    public long? ChannelId { get; set; }
}
