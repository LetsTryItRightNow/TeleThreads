using TeleThreads.TelegramScanner;

namespace TeleThreads.Tests;

public class MediaGroupAggregatorTests
{
    private static readonly DateTime T0 = new(2026, 10, 19, 10, 0, 0, DateTimeKind.Utc);
    private readonly MediaGroupAggregator _aggregator = new(TimeSpan.FromSeconds(3));

    private static IncomingMessage Message(int id, string? group, string? text = null, string? photo = null) =>
        new(-100, id, group, T0, text, photo);

    [Fact]
    public void StandaloneMessage_IsReturnedImmediately()
    {
        var post = _aggregator.Add(Message(1, null, "hello", "p1"), T0);

        Assert.NotNull(post);
        Assert.Equal([1], post.MessageIds);
        Assert.Equal("hello", post.Text);
        Assert.Equal(["p1"], post.PhotoFileIds);
        Assert.False(_aggregator.HasPending);
    }

    [Fact]
    public void AlbumParts_AreHeldUntilQuietPeriodPasses()
    {
        Assert.Null(_aggregator.Add(Message(12, "g", photo: "p2"), T0));
        Assert.Null(_aggregator.Add(Message(11, "g", "caption", "p1"), T0.AddSeconds(1)));
        Assert.Null(_aggregator.Add(Message(13, "g", photo: "p3"), T0.AddSeconds(2)));

        Assert.Empty(_aggregator.TakeCompleted(T0.AddSeconds(4)));
        var post = Assert.Single(_aggregator.TakeCompleted(T0.AddSeconds(5)));

        Assert.Equal([11, 12, 13], post.MessageIds);
        Assert.Equal(11, post.FirstMessageId);
        Assert.Equal(["p1", "p2", "p3"], post.PhotoFileIds);
        Assert.Equal("caption", post.Text);
        Assert.Equal("g", post.MediaGroupId);
        Assert.False(_aggregator.HasPending);
    }

    [Fact]
    public void TwoAlbums_CompleteIndependently()
    {
        _aggregator.Add(Message(1, "a", photo: "a1"), T0);
        _aggregator.Add(Message(5, "b", photo: "b1"), T0.AddSeconds(2));

        var first = Assert.Single(_aggregator.TakeCompleted(T0.AddSeconds(3)));
        Assert.Equal("a", first.MediaGroupId);
        Assert.True(_aggregator.HasPending);

        var second = Assert.Single(_aggregator.TakeCompleted(T0.AddSeconds(5)));
        Assert.Equal("b", second.MediaGroupId);
    }
}
