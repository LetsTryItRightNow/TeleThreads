using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using TeleThreads.Storage;

namespace TeleThreads.Threads;

// Long-lived Threads tokens expire after 60 days; refreshing one regularly keeps it alive.
public sealed class ThreadsTokenService(
    HttpClient http,
    PostRepository repository,
    IOptions<ThreadsOptions> options,
    TimeProvider time,
    ILogger<ThreadsTokenService> logger) : IThreadsTokenProvider
{
    private const string TokenKey = "threads_access_token";
    private const string SeedKey = "threads_seed_token";
    private const string CheckedAtKey = "threads_token_checked_at";
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromDays(7);

    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var configured = options.Value.AccessToken;

        // A token put into config by hand (e.g. after re-issuing it) replaces the stored one.
        if (repository.GetSetting(SeedKey) != configured)
        {
            repository.SetSetting(SeedKey, configured);
            repository.SetSetting(TokenKey, configured);
            repository.SetSetting(CheckedAtKey, Format(now));
        }

        var token = repository.GetSetting(TokenKey)!;
        var checkedAt = repository.GetSetting(CheckedAtKey) is { } value
            ? DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            : DateTime.MinValue;
        if (now - checkedAt < RefreshInterval)
            return token;

        repository.SetSetting(CheckedAtKey, Format(now));
        try
        {
            using var response = await http.GetAsync(
                "https://graph.threads.net/refresh_access_token?grant_type=th_refresh_token&access_token="
                + Uri.EscapeDataString(token), ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Threads token refresh returned {(int)response.StatusCode}: {body}");

            var refreshed = JsonNode.Parse(body)?["access_token"]?.GetValue<string>()
                ?? throw new InvalidOperationException("Threads token refresh returned no token");
            repository.SetSetting(TokenKey, refreshed);
            logger.LogInformation("Threads access token refreshed");
            return refreshed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Threads token refresh failed; using the current token");
            return token;
        }
    }

    private static string Format(DateTime value) => value.ToString("O", CultureInfo.InvariantCulture);
}
