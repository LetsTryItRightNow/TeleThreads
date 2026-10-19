using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TeleThreads.Threads;

// Meta's signed_request: base64url(HMAC-SHA256(payload part, app secret)) + "." + base64url(JSON payload).
public static class SignedRequest
{
    public static bool TryParse(string signedRequest, string appSecret, out JsonNode? payload)
    {
        payload = null;
        var parts = signedRequest.Split('.');
        if (parts.Length != 2 || appSecret.Length == 0)
            return false;

        try
        {
            var signature = Base64Url.DecodeFromChars(parts[0]);
            var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), Encoding.ASCII.GetBytes(parts[1]));
            if (!CryptographicOperations.FixedTimeEquals(signature, expected))
                return false;

            var json = JsonNode.Parse(Base64Url.DecodeFromChars(parts[1])) as JsonObject;
            if (json?["algorithm"] is not JsonValue algorithm
                || algorithm.GetValueKind() != JsonValueKind.String
                || !string.Equals(algorithm.GetValue<string>(), "HMAC-SHA256", StringComparison.OrdinalIgnoreCase))
                return false;

            payload = json;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            payload = null;
            return false;
        }
    }
}
