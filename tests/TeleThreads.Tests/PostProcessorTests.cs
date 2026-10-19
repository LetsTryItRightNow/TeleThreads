using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TeleThreads.Pipeline;
using TeleThreads.Rewriting;
using TeleThreads.Storage;
using TeleThreads.TelegramScanner;
using TeleThreads.Threads;

namespace TeleThreads.Tests;

public sealed class PostProcessorTests : IDisposable
{
    private readonly TempDataDir _data = new();
    private readonly ManualTimeProvider _time = new(new DateTime(2026, 10, 19, 10, 0, 0, DateTimeKind.Utc));
    private readonly FakeRewriter _rewriter = new();
    private readonly FakePublisher _publisher = new();

    public void Dispose() => _data.Dispose();

    private sealed class FakeRewriter : IPostRewriter
    {
        public bool Fail { get; set; }
        public int Calls { get; private set; }

        public Task<string> RewriteAsync(string text, CancellationToken ct)
        {
            Calls++;
            return Fail ? throw new InvalidOperationException("gemini down") : Task.FromResult("short: " + text);
        }
    }

    private sealed class FakePublisher : IThreadsPublisher
    {
        public List<ThreadsPost> Published { get; } = [];

        public Task<string> PublishAsync(ThreadsPost post, CancellationToken ct)
        {
            Published.Add(post);
            return Task.FromResult("threads-1");
        }
    }

    private PostProcessor Create(bool dryRun = true)
    {
        var scopes = new ServiceCollection()
            .AddSingleton<IPostRewriter>(_rewriter)
            .AddSingleton<IThreadsPublisher>(_publisher)
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
        return new PostProcessor(
            _data.Repository, scopes,
            Options.Create(new MediaOptions { PublicBaseUrl = "https://x" }),
            Options.Create(_data.Storage),
            Options.Create(new ThreadsOptions { DryRun = dryRun }),
            _time, NullLogger<PostProcessor>.Instance);
    }

    private int _nextMessageId = 1;

    private long Insert(string? text, params string[] files) =>
        _data.Repository.Insert(
            new IncomingPost(-100, [_nextMessageId++], null, _time.UtcNow, text, []), files, _time.UtcNow);

    [Fact]
    public async Task Post_IsRewrittenAndPublished()
    {
        var id = Insert("long text", "a/0.jpg", "a/1.jpg");

        await Create().ProcessPendingAsync(CancellationToken.None);

        var post = _data.Repository.Get(id)!;
        Assert.Equal(PostStatus.Published, post.Status);
        Assert.Equal("short: long text", post.RewrittenText);
        Assert.Equal("threads-1", post.ThreadsPostId);
        var published = Assert.Single(_publisher.Published);
        Assert.Equal("short: long text", published.Text);
        Assert.Equal(["https://x/media/a/0.jpg", "https://x/media/a/1.jpg"], published.ImageUrls);
    }

    [Fact]
    public async Task PostWithoutText_SkipsRewriterAndPublishesImagesOnly()
    {
        Insert(null, "a/0.jpg");

        await Create().ProcessPendingAsync(CancellationToken.None);

        Assert.Equal(0, _rewriter.Calls);
        Assert.Null(Assert.Single(_publisher.Published).Text);
    }

    [Fact]
    public async Task RewriterFailure_IsRecordedForRetry()
    {
        var id = Insert("text");
        _rewriter.Fail = true;

        await Create().ProcessPendingAsync(CancellationToken.None);

        var post = _data.Repository.Get(id)!;
        Assert.Equal(PostStatus.Received, post.Status);
        Assert.Equal(1, post.Attempts);
        Assert.Equal("gemini down", post.LastError);
        Assert.Empty(_publisher.Published);
    }

    [Fact]
    public async Task MediaOutsideMediaRoot_IsNotDeleted_AndPostStaysPublished()
    {
        var outside = Path.Combine(_data.Storage.MediaRoot, "..", "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "0.jpg"), "x");
        var id = Insert("text", "../outside/0.jpg");

        await Create(dryRun: false).ProcessPendingAsync(CancellationToken.None);

        Assert.True(Directory.Exists(outside));
        Assert.Equal(PostStatus.Published, _data.Repository.Get(id)!.Status);
        Assert.Single(_publisher.Published);
    }

    [Fact]
    public async Task RealPublish_DeletesMediaFolder_DryRunKeepsIt()
    {
        var folder = Path.Combine(_data.Storage.MediaRoot, "a");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "0.jpg"), "x");
        Insert("text", "a/0.jpg");

        await Create(dryRun: true).ProcessPendingAsync(CancellationToken.None);
        Assert.True(Directory.Exists(folder));

        Insert("text", "a/0.jpg");
        await Create(dryRun: false).ProcessPendingAsync(CancellationToken.None);
        Assert.False(Directory.Exists(folder));
    }
}
