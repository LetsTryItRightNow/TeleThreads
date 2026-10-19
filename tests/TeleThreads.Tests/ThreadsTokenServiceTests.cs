using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TeleThreads.Threads;

namespace TeleThreads.Tests;

public sealed class ThreadsTokenServiceTests : IDisposable
{
    private readonly TempDataDir _data = new();
    private readonly ManualTimeProvider _time = new(new DateTime(2026, 10, 19, 10, 0, 0, DateTimeKind.Utc));
    private readonly StubHttpHandler _handler =
        new(_ => StubHttpHandler.Json("""{"access_token":"refreshed","token_type":"bearer","expires_in":5183944}"""));

    public void Dispose() => _data.Dispose();

    private ThreadsTokenService Create(string configuredToken) =>
        new(new HttpClient(_handler), _data.Repository,
            Options.Create(new ThreadsOptions { AccessToken = configuredToken }),
            _time, NullLogger<ThreadsTokenService>.Instance);

    [Fact]
    public async Task FirstUse_ReturnsConfiguredTokenWithoutRefresh()
    {
        Assert.Equal("initial", await Create("initial").GetAccessTokenAsync(CancellationToken.None));
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task AfterRefreshInterval_RefreshesAndStoresToken()
    {
        var service = Create("initial");
        await service.GetAccessTokenAsync(CancellationToken.None);

        _time.UtcNow = _time.UtcNow.AddDays(8);
        Assert.Equal("refreshed", await service.GetAccessTokenAsync(CancellationToken.None));
        Assert.Equal("refreshed", await service.GetAccessTokenAsync(CancellationToken.None));

        var request = Assert.Single(_handler.Requests).Request;
        Assert.Equal("/refresh_access_token", request.RequestUri!.AbsolutePath);
        Assert.Contains("access_token=initial", request.RequestUri.Query);
    }

    [Fact]
    public async Task NewConfiguredToken_ReplacesStoredOne()
    {
        await Create("old").GetAccessTokenAsync(CancellationToken.None);

        Assert.Equal("new", await Create("new").GetAccessTokenAsync(CancellationToken.None));
    }
}
