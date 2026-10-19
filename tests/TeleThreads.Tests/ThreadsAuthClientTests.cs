using System.Net;
using System.Web;
using Microsoft.Extensions.Options;
using TeleThreads.Storage;
using TeleThreads.Threads;

namespace TeleThreads.Tests;

public class ThreadsAuthClientTests
{
    private static ThreadsAuthClient Create(StubHttpHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new ThreadsOptions { AppId = "app-1", AppSecret = "s3cret" }),
            Options.Create(new MediaOptions { PublicBaseUrl = "https://tt.example.org/" }));

    [Fact]
    public void AuthorizeUrl_HasAllParameters()
    {
        var client = Create(new StubHttpHandler(_ => throw new InvalidOperationException()));

        var url = new Uri(client.BuildAuthorizeUrl("st-1"));

        Assert.Equal("https://threads.net/oauth/authorize", url.GetLeftPart(UriPartial.Path));
        Assert.Contains("redirect_uri=https%3A%2F%2Ftt.example.org%2Fthreads%2Fcallback", url.Query);
        var query = HttpUtility.ParseQueryString(url.Query);
        Assert.Equal("app-1", query["client_id"]);
        Assert.Equal("https://tt.example.org/threads/callback", query["redirect_uri"]);
        Assert.Equal("threads_basic,threads_content_publish", query["scope"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("st-1", query["state"]);
        Assert.Equal("https://tt.example.org/threads/callback", client.RedirectUri);
    }

    [Fact]
    public async Task ExchangeCode_PostsFormAndParsesNumericUserId()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json("""{"access_token":"short","user_id":17841400000000001}"""));

        var (token, userId) = await Create(handler).ExchangeCodeAsync("the-code", CancellationToken.None);

        Assert.Equal("short", token);
        Assert.Equal("17841400000000001", userId);
        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://graph.threads.net/oauth/access_token", request.RequestUri!.ToString());
        var form = HttpUtility.ParseQueryString(body!);
        Assert.Equal("app-1", form["client_id"]);
        Assert.Equal("s3cret", form["client_secret"]);
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal("https://tt.example.org/threads/callback", form["redirect_uri"]);
        Assert.Equal("the-code", form["code"]);
    }

    [Fact]
    public async Task ExchangeCode_AcceptsStringUserId()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json("""{"access_token":"short","user_id":"42"}"""));

        var (_, userId) = await Create(handler).ExchangeCodeAsync("c", CancellationToken.None);

        Assert.Equal("42", userId);
    }

    [Fact]
    public async Task ExchangeForLongLived_SendsSecretAndToken()
    {
        var handler = new StubHttpHandler(_ =>
            StubHttpHandler.Json("""{"access_token":"long","token_type":"bearer","expires_in":5183944}"""));

        Assert.Equal("long", await Create(handler).ExchangeForLongLivedAsync("short", CancellationToken.None));

        var request = Assert.Single(handler.Requests).Request;
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://graph.threads.net/access_token", request.RequestUri!.GetLeftPart(UriPartial.Path));
        var query = HttpUtility.ParseQueryString(request.RequestUri.Query);
        Assert.Equal("th_exchange_token", query["grant_type"]);
        Assert.Equal("s3cret", query["client_secret"]);
        Assert.Equal("short", query["access_token"]);
    }

    [Fact]
    public async Task GetProfile_ParsesIdAndUsername()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json("""{"id":"42","username":"owner"}"""));

        var (id, username) = await Create(handler).GetProfileAsync("long", CancellationToken.None);

        Assert.Equal(("42", "owner"), (id, username));
        var request = Assert.Single(handler.Requests).Request;
        Assert.Equal("https://graph.threads.net/v1.0/me", request.RequestUri!.GetLeftPart(UriPartial.Path));
        var query = HttpUtility.ParseQueryString(request.RequestUri.Query);
        Assert.Equal("id,username", query["fields"]);
        Assert.Equal("long", query["access_token"]);
    }

    [Fact]
    public async Task ErrorResponse_ThrowsWithoutLeakingSecret()
    {
        var handler = new StubHttpHandler(_ =>
            StubHttpHandler.Json("""{"error":{"message":"Invalid code"}}""", HttpStatusCode.BadRequest));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => Create(handler).ExchangeCodeAsync("c", CancellationToken.None));

        Assert.Contains("400", ex.Message);
        Assert.Contains("Invalid code", ex.Message);
        Assert.DoesNotContain("s3cret", ex.Message);
    }
}
