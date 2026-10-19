using System.Net;
using Microsoft.Extensions.Options;
using TeleThreads.Rewriting;

namespace TeleThreads.Tests;

public sealed class GeminiRewriterTests : IDisposable
{
    private readonly string _promptPath = Path.GetTempFileName();

    public GeminiRewriterTests() => File.WriteAllText(_promptPath, "SYSTEM PROMPT");

    public void Dispose() => File.Delete(_promptPath);

    private GeminiRewriter Create(StubHttpHandler handler) =>
        new(new HttpClient(handler), Options.Create(new GeminiOptions
        {
            ApiKey = "key-1",
            Model = "test-model",
            SystemPromptPath = _promptPath,
        }));

    private static string Answer(string text) =>
        $$$"""{"candidates":[{"content":{"role":"model","parts":[{"text":{{{System.Text.Json.JsonSerializer.Serialize(text)}}}}]}}]}""";

    [Fact]
    public async Task RewriteAsync_SendsSystemPromptAndText()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json(Answer(" short version ")));

        var result = await Create(handler).RewriteAsync("long original", CancellationToken.None);

        Assert.Equal("short version", result);
        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal(
            "https://generativelanguage.googleapis.com/v1beta/models/test-model:generateContent",
            request.RequestUri!.ToString());
        Assert.Equal("key-1", request.Headers.GetValues("x-goog-api-key").Single());
        Assert.Contains("SYSTEM PROMPT", body);
        Assert.Contains("long original", body);
    }

    [Fact]
    public async Task RewriteAsync_TooLongAnswer_IsLimited()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json(Answer(new string('x', 800))));

        var result = await Create(handler).RewriteAsync("text", CancellationToken.None);

        Assert.True(result.Length <= TextLimiter.ThreadsMaxLength);
    }

    [Fact]
    public async Task RewriteAsync_IgnoresThoughtParts()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json(
            """{"candidates":[{"content":{"role":"model","parts":[{"text":"secret reasoning","thought":true},{"text":"final answer"}]}}]}"""));

        var result = await Create(handler).RewriteAsync("text", CancellationToken.None);

        Assert.Equal("final answer", result);
    }

    [Fact]
    public async Task RewriteAsync_ErrorStatus_Throws()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json("""{"error":"bad"}""", HttpStatusCode.BadRequest));

        await Assert.ThrowsAsync<HttpRequestException>(() => Create(handler).RewriteAsync("text", CancellationToken.None));
    }

    [Fact]
    public void ShippedPrompt_StatesThe500CharLimit()
    {
        var prompt = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "prompts", "system.txt"));

        Assert.Contains("500", prompt);
    }
}
