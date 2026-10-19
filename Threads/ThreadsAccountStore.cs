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
        UpdateToken(account.AccessToken, now);
    }

    public void UpdateToken(string token, DateTime now)
    {
        repository.SetSetting(TokenKey, token);
        repository.SetSetting(CheckedAtKey, now.ToString("O", CultureInfo.InvariantCulture));
    }

    public void Clear()
    {
        foreach (var key in new[] { UserIdKey, UsernameKey, TokenKey, CheckedAtKey })
            repository.DeleteSetting(key);
    }
}
