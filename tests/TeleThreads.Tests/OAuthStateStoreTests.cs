using TeleThreads.Threads;

namespace TeleThreads.Tests;

public class OAuthStateStoreTests
{
    private readonly ManualTimeProvider _time = new(new DateTime(2026, 10, 20, 10, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void State_CanBeConsumedOnce()
    {
        var store = new OAuthStateStore(_time);
        var state = store.Create();

        Assert.True(store.Consume(state));
        Assert.False(store.Consume(state));
    }

    [Fact]
    public void States_AreUniqueAndUrlSafe()
    {
        var store = new OAuthStateStore(_time);
        var a = store.Create();
        var b = store.Create();

        Assert.NotEqual(a, b);
        Assert.Equal(Uri.EscapeDataString(a), a);
    }

    [Fact]
    public void State_ExpiresAfter15Minutes()
    {
        var store = new OAuthStateStore(_time);
        var fresh = store.Create();
        var stale = store.Create();

        _time.UtcNow = _time.UtcNow.AddMinutes(14);
        Assert.True(store.Consume(fresh));
        _time.UtcNow = _time.UtcNow.AddMinutes(2);
        Assert.False(store.Consume(stale));
    }

    [Fact]
    public void WhenFull_OldestStateIsEvicted()
    {
        var store = new OAuthStateStore(_time);
        var first = store.Create();
        var second = store.Create();
        for (var i = 2; i < OAuthStateStore.MaxStates; i++)
            store.Create();
        var newest = store.Create();

        Assert.False(store.Consume(first));
        Assert.True(store.Consume(second));
        Assert.True(store.Consume(newest));
    }

    [Fact]
    public void UnknownState_IsRejected()
    {
        Assert.False(new OAuthStateStore(_time).Consume("nope"));
    }
}
