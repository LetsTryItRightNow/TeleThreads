using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using TeleThreads.Storage;

namespace TeleThreads.Threads;

// Long-lived Threads tokens expire after 60 days; refreshing one regularly keeps it alive.
public sealed class ThreadsTokenService(
    HttpClient http,
    ThreadsAccountStore store,
    IOptions<MediaOptions> media,
    TimeProvider time,
    ILogger<ThreadsTokenService> logger) : IThreadsAccountProvider
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromDays(7);

    public async Task<ThreadsAccount> GetAccountAsync(CancellationToken ct)
    {
        var account = store.Get() ?? throw new InvalidOperationException(
            $"Threads account is not connected: open {media.Value.PublicBaseUrl.TrimEnd('/')}/threads/login");

        var now = time.GetUtcNow().UtcDateTime;
        if (now - (store.CheckedAt ?? DateTime.MinValue) < RefreshInterval)
            return account;

        try
        {
            using var response = await http.GetAsync(
                "https://graph.threads.net/refresh_access_token?grant_type=th_refresh_token&access_token="
                + Uri.EscapeDataString(account.AccessToken), ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Threads token refresh returned {(int)response.StatusCode}: {body}");

            var refreshed = JsonNode.Parse(body)?["access_token"]?.GetValue<string>()
                ?? throw new InvalidOperationException("Threads token refresh returned no token");
            store.UpdateToken(refreshed, now);
            logger.LogInformation("Threads access token refreshed");
            return account with { AccessToken = refreshed };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Don't retry on every publish: the current token is still valid for weeks.
            store.UpdateToken(account.AccessToken, now);
            logger.LogWarning(ex, "Threads token refresh failed; using the current token");
            return account;
        }
    }
}
