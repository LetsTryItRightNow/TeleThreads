using TeleThreads.Rewriting;

namespace TeleThreads.Tests;

public class TextLimiterTests
{
    [Fact]
    public void ShortText_IsReturnedTrimmed()
    {
        Assert.Equal("hi", TextLimiter.Limit("  hi  "));
    }

    [Fact]
    public void LongText_IsCutAtLastSentenceEnd()
    {
        var text = new string('a', 300) + ". " + new string('b', 300);

        Assert.Equal(new string('a', 300) + ".", TextLimiter.Limit(text));
    }

    [Fact]
    public void LongText_WithoutSentenceEnd_IsCutAtWordWithEllipsis()
    {
        var text = string.Join(" ", Enumerable.Repeat("word", 200));

        var result = TextLimiter.Limit(text);

        Assert.True(result.Length <= TextLimiter.ThreadsMaxLength);
        Assert.EndsWith("word…", result);
    }

    [Fact]
    public void LongText_WithoutSpaces_IsHardCut()
    {
        var result = TextLimiter.Limit(new string('x', 600));

        Assert.Equal(TextLimiter.ThreadsMaxLength, result.Length);
        Assert.EndsWith("…", result);
    }
}
