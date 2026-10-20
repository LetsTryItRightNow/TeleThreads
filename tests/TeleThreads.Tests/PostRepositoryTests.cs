using MongoDB.Driver;
using TeleThreads.Storage;
using TeleThreads.TelegramScanner;

namespace TeleThreads.Tests;

public sealed class PostRepositoryTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 19, 10, 0, 0, DateTimeKind.Utc);
    private readonly TempDataDir _data = new();
    private PostRepository Repo => _data.Repository;

    public void Dispose() => _data.Dispose();

    private static IncomingPost Post(int firstId, string? text = "hello") =>
        new(-100, [firstId, firstId + 1], "g1", Now.AddMinutes(-1), text, ["f1", "f2"]);

    [Fact]
    public void Insert_ThenGet_RoundTripsFields()
    {
        var id = Repo.Insert(Post(10), ["a/0.jpg", "a/1.jpg"], Now);

        var post = Repo.Get(id)!;
        Assert.Equal(-100, post.TelegramChatId);
        Assert.Equal([10, 11], post.TelegramMessageIds);
        Assert.Equal("g1", post.MediaGroupId);
        Assert.Equal(Now.AddMinutes(-1), post.TelegramDate);
        Assert.Equal("hello", post.OriginalText);
        Assert.Equal(["a/0.jpg", "a/1.jpg"], post.ImageFiles);
        Assert.Equal(2, post.ImageCount);
        Assert.Equal(PostStatus.Received, post.Status);
        Assert.Equal(Now, post.ReceivedAt);
        Assert.Null(post.RewrittenText);
    }

    [Fact]
    public void Exists_IsTrueOnlyAfterInsert()
    {
        Assert.False(Repo.Exists(-100, 10));
        Repo.Insert(Post(10), [], Now);
        Assert.True(Repo.Exists(-100, 10));
    }

    [Fact]
    public void ClaimNext_MovesPostToInProgressStatusOnce()
    {
        var id = Repo.Insert(Post(10), [], Now);

        var claimed = Repo.ClaimNext(PostStatus.Received, PostStatus.Rewriting, Now);

        Assert.Equal(id, claimed!.Id);
        Assert.Equal(PostStatus.Rewriting, claimed.Status);
        Assert.Null(Repo.ClaimNext(PostStatus.Received, PostStatus.Rewriting, Now));
    }

    [Fact]
    public void MarkRewrittenAndPublished_StoreStageResults()
    {
        var id = Repo.Insert(Post(10), [], Now);

        Repo.MarkRewritten(id, "short", Now.AddSeconds(1));
        Repo.MarkPublished(id, "dry-run", Now.AddSeconds(2));

        var post = Repo.Get(id)!;
        Assert.Equal("short", post.RewrittenText);
        Assert.Equal(Now.AddSeconds(1), post.RewrittenAt);
        Assert.Equal(PostStatus.Published, post.Status);
        Assert.Equal("dry-run", post.ThreadsPostId);
        Assert.Equal(Now.AddSeconds(2), post.PublishedAt);
    }

    [Fact]
    public void RecordFailure_RetriesAfterDelayThenFails()
    {
        var id = Repo.Insert(Post(10), [], Now);
        var delay = TimeSpan.FromSeconds(30);

        Repo.ClaimNext(PostStatus.Received, PostStatus.Rewriting, Now);
        Assert.Equal(PostStatus.Received, Repo.RecordFailure(id, PostStatus.Received, "boom", Now, 3, delay));
        Assert.Null(Repo.ClaimNext(PostStatus.Received, PostStatus.Rewriting, Now.AddSeconds(29)));

        Assert.NotNull(Repo.ClaimNext(PostStatus.Received, PostStatus.Rewriting, Now.AddSeconds(30)));
        Assert.Equal(PostStatus.Received, Repo.RecordFailure(id, PostStatus.Received, "boom", Now.AddSeconds(30), 3, delay));

        Assert.NotNull(Repo.ClaimNext(PostStatus.Received, PostStatus.Rewriting, Now.AddMinutes(5)));
        Assert.Equal(PostStatus.Failed, Repo.RecordFailure(id, PostStatus.Received, "boom", Now.AddMinutes(5), 3, delay));

        var post = Repo.Get(id)!;
        Assert.Equal(3, post.Attempts);
        Assert.Equal("boom", post.LastError);
    }

    [Fact]
    public void ResetInProgress_ReturnsStuckPostsToStableStatus()
    {
        var rewriting = Repo.Insert(Post(10), [], Now);
        var publishing = Repo.Insert(Post(20), [], Now);
        Repo.ClaimNext(PostStatus.Received, PostStatus.Rewriting, Now);
        Repo.MarkRewritten(publishing, "x", Now);
        Repo.ClaimNext(PostStatus.Rewritten, PostStatus.Publishing, Now);

        Repo.ResetInProgress(Now);

        Assert.Equal(PostStatus.Received, Repo.Get(rewriting)!.Status);
        Assert.Equal(PostStatus.Rewritten, Repo.Get(publishing)!.Status);
    }

    [Fact]
    public void List_FiltersByStatusNewestFirst()
    {
        var a = Repo.Insert(Post(10), [], Now);
        var b = Repo.Insert(Post(20), [], Now);
        var c = Repo.Insert(Post(30), [], Now);
        Repo.MarkRewritten(b, "x", Now);

        Assert.Equal([c, b, a], Repo.List(50, null).Select(p => p.Id));
        Assert.Equal([c, a], Repo.List(50, PostStatus.Received).Select(p => p.Id));
        Assert.Equal([c], Repo.List(1, null).Select(p => p.Id));
    }

    [Fact]
    public void Settings_AreUpserted()
    {
        Assert.Null(Repo.GetSetting("k"));
        Repo.SetSetting("k", "1");
        Repo.SetSetting("k", "2");
        Assert.Equal("2", Repo.GetSetting("k"));
    }

    [Fact]
    public void Settings_CanBeDeleted()
    {
        Repo.SetSetting("k", "1");
        Repo.DeleteSetting("k");
        Assert.Null(Repo.GetSetting("k"));
    }

    [Fact]
    public void Insert_AssignsIncreasingIds()
    {
        var a = Repo.Insert(Post(10), [], Now);
        var b = Repo.Insert(Post(20), [], Now);
        var c = Repo.Insert(Post(30), [], Now);

        Assert.True(a > 0);
        Assert.True(b > a);
        Assert.True(c > b);
    }

    [Fact]
    public void Insert_RejectsDuplicateTelegramPost()
    {
        Repo.Insert(Post(10), [], Now);

        Assert.Throws<MongoWriteException>(() => Repo.Insert(Post(10), [], Now));
        Assert.Single(Repo.List(50, null));
    }

    [Fact]
    public void Initialize_IsIdempotent()
    {
        var id = Repo.Insert(Post(10), [], Now);

        Repo.Initialize();

        Assert.NotNull(Repo.Get(id));
    }
}
