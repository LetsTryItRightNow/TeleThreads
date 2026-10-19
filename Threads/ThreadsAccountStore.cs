using System.Globalization;
using TeleThreads.Storage;

namespace TeleThreads.Threads;

// The connected Threads account lives in the settings table, so it survives restarts.
public sealed class ThreadsAccountStore(PostRepository repository)
{
    private const string UserIdKey = "threads_user_id";
    private const string UsernameKey = "threads_username";
    private const string TokenKey = "threads_access_token";
    private const string CheckedAtKey = "threads_token_checked_at";
    // Left over from the version that took the token from config.
    private const string LegacySeedKey = "threads_seed_token";

    public ThreadsAccount? Get() =>
        repository.GetSetting(UserIdKey) is { } userId && repository.GetSetting(TokenKey) is { } token
            ? new ThreadsAccount(userId, repository.GetSetting(UsernameKey) ?? "", token)
            : null;

    public DateTime? CheckedAt =>
        repository.GetSetting(CheckedAtKey) is { } value
            ? DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            : null;

    public void Save(ThreadsAccount account, DateTime now)
    {
        repository.SetSetting(UserIdKey, account.UserId);
        repository.SetSetting(UsernameKey, account.Username);
        SetToken(account.AccessToken, now);
    }

    // No-op when the account was reconnected or removed while the old token was being refreshed.
    public void UpdateToken(string oldToken, string newToken, DateTime now)
    {
        if (Get()?.AccessToken == oldToken)
            SetToken(newToken, now);
    }

    // Meta's deauthorization callbacks name the user; an unrelated user must not remove the connected account.
    public bool ClearIfUser(string userId)
    {
        if (Get()?.UserId != userId)
            return false;
        Clear();
        return true;
    }

    public void Clear()
    {
        foreach (var key in new[] { UserIdKey, UsernameKey, TokenKey, CheckedAtKey, LegacySeedKey })
            repository.DeleteSetting(key);
    }

    private void SetToken(string token, DateTime now)
    {
        repository.SetSetting(TokenKey, token);
        repository.SetSetting(CheckedAtKey, now.ToString("O", CultureInfo.InvariantCulture));
    }
}
