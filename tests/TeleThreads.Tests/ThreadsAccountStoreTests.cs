using TeleThreads.Threads;

namespace TeleThreads.Tests;

public sealed class ThreadsAccountStoreTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 20, 10, 0, 0, DateTimeKind.Utc);
    private readonly TempDataDir _data = new();
    private readonly ThreadsAccountStore _store;

    public ThreadsAccountStoreTests()
    {
        _store = new ThreadsAccountStore(_data.Repository);
        _store.Save(new ThreadsAccount("42", "owner", "tok"), Now);
    }

    public void Dispose() => _data.Dispose();

    [Fact]
    public void ClearIfUser_MatchingId_Clears()
    {
        Assert.True(_store.ClearIfUser("42"));
        Assert.Null(_store.Get());
    }

    [Fact]
    public void ClearIfUser_DifferentId_KeepsAccount()
    {
        Assert.False(_store.ClearIfUser("43"));
        Assert.Equal("42", _store.Get()!.UserId);
    }

    [Fact]
    public void Clear_RemovesLegacySeedToken()
    {
        _data.Repository.SetSetting("threads_seed_token", "old");

        _store.Clear();

        Assert.Null(_data.Repository.GetSetting("threads_seed_token"));
    }

    [Fact]
    public void UpdateToken_ReplacesMatchingToken()
    {
        _store.UpdateToken("tok", "new", Now.AddDays(8));

        Assert.Equal("new", _store.Get()!.AccessToken);
        Assert.Equal(Now.AddDays(8), _store.CheckedAt);
    }

    [Fact]
    public void UpdateToken_IgnoresTokenReplacedMeanwhile()
    {
        _store.Save(new ThreadsAccount("42", "owner", "reconnected"), Now.AddDays(1));

        _store.UpdateToken("tok", "refreshed", Now.AddDays(8));

        Assert.Equal("reconnected", _store.Get()!.AccessToken);
        Assert.Equal(Now.AddDays(1), _store.CheckedAt);
    }

    [Fact]
    public void UpdateToken_DoesNotRecreateClearedAccount()
    {
        _store.Clear();

        _store.UpdateToken("tok", "refreshed", Now.AddDays(8));

        Assert.Null(_store.Get());
        Assert.Null(_data.Repository.GetSetting("threads_access_token"));
        Assert.Null(_store.CheckedAt);
    }
}
