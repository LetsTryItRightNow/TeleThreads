using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TeleThreads.Storage;
using TeleThreads.Threads;

namespace TeleThreads.Tests;

public sealed class ThreadsTokenServiceTests : IDisposable
{
    private readonly TempDataDir _data = new();
    private readonly ManualTimeProvider _time = new(new DateTime(2026, 10, 19, 10, 0, 0, DateTimeKind.Utc));
    private readonly ThreadsAccountStore _store;
    private readonly StubHttpHandler _handler;
    private HttpStatusCode _refreshStatus = HttpStatusCode.OK;

    public ThreadsTokenServiceTests()
    {
        _store = new ThreadsAccountStore(_data.Repository);
        _handler = new(_ => _refreshStatus == HttpStatusCode.OK
            ? StubHttpHandler.Json("""{"access_token":"refreshed","token_type":"bearer","expires_in":5183944}""")
            : StubHttpHandler.Json("""{"error":{"message":"nope"}}""", _refreshStatus));
    }

    public void Dispose() => _data.Dispose();

    private ThreadsTokenService Create() =>
        new(new HttpClient(_handler), _store, Options.Create(new MediaOptions { PublicBaseUrl = "https://example.org/" }),
            _time, NullLogger<ThreadsTokenService>.Instance);

    [Fact]
    public async Task NotConnected_ThrowsWithLoginLink()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Create().GetAccountAsync(CancellationToken.None));

        Assert.Contains("https://example.org/threads/login", ex.Message);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task FreshAccount_ReturnedWithoutRefresh()
    {
        _store.Save(new ThreadsAccount("42", "owner", "initial"), _time.UtcNow);

        var account = await Create().GetAccountAsync(CancellationToken.None);

        Assert.Equal(new ThreadsAccount("42", "owner", "initial"), account);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task AfterRefreshInterval_RefreshesAndStoresToken()
    {
        _store.Save(new ThreadsAccount("42", "owner", "initial"), _time.UtcNow);
        var service = Create();

        _time.UtcNow = _time.UtcNow.AddDays(8);
        Assert.Equal("refreshed", (await service.GetAccountAsync(CancellationToken.None)).AccessToken);
        Assert.Equal("refreshed", (await service.GetAccountAsync(CancellationToken.None)).AccessToken);

        var request = Assert.Single(_handler.Requests).Request;
        Assert.Equal("/refresh_access_token", request.RequestUri!.AbsolutePath);
        Assert.Contains("access_token=initial", request.RequestUri.Query);
        Assert.Equal("refreshed", _store.Get()!.AccessToken);
        Assert.Equal(_time.UtcNow, _store.CheckedAt);
    }

    [Fact]
    public async Task RefreshFailure_KeepsCurrentToken()
    {
        _store.Save(new ThreadsAccount("42", "owner", "initial"), _time.UtcNow);
        _refreshStatus = HttpStatusCode.BadRequest;

        _time.UtcNow = _time.UtcNow.AddDays(8);
        var account = await Create().GetAccountAsync(CancellationToken.None);

        Assert.Equal("initial", account.AccessToken);
        Assert.Equal("initial", _store.Get()!.AccessToken);
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task ReconnectDuringRefresh_KeepsNewAccount()
    {
        _store.Save(new ThreadsAccount("42", "owner", "initial"), _time.UtcNow);
        _time.UtcNow = _time.UtcNow.AddDays(8);
        var handler = new StubHttpHandler(_ =>
        {
            _store.Save(new ThreadsAccount("42", "owner", "reconnected"), _time.UtcNow);
            return StubHttpHandler.Json("""{"access_token":"refreshed"}""");
        });
        var service = new ThreadsTokenService(new HttpClient(handler), _store,
            Options.Create(new MediaOptions()), _time, NullLogger<ThreadsTokenService>.Instance);

        await service.GetAccountAsync(CancellationToken.None);

        Assert.Equal("reconnected", _store.Get()!.AccessToken);
    }

    [Fact]
    public void Clear_RemovesAccount()
    {
        _store.Save(new ThreadsAccount("42", "owner", "initial"), _time.UtcNow);

        _store.Clear();

        Assert.Null(_store.Get());
        Assert.Null(_store.CheckedAt);
    }
}
