namespace TeleThreads.TelegramScanner;

public sealed class TelegramOptions
{
    public string BotToken { get; set; } = "";
    public long ChannelId { get; set; }
}
