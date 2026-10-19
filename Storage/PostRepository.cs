using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using TeleThreads.TelegramScanner;

namespace TeleThreads.Storage;

// Posts table doubles as the work queue: each pipeline stage claims posts by status.
public sealed class PostRepository(IOptions<StorageOptions> options)
{
    private readonly string _databasePath = options.Value.DatabasePath;

    public void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_databasePath))!);
        Execute("""
            CREATE TABLE IF NOT EXISTS posts (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                telegram_chat_id INTEGER NOT NULL,
                telegram_first_message_id INTEGER NOT NULL,
                telegram_message_ids TEXT NOT NULL,
                media_group_id TEXT,
                telegram_date TEXT NOT NULL,
                original_text TEXT,
                image_count INTEGER NOT NULL,
                image_files TEXT NOT NULL,
                rewritten_text TEXT,
                status TEXT NOT NULL,
                attempts INTEGER NOT NULL DEFAULT 0,
                last_error TEXT,
                next_attempt_at TEXT,
                threads_post_id TEXT,
                received_at TEXT NOT NULL,
                rewritten_at TEXT,
                published_at TEXT,
                updated_at TEXT NOT NULL,
                UNIQUE (telegram_chat_id, telegram_first_message_id)
            );
            CREATE INDEX IF NOT EXISTS ix_posts_status ON posts (status);
            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """);
    }

    public bool Exists(long chatId, int firstMessageId) =>
        Scalar<long>(
            "SELECT COUNT(*) FROM posts WHERE telegram_chat_id = $chat AND telegram_first_message_id = $first",
            ("$chat", chatId), ("$first", firstMessageId)) > 0;

    public long Insert(IncomingPost post, IReadOnlyList<string> imageFiles, DateTime now) =>
        Scalar<long>("""
            INSERT INTO posts (telegram_chat_id, telegram_first_message_id, telegram_message_ids, media_group_id,
                telegram_date, original_text, image_count, image_files, status, received_at, updated_at)
            VALUES ($chat, $first, $ids, $group, $date, $text, $count, $files, $status, $now, $now)
            RETURNING id
            """,
            ("$chat", post.ChatId),
            ("$first", post.FirstMessageId),
            ("$ids", JsonSerializer.Serialize(post.MessageIds)),
            ("$group", post.MediaGroupId),
            ("$date", Format(post.Date)),
            ("$text", post.Text),
            ("$count", imageFiles.Count),
            ("$files", JsonSerializer.Serialize(imageFiles)),
            ("$status", PostStatus.Received),
            ("$now", Format(now)));

    public PostRecord? Get(long id) =>
        Query("SELECT * FROM posts WHERE id = $id", ("$id", id)).SingleOrDefault();

    public IReadOnlyList<PostRecord> List(int limit, string? status) =>
        Query(
            "SELECT * FROM posts WHERE $status IS NULL OR status = $status ORDER BY id DESC LIMIT $limit",
            ("$status", status), ("$limit", limit));

    // Atomically moves the oldest due post from fromStatus to the in-progress toStatus.
    public PostRecord? ClaimNext(string fromStatus, string toStatus, DateTime now) =>
        Query("""
            UPDATE posts SET status = $to, updated_at = $now
            WHERE id = (
                SELECT id FROM posts
                WHERE status = $from AND (next_attempt_at IS NULL OR next_attempt_at <= $now)
                ORDER BY id LIMIT 1)
            RETURNING *
            """,
            ("$from", fromStatus), ("$to", toStatus), ("$now", Format(now))).SingleOrDefault();

    public void MarkRewritten(long id, string text, DateTime now) =>
        Execute("""
            UPDATE posts SET status = $status, rewritten_text = $text, rewritten_at = $now,
                attempts = 0, last_error = NULL, next_attempt_at = NULL, updated_at = $now
            WHERE id = $id
            """,
            ("$id", id), ("$status", PostStatus.Rewritten), ("$text", text), ("$now", Format(now)));

    public void MarkPublished(long id, string threadsPostId, DateTime now) =>
        Execute("""
            UPDATE posts SET status = $status, threads_post_id = $threadsId, published_at = $now,
                attempts = 0, last_error = NULL, next_attempt_at = NULL, updated_at = $now
            WHERE id = $id
            """,
            ("$id", id), ("$status", PostStatus.Published), ("$threadsId", threadsPostId), ("$now", Format(now)));

    // Returns the status the post ended up in: retryStatus, or Failed once maxAttempts is reached.
    public string RecordFailure(long id, string retryStatus, string error, DateTime now, int maxAttempts, TimeSpan retryDelay) =>
        Scalar<string>("""
            UPDATE posts SET
                attempts = attempts + 1,
                status = CASE WHEN attempts + 1 >= $max THEN $failed ELSE $retry END,
                last_error = $error,
                next_attempt_at = $next,
                updated_at = $now
            WHERE id = $id
            RETURNING status
            """,
            ("$id", id), ("$max", maxAttempts), ("$failed", PostStatus.Failed), ("$retry", retryStatus),
            ("$error", error), ("$next", Format(now + retryDelay)), ("$now", Format(now)));

    // Posts interrupted mid-stage by a restart go back to the stage's input status.
    public void ResetInProgress(DateTime now) =>
        Execute("""
            UPDATE posts SET status = CASE status WHEN $rewriting THEN $received ELSE $rewritten END, updated_at = $now
            WHERE status IN ($rewriting, $publishing)
            """,
            ("$rewriting", PostStatus.Rewriting), ("$publishing", PostStatus.Publishing),
            ("$received", PostStatus.Received), ("$rewritten", PostStatus.Rewritten), ("$now", Format(now)));

    public string? GetSetting(string key)
    {
        using var connection = Open();
        using var command = Command(connection, "SELECT value FROM settings WHERE key = $key", [("$key", key)]);
        return command.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string value) =>
        Execute(
            "INSERT INTO settings (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
            ("$key", key), ("$value", value));

    private static string Format(DateTime value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTime ParseDate(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString());
        connection.Open();
        return connection;
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private void Execute(string sql, params (string, object?)[] parameters)
    {
        using var connection = Open();
        using var command = Command(connection, sql, parameters);
        command.ExecuteNonQuery();
    }

    private T Scalar<T>(string sql, params (string, object?)[] parameters)
    {
        using var connection = Open();
        using var command = Command(connection, sql, parameters);
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), CultureInfo.InvariantCulture);
    }

    private List<PostRecord> Query(string sql, params (string, object?)[] parameters)
    {
        using var connection = Open();
        using var command = Command(connection, sql, parameters);
        using var reader = command.ExecuteReader();
        var result = new List<PostRecord>();
        while (reader.Read())
            result.Add(Read(reader));
        return result;
    }

    private static PostRecord Read(SqliteDataReader reader)
    {
        string? Text(string column)
        {
            var i = reader.GetOrdinal(column);
            return reader.IsDBNull(i) ? null : reader.GetString(i);
        }
        DateTime? Date(string column) => Text(column) is { } value ? ParseDate(value) : null;

        return new PostRecord(
            Id: reader.GetInt64(reader.GetOrdinal("id")),
            TelegramChatId: reader.GetInt64(reader.GetOrdinal("telegram_chat_id")),
            TelegramMessageIds: JsonSerializer.Deserialize<List<int>>(Text("telegram_message_ids")!)!,
            MediaGroupId: Text("media_group_id"),
            TelegramDate: Date("telegram_date")!.Value,
            OriginalText: Text("original_text"),
            ImageFiles: JsonSerializer.Deserialize<List<string>>(Text("image_files")!)!,
            RewrittenText: Text("rewritten_text"),
            Status: Text("status")!,
            Attempts: reader.GetInt32(reader.GetOrdinal("attempts")),
            LastError: Text("last_error"),
            ThreadsPostId: Text("threads_post_id"),
            ReceivedAt: Date("received_at")!.Value,
            RewrittenAt: Date("rewritten_at"),
            PublishedAt: Date("published_at"),
            UpdatedAt: Date("updated_at")!.Value);
    }
}
