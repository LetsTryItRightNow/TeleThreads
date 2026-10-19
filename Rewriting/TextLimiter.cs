namespace TeleThreads.Rewriting;

public static class TextLimiter
{
    public const int ThreadsMaxLength = 500;

    // Gemini is asked to stay within the limit; this is the safety net if it doesn't.
    public static string Limit(string text, int maxLength = ThreadsMaxLength)
    {
        text = text.Trim();
        if (text.Length <= maxLength)
            return text;

        var cut = text[..maxLength];
        var sentenceEnd = cut.LastIndexOfAny(['.', '!', '?', '…']);
        if (sentenceEnd >= maxLength / 2)
            return cut[..(sentenceEnd + 1)];

        // Leave room for the ellipsis.
        var space = cut.LastIndexOf(' ', maxLength - 2);
        if (space > 0)
            return cut[..space].TrimEnd() + "…";

        var end = maxLength - 1;
        if (char.IsLowSurrogate(text[end]))
            end--;
        return text[..end] + "…";
    }
}
