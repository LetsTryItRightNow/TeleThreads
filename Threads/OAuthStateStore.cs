using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace TeleThreads.Threads;

// One-time OAuth state values, kept in memory: a restart only means opening the login link again.
public sealed class OAuthStateStore(TimeProvider time)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _expiresAt = new();

    public string Create()
    {
        var now = time.GetUtcNow();
        foreach (var (key, expiresAt) in _expiresAt)
        {
            if (expiresAt <= now)
                _expiresAt.TryRemove(key, out _);
        }

        var state = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        _expiresAt[state] = now + Lifetime;
        return state;
    }

    public bool Consume(string state) =>
        _expiresAt.TryRemove(state, out var expiresAt) && time.GetUtcNow() < expiresAt;
}
