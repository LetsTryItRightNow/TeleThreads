namespace TeleThreads.Rewriting;

public sealed class GeminiOptions
{
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gemini-3.5-flash";
    public string SystemPromptPath { get; set; } = "prompts/system.txt";
}
