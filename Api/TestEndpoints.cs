using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using TeleThreads.Storage;
using TeleThreads.TelegramScanner;
using TeleThreads.Threads;

namespace TeleThreads.Api;

public sealed class TestingOptions
{
    public string Key { get; set; } = "";
}

public sealed record TestPostRequest(string? Text, IReadOnlyList<string>? ImageUrls);

// Pushes a post through the pipeline as if it came from Telegram. Disabled unless Testing:Key is set,
// because it spends Gemini quota and downloads arbitrary URLs.
public static class TestEndpoints
{
    public const long TestChatId = 0;

    public static void MapTestApi(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/test/posts", async (
            TestPostRequest body,
            HttpRequest request,
            IOptions<TestingOptions> testing,
            IOptions<StorageOptions> storage,
            IHttpClientFactory httpClients,
            PostRepository repository,
            TimeProvider time,
            CancellationToken ct) =>
        {
            if (!IsAuthorized(request, testing.Value.Key))
                return Results.NotFound();

            var imageUrls = body.ImageUrls ?? [];
            if (string.IsNullOrWhiteSpace(body.Text) && imageUrls.Count == 0)
                return Results.BadRequest("Нужен text и/или imageUrls");
            if (imageUrls.Count > ThreadsPublisher.MaxCarouselItems)
                return Results.BadRequest($"Не больше {ThreadsPublisher.MaxCarouselItems} картинок");

            var messageId = RandomNumberGenerator.GetInt32(1, int.MaxValue);
            var folder = $"test_{messageId}";
            var folderPath = Path.Combine(storage.Value.MediaRoot, folder);
            Directory.CreateDirectory(folderPath);
            var http = httpClients.CreateClient();
            var files = new List<string>();
            try
            {
                for (var i = 0; i < imageUrls.Count; i++)
                {
                    using var response = await http.GetAsync(imageUrls[i], ct);
                    if (!response.IsSuccessStatusCode)
                        throw new HttpRequestException($"{imageUrls[i]} вернул {(int)response.StatusCode}");

                    var extension = response.Content.Headers.ContentType?.MediaType == "image/png" ? "png" : "jpg";
                    var relativePath = $"{folder}/{i}.{extension}";
                    await using var file = File.Create(Path.Combine(storage.Value.MediaRoot, relativePath));
                    await response.Content.CopyToAsync(file, ct);
                    files.Add(relativePath);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
            {
                Directory.Delete(folderPath, recursive: true);
                return Results.BadRequest($"Не удалось скачать картинку: {ex.Message}");
            }

            var now = time.GetUtcNow().UtcDateTime;
            var id = repository.Insert(new IncomingPost(TestChatId, [messageId], null, now, body.Text, []), files, now);
            return Results.Created($"/api/posts/{id}", new { id, status = $"/api/posts/{id}" });
        });
    }

    private static bool IsAuthorized(HttpRequest request, string key) =>
        !string.IsNullOrEmpty(key)
        && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(request.Headers["X-Test-Key"].ToString()),
            Encoding.UTF8.GetBytes(key));
}
