using System.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TeleThreads.Storage;
using TeleThreads.Threads;

namespace TeleThreads.Tests;

public sealed class ThreadsConnectorTests : IDisposable
{
    private readonly TempDataDir _data = new();
    private readonly ManualTimeProvider _time = new(new DateTime(2026, 10, 20, 10, 0, 0, DateTimeKind.Utc));
    private readonly ThreadsAccountStore _store;
    private readonly OAuthStateStore _states;
    private readonly StubHttpHandler _handler = new(request => request.RequestUri!.AbsolutePath switch
    {
        "/oauth/access_token" => StubHttpHandler.Json("""{"access_token":"short","user_id":42}"""),
        "/access_token" => StubHttpHandler.Json("""{"access_token":"long","token_type":"bearer","expires_in":5183944}"""),
        "/v1.0/me" => StubHttpHandler.Json("""{"id":"42","username":"owner"}"""),
        _ => throw new InvalidOperationException($"Unexpected request {request.RequestUri}"),
    });

    public ThreadsConnectorTests()
    {
        _store = new ThreadsAccountStore(_data.Repository);
        _states = new OAuthStateStore(_time);
    }

    public void Dispose() => _data.Dispose();

    private ThreadsConnector Create() =>
        new(new ThreadsAuthClient(new HttpClient(_handler),
                Options.Create(new ThreadsOptions { AppId = "app-1", AppSecret = "s3cret" }),
                Options.Create(new MediaOptions { PublicBaseUrl = "https://tt.example.org" })),
            _states, _store, _time, NullLogger<ThreadsConnector>.Instance);

    [Fact]
    public async Task ValidCallback_SavesLongLivedAccount()
    {
        var account = await Create().ConnectAsync("the-code#_", _states.Create(), CancellationToken.None);

        Assert.Equal(new ThreadsAccount("42", "owner", "long"), account);
        Assert.Equal(account, _store.Get());
        Assert.Equal(_time.UtcNow, _store.CheckedAt);
        var exchange = _handler.Requests.Single(r => r.Request.RequestUri!.AbsolutePath == "/oauth/access_token");
        Assert.Equal("the-code", HttpUtility.ParseQueryString(exchange.Body!)["code"]);
        Assert.Contains("access_token=short", _handler.Requests
            .Single(r => r.Request.RequestUri!.AbsolutePath == "/access_token").Request.RequestUri!.Query);
        Assert.Contains("access_token=long", _handler.Requests
            .Single(r => r.Request.RequestUri!.AbsolutePath == "/v1.0/me").Request.RequestUri!.Query);
    }

    [Fact]
    public async Task UnknownState_ThrowsWithoutCallingThreads()
    {
        await Assert.ThrowsAsync<InvalidOAuthStateException>(
            () => Create().ConnectAsync("the-code", "forged", CancellationToken.None));

        Assert.Empty(_handler.Requests);
        Assert.Null(_store.Get());
    }

    [Fact]
    public async Task ReusedState_IsRejected()
    {
        var state = _states.Create();
        await Create().ConnectAsync("the-code", state, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOAuthStateException>(
            () => Create().ConnectAsync("the-code", state, CancellationToken.None));
    }
}
