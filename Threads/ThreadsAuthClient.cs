using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using TeleThreads.Storage;

namespace TeleThreads.Threads;

// OAuth endpoints used once, when the account owner connects their Threads account.
public sealed class ThreadsAuthClient(HttpClient http, IOptions<ThreadsOptions> options, IOptions<MediaOptions> media)
{
    private readonly ThreadsOptions _options = options.Value;

    public string RedirectUri { get; } = $"{media.Value.PublicBaseUrl.TrimEnd('/')}/threads/callback";

    public string BuildAuthorizeUrl(string state) =>
        "https://threads.net/oauth/authorize"
        + $"?client_id={Uri.EscapeDataString(_options.AppId)}"
        + $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}"
        + "&scope=threads_basic,threads_content_publish"
        + "&response_type=code"
        + $"&state={Uri.EscapeDataString(state)}";

    // Returns a short-lived token (valid for about an hour).
    public async Task<(string AccessToken, string UserId)> ExchangeCodeAsync(string code, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _options.AppId,
            ["client_secret"] = _options.AppSecret,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = RedirectUri,
            ["code"] = code,
        });
        using var response = await http.PostAsync("https://graph.threads.net/oauth/access_token", content, ct);
        var json = await ReadJsonAsync(response, "code exchange", ct);
        return (Required(json, "access_token"), Required(json, "user_id"));
    }

    // Returns a token valid for 60 days.
    public async Task<string> ExchangeForLongLivedAsync(string shortToken, CancellationToken ct)
    {
        using var response = await http.GetAsync(
            "https://graph.threads.net/access_token?grant_type=th_exchange_token"
            + $"&client_secret={Uri.EscapeDataString(_options.AppSecret)}"
            + $"&access_token={Uri.EscapeDataString(shortToken)}", ct);
        return Required(await ReadJsonAsync(response, "long-lived token exchange", ct), "access_token");
    }

    public async Task<(string Id, string Username)> GetProfileAsync(string token, CancellationToken ct)
    {
        using var response = await http.GetAsync(
            $"https://graph.threads.net/v1.0/me?fields=id,username&access_token={Uri.EscapeDataString(token)}", ct);
        var json = await ReadJsonAsync(response, "profile request", ct);
        return (Required(json, "id"), Required(json, "username"));
    }

    private static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response, string step, CancellationToken ct)
    {
        // Only the status and body go into the message: the request URL carries the secret and tokens.
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Threads {step} returned {(int)response.StatusCode}: {body}");
        return JsonNode.Parse(body) ?? throw new InvalidOperationException($"Threads {step} returned an empty body");
    }

    // user_id comes back as a JSON number; its raw text keeps all digits.
    private static string Required(JsonNode json, string name) =>
        json[name] switch
        {
            JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
            JsonValue value when value.GetValueKind() == JsonValueKind.Number => value.ToJsonString(),
            _ => throw new InvalidOperationException($"Threads response has no {name}"),
        };
}
