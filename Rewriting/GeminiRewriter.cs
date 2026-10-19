using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace TeleThreads.Rewriting;

public sealed class GeminiRewriter(HttpClient http, IOptions<GeminiOptions> options) : IPostRewriter
{
    private readonly GeminiOptions _options = options.Value;

    public async Task<string> RewriteAsync(string text, CancellationToken ct)
    {
        var systemPrompt = await File.ReadAllTextAsync(ResolvePromptPath(), ct);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://generativelanguage.googleapis.com/v1beta/models/{_options.Model}:generateContent")
        {
            Content = JsonContent.Create(new
            {
                systemInstruction = new { parts = new[] { new { text = systemPrompt } } },
                contents = new[] { new { role = "user", parts = new[] { new { text } } } },
            }),
        };
        request.Headers.Add("x-goog-api-key", _options.ApiKey);

        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Gemini returned {(int)response.StatusCode}: {body}");

        var parts = JsonNode.Parse(body)?["candidates"]?[0]?["content"]?["parts"]?.AsArray();
        var result = string.Concat(parts?.Select(p => p?["text"]?.GetValue<string>()) ?? Enumerable.Empty<string?>());
        if (string.IsNullOrWhiteSpace(result))
            throw new InvalidOperationException($"Gemini returned no text: {body}");

        return TextLimiter.Limit(result);
    }

    private string ResolvePromptPath() =>
        Path.IsPathRooted(_options.SystemPromptPath)
            ? _options.SystemPromptPath
            : Path.Combine(AppContext.BaseDirectory, _options.SystemPromptPath);
}
