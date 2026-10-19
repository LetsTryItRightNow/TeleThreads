namespace TeleThreads.Threads;

public sealed class InvalidOAuthStateException() : Exception("OAuth state is unknown, already used or expired");

// Handles the OAuth callback: code → short-lived token → long-lived token → profile → stored account.
public sealed class ThreadsConnector(
    ThreadsAuthClient auth,
    OAuthStateStore states,
    ThreadsAccountStore store,
    TimeProvider time,
    ILogger<ThreadsConnector> logger)
{
    public async Task<ThreadsAccount> ConnectAsync(string code, string state, CancellationToken ct)
    {
        if (!states.Consume(state))
            throw new InvalidOAuthStateException();

        // Threads appends "#_" to the code; it is not part of it.
        if (code.EndsWith("#_", StringComparison.Ordinal))
            code = code[..^2];

        var (shortToken, _) = await auth.ExchangeCodeAsync(code, ct);
        var longToken = await auth.ExchangeForLongLivedAsync(shortToken, ct);
        var (id, username) = await auth.GetProfileAsync(longToken, ct);

        var account = new ThreadsAccount(id, username, longToken);
        store.Save(account, time.GetUtcNow().UtcDateTime);
        logger.LogInformation("Threads account @{Username} connected", username);
        return account;
    }
}
