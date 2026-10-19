using TeleThreads.Api;
using TeleThreads.Storage;

namespace TeleThreads.Tests;

public class PostDtoTests
{
    [Fact]
    public void From_MapsFieldsAndBuildsImageUrls()
    {
        var at = new DateTime(2026, 10, 19, 10, 0, 0, DateTimeKind.Utc);
        var record = new PostRecord(
            7, -100, [1, 2], "g", at, "orig", ["-100_1/0.jpg", "-100_1/1.jpg"], "short", PostStatus.Published,
            0, null, "dry-run", at, at, at, at);

        var dto = PostDto.From(record, new MediaOptions { PublicBaseUrl = "https://t.example/" });

        Assert.Equal(7, dto.Id);
        Assert.Equal(2, dto.ImageCount);
        Assert.Equal(["https://t.example/media/-100_1/0.jpg", "https://t.example/media/-100_1/1.jpg"], dto.ImageUrls);
        Assert.Equal("orig", dto.OriginalText);
        Assert.Equal("short", dto.RewrittenText);
        Assert.Equal(PostStatus.Published, dto.Status);
        Assert.Equal("dry-run", dto.ThreadsPostId);
    }
}
