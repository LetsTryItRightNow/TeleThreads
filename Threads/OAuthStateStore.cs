using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace TeleThreads.Threads;

// One-time OAuth state values, kept in memory: a restart only means opening the login link again.
public sealed class OAuthStateStore(TimeProvider time)
{
    // /threads/login is public, so the number of pending states is capped to keep memory bounded.
    public const int MaxStates = 100;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<string, (DateTimeOffset ExpiresAt, long Order)> _states = new();
    private readonly Lock _createLock = new();
    private long _created;

    public string Create()
    {
        var state = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        lock (_createLock)
        {
            var now = time.GetUtcNow();
            foreach (var (key, entry) in _states)
            {
                if (entry.ExpiresAt <= now)
                    _states.TryRemove(key, out _);
            }
            while (_states.Count >= MaxStates)
                _states.TryRemove(_states.MinBy(e => e.Value.Order).Key, out _);

            _states[state] = (now + Lifetime, _created++);
        }
        return state;
    }

    public bool Consume(string state) =>
        _states.TryRemove(state, out var entry) && time.GetUtcNow() < entry.ExpiresAt;
}
