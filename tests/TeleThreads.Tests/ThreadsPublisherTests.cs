using System.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TeleThreads.Threads;

namespace TeleThreads.Tests;

public class ThreadsPublisherTests
{
    private sealed class FakeAccount : IThreadsAccountProvider
    {
        public Task<ThreadsAccount> GetAccountAsync(CancellationToken ct) =>
            Task.FromResult(new ThreadsAccount("42", "owner", "tok"));
    }

    private int _containers;

    private StubHttpHandler Handler(string status = "FINISHED") => new(request =>
        request.RequestUri!.AbsolutePath switch
        {
            "/v1.0/42/threads" => StubHttpHandler.Json($$"""{"id":"c{{++_containers}}"}"""),
            "/v1.0/42/threads_publish" => StubHttpHandler.Json("""{"id":"media-1"}"""),
            _ => StubHttpHandler.Json($$"""{"status":"{{status}}"}"""),
        });

    private static ThreadsPublisher Create(StubHttpHandler handler) =>
        new(new HttpClient(handler), new FakeAccount(), Options.Create(new ThreadsOptions
        {
            DryRun = false,
            StatusPollInterval = TimeSpan.Zero,
            StatusPollAttempts = 3,
        }));

    private static List<System.Collections.Specialized.NameValueCollection> Posts(StubHttpHandler handler, string path) =>
        handler.Requests
            .Where(r => r.Request.Method == HttpMethod.Post && r.Request.RequestUri!.AbsolutePath == path)
            .Select(r => HttpUtility.ParseQueryString(r.Body!))
            .ToList();

    [Fact]
    public async Task TextOnly_CreatesTextContainerAndPublishesIt()
    {
        var handler = Handler();

        var id = await Create(handler).PublishAsync(new ThreadsPost("hello world", []), CancellationToken.None);

        Assert.Equal("media-1", id);
        var container = Assert.Single(Posts(handler, "/v1.0/42/threads"));
        Assert.Equal("TEXT", container["media_type"]);
        Assert.Equal("hello world", container["text"]);
        Assert.Equal("tok", container["access_token"]);
        Assert.Equal("c1", Assert.Single(Posts(handler, "/v1.0/42/threads_publish"))["creation_id"]);
    }

    [Fact]
    public async Task SingleImage_CreatesImageContainer()
    {
        var handler = Handler();

        await Create(handler).PublishAsync(new ThreadsPost("t", ["https://x/media/a/0.jpg"]), CancellationToken.None);

        var container = Assert.Single(Posts(handler, "/v1.0/42/threads"));
        Assert.Equal("IMAGE", container["media_type"]);
        Assert.Equal("https://x/media/a/0.jpg", container["image_url"]);
        Assert.Equal("t", container["text"]);
    }

    [Fact]
    public async Task SeveralImages_CreateCarousel()
    {
        var handler = Handler();

        await Create(handler).PublishAsync(
            new ThreadsPost("t", ["https://x/0.jpg", "https://x/1.jpg", "https://x/2.jpg"]), CancellationToken.None);

        var containers = Posts(handler, "/v1.0/42/threads");
        Assert.Equal(4, containers.Count);
        Assert.All(containers.Take(3), c =>
        {
            Assert.Equal("IMAGE", c["media_type"]);
            Assert.Equal("true", c["is_carousel_item"]);
            Assert.Null(c["text"]);
        });
        Assert.Equal("CAROUSEL", containers[3]["media_type"]);
        Assert.Equal("c1,c2,c3", containers[3]["children"]);
        Assert.Equal("t", containers[3]["text"]);
        Assert.Equal("c4", Assert.Single(Posts(handler, "/v1.0/42/threads_publish"))["creation_id"]);
    }

    [Fact]
    public async Task ContainerError_Throws()
    {
        var handler = Handler(status: "ERROR");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Create(handler).PublishAsync(new ThreadsPost("t", []), CancellationToken.None));
        Assert.Empty(Posts(handler, "/v1.0/42/threads_publish"));
    }

    [Fact]
    public async Task ContainerNeverFinishes_TimesOut()
    {
        var handler = Handler(status: "IN_PROGRESS");

        await Assert.ThrowsAsync<TimeoutException>(
            () => Create(handler).PublishAsync(new ThreadsPost("t", []), CancellationToken.None));
    }

    [Fact]
    public async Task DryRun_ReturnsMarkerWithoutHttp()
    {
        var publisher = new DryRunThreadsPublisher(NullLogger<DryRunThreadsPublisher>.Instance);

        var id = await publisher.PublishAsync(new ThreadsPost("t", ["https://x/0.jpg"]), CancellationToken.None);

        Assert.Equal(DryRunThreadsPublisher.PostId, id);
    }
}
