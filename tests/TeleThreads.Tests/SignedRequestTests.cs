using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using TeleThreads.Threads;

namespace TeleThreads.Tests;

public class SignedRequestTests
{
    private const string Secret = "app-secret";

    private static string Sign(string json, string secret = Secret)
    {
        var payload = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));
        var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.ASCII.GetBytes(payload));
        return $"{Base64Url.EncodeToString(signature)}.{payload}";
    }

    [Fact]
    public void ValidSignature_ReturnsPayload()
    {
        var request = Sign("""{"algorithm":"HMAC-SHA256","user_id":"42","issued_at":1}""");

        Assert.True(SignedRequest.TryParse(request, Secret, out var payload));
        Assert.Equal("42", payload!["user_id"]!.GetValue<string>());
    }

    [Fact]
    public void TamperedPayload_IsRejected()
    {
        var signature = Sign("""{"user_id":"42"}""").Split('.')[0];
        var other = Base64Url.EncodeToString(Encoding.UTF8.GetBytes("""{"user_id":"43"}"""));

        Assert.False(SignedRequest.TryParse($"{signature}.{other}", Secret, out _));
    }

    [Fact]
    public void WrongSecret_IsRejected()
    {
        Assert.False(SignedRequest.TryParse(Sign("""{"user_id":"42"}""", "other"), Secret, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-dot")]
    [InlineData("a.b.c")]
    [InlineData("!!!.???")]
    public void Malformed_IsRejected(string request)
    {
        Assert.False(SignedRequest.TryParse(request, Secret, out _));
    }

    [Fact]
    public void ValidSignatureOverNonJson_IsRejected()
    {
        Assert.False(SignedRequest.TryParse(Sign("not json"), Secret, out _));
    }
}
