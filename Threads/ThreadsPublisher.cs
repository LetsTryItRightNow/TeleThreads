using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace TeleThreads.Threads;

// Publishing is two-step: create a media container, wait until Threads has processed it, then publish it.
public sealed class ThreadsPublisher(HttpClient http, IThreadsAccountProvider accounts, IOptions<ThreadsOptions> options)
    : IThreadsPublisher
{
    public const int MaxCarouselItems = 20;
    private const string BaseUrl = "https://graph.threads.net/v1.0/";
    private readonly ThreadsOptions _options = options.Value;

    public async Task<string> PublishAsync(ThreadsPost post, CancellationToken ct)
    {
        var account = await accounts.GetAccountAsync(ct);
        var images = post.ImageUrls.Take(MaxCarouselItems).ToList();

        var containerId = images.Count switch
        {
            0 => await CreateContainerAsync(account, new() { ["media_type"] = "TEXT", ["text"] = post.Text }, ct),
            1 => await CreateContainerAsync(
                account, new() { ["media_type"] = "IMAGE", ["image_url"] = images[0], ["text"] = post.Text }, ct),
            _ => await CreateCarouselAsync(account, images, post.Text, ct),
        };
        await WaitUntilFinishedAsync(account, containerId, ct);

        return await PostAsync($"{account.UserId}/threads_publish", account, new() { ["creation_id"] = containerId }, ct);
    }

    private async Task<string> CreateCarouselAsync(ThreadsAccount account, List<string> images, string? text, CancellationToken ct)
    {
        var children = new List<string>();
        foreach (var url in images)
        {
            var id = await CreateContainerAsync(
                account, new() { ["media_type"] = "IMAGE", ["image_url"] = url, ["is_carousel_item"] = "true" }, ct);
            await WaitUntilFinishedAsync(account, id, ct);
            children.Add(id);
        }
        return await CreateContainerAsync(
            account, new() { ["media_type"] = "CAROUSEL", ["children"] = string.Join(",", children), ["text"] = text }, ct);
    }

    private Task<string> CreateContainerAsync(ThreadsAccount account, Dictionary<string, string?> fields, CancellationToken ct) =>
        PostAsync($"{account.UserId}/threads", account, fields, ct);

    private async Task<string> PostAsync(string path, ThreadsAccount account, Dictionary<string, string?> fields, CancellationToken ct)
    {
        fields["access_token"] = account.AccessToken;
        using var content = new FormUrlEncodedContent(
            fields.Where(f => f.Value is not null).Select(f => KeyValuePair.Create(f.Key, f.Value!)));
        using var response = await http.PostAsync(BaseUrl + path, content, ct);
        var json = await ReadJsonAsync(response, ct);
        return json["id"]?.GetValue<string>() ?? throw new InvalidOperationException($"Threads response has no id: {json}");
    }

    private async Task WaitUntilFinishedAsync(ThreadsAccount account, string containerId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < _options.StatusPollAttempts; attempt++)
        {
            using var response = await http.GetAsync(
                $"{BaseUrl}{containerId}?fields=status,error_message&access_token={Uri.EscapeDataString(account.AccessToken)}", ct);
            var json = await ReadJsonAsync(response, ct);
            switch (json["status"]?.GetValue<string>())
            {
                case "FINISHED":
                    return;
                case "ERROR" or "EXPIRED":
                    throw new InvalidOperationException($"Threads container {containerId} failed: {json}");
            }
            await Task.Delay(_options.StatusPollInterval, ct);
        }
        throw new TimeoutException($"Threads container {containerId} was not processed in time");
    }

    private static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Threads returned {(int)response.StatusCode}: {body}");
        return JsonNode.Parse(body) ?? throw new InvalidOperationException("Threads returned an empty body");
    }
}
