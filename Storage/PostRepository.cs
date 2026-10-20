using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Driver;
using TeleThreads.TelegramScanner;

namespace TeleThreads.Storage;

// Posts collection doubles as the work queue: each pipeline stage claims posts by status.
public sealed class PostRepository
{
    private readonly IMongoCollection<PostDocument> _posts;
    private readonly IMongoCollection<CounterDocument> _counters;
    private readonly IMongoCollection<SettingDocument> _settings;

    static PostRepository() =>
        ConventionRegistry.Register(
            "TeleThreads storage",
            new ConventionPack { new CamelCaseElementNameConvention() },
            type => type.DeclaringType == typeof(PostRepository));

    public PostRepository(IOptions<MongoOptions> options)
    {
        var database = new MongoClient(options.Value.ConnectionString).GetDatabase(options.Value.Database);
        _posts = database.GetCollection<PostDocument>("posts");
        _counters = database.GetCollection<CounterDocument>("counters");
        _settings = database.GetCollection<SettingDocument>("settings");
    }

    private static FilterDefinitionBuilder<PostDocument> Filter => Builders<PostDocument>.Filter;
    private static UpdateDefinitionBuilder<PostDocument> Update => Builders<PostDocument>.Update;

    public void Initialize() =>
        _posts.Indexes.CreateMany([
            new CreateIndexModel<PostDocument>(
                Builders<PostDocument>.IndexKeys.Ascending(p => p.TelegramChatId).Ascending(p => p.TelegramFirstMessageId),
                new CreateIndexOptions { Unique = true }),
            new CreateIndexModel<PostDocument>(
                Builders<PostDocument>.IndexKeys.Ascending(p => p.Status).Ascending(p => p.NextAttemptAt).Ascending(p => p.Id)),
        ]);

    public bool Exists(long chatId, int firstMessageId) =>
        _posts.Find(p => p.TelegramChatId == chatId && p.TelegramFirstMessageId == firstMessageId).Any();

    public long Insert(IncomingPost post, IReadOnlyList<string> imageFiles, DateTime now)
    {
        var id = NextPostId();
        _posts.InsertOne(new PostDocument
        {
            Id = id,
            TelegramChatId = post.ChatId,
            TelegramFirstMessageId = post.FirstMessageId,
            TelegramMessageIds = [.. post.MessageIds],
            MediaGroupId = post.MediaGroupId,
            TelegramDate = post.Date,
            OriginalText = post.Text,
            ImageFiles = [.. imageFiles],
            Status = PostStatus.Received,
            ReceivedAt = now,
            UpdatedAt = now,
        });
        return id;
    }

    public PostRecord? Get(long id) =>
        _posts.Find(p => p.Id == id).SingleOrDefault()?.ToRecord();

    public IReadOnlyList<PostRecord> List(int limit, string? status) =>
        _posts.Find(status is null ? Filter.Empty : Filter.Eq(p => p.Status, status))
            .SortByDescending(p => p.Id)
            .Limit(limit)
            .ToList()
            .ConvertAll(p => p.ToRecord());

    // Atomically moves the oldest due post from fromStatus to the in-progress toStatus.
    public PostRecord? ClaimNext(string fromStatus, string toStatus, DateTime now) =>
        _posts.FindOneAndUpdate(
            Filter.Eq(p => p.Status, fromStatus)
                & (Filter.Eq(p => p.NextAttemptAt, null) | Filter.Lte(p => p.NextAttemptAt, now)),
            Update.Set(p => p.Status, toStatus).Set(p => p.UpdatedAt, now),
            new FindOneAndUpdateOptions<PostDocument>
            {
                Sort = Builders<PostDocument>.Sort.Ascending(p => p.Id),
                ReturnDocument = ReturnDocument.After,
            })?.ToRecord();

    public void MarkRewritten(long id, string text, DateTime now) =>
        _posts.UpdateOne(
            p => p.Id == id,
            Update.Set(p => p.Status, PostStatus.Rewritten)
                .Set(p => p.RewrittenText, text)
                .Set(p => p.RewrittenAt, now)
                .Set(p => p.Attempts, 0)
                .Set(p => p.LastError, null)
                .Set(p => p.NextAttemptAt, null)
                .Set(p => p.UpdatedAt, now));

    public void MarkPublished(long id, string threadsPostId, DateTime now) =>
        _posts.UpdateOne(
            p => p.Id == id,
            Update.Set(p => p.Status, PostStatus.Published)
                .Set(p => p.ThreadsPostId, threadsPostId)
                .Set(p => p.PublishedAt, now)
                .Set(p => p.Attempts, 0)
                .Set(p => p.LastError, null)
                .Set(p => p.NextAttemptAt, null)
                .Set(p => p.UpdatedAt, now));

    // Returns the status the post ended up in: retryStatus, or Failed once maxAttempts is reached.
    public string RecordFailure(long id, string retryStatus, string error, DateTime now, int maxAttempts, TimeSpan retryDelay)
    {
        // Within one $set stage "$attempts" still refers to the value before this update.
        var attempts = new BsonDocument("$add", new BsonArray { "$attempts", 1 });
        var set = new BsonDocument
        {
            { "attempts", attempts },
            {
                "status", new BsonDocument("$cond", new BsonArray
                {
                    new BsonDocument("$gte", new BsonArray { attempts, maxAttempts }),
                    Literal(PostStatus.Failed),
                    Literal(retryStatus),
                })
            },
            { "lastError", Literal(error) },
            { "nextAttemptAt", new BsonDateTime(now + retryDelay) },
            { "updatedAt", new BsonDateTime(now) },
        };
        var updated = _posts.FindOneAndUpdate(
            Filter.Eq(p => p.Id, id),
            Update.Pipeline(new BsonDocument[] { new("$set", set) }),
            new FindOneAndUpdateOptions<PostDocument> { ReturnDocument = ReturnDocument.After });
        return updated?.Status!;
    }

    // Posts interrupted mid-stage by a restart go back to the stage's input status.
    public void ResetInProgress(DateTime now)
    {
        _posts.UpdateMany(
            p => p.Status == PostStatus.Rewriting,
            Update.Set(p => p.Status, PostStatus.Received).Set(p => p.UpdatedAt, now));
        _posts.UpdateMany(
            p => p.Status == PostStatus.Publishing,
            Update.Set(p => p.Status, PostStatus.Rewritten).Set(p => p.UpdatedAt, now));
    }

    public string? GetSetting(string key) =>
        _settings.Find(s => s.Id == key).SingleOrDefault()?.Value;

    public void SetSetting(string key, string value) =>
        _settings.ReplaceOne(
            s => s.Id == key,
            new SettingDocument { Id = key, Value = value },
            new ReplaceOptions { IsUpsert = true });

    public void DeleteSetting(string key) =>
        _settings.DeleteOne(s => s.Id == key);

    private long NextPostId() =>
        _counters.FindOneAndUpdate(
            Builders<CounterDocument>.Filter.Eq(c => c.Id, "posts"),
            Builders<CounterDocument>.Update.Inc(c => c.Seq, 1L),
            new FindOneAndUpdateOptions<CounterDocument> { IsUpsert = true, ReturnDocument = ReturnDocument.After })
            .Seq;

    // In a pipeline a string starting with '$' would be read as a field path.
    private static BsonDocument Literal(string value) => new("$literal", value);

    // DateTime fields use the driver default: stored as BSON Date (UTC), read back with DateTimeKind.Utc.
    private sealed class PostDocument
    {
        public long Id { get; set; }
        public long TelegramChatId { get; set; }
        public int TelegramFirstMessageId { get; set; }
        public List<int> TelegramMessageIds { get; set; } = [];
        public string? MediaGroupId { get; set; }
        public DateTime TelegramDate { get; set; }
        public string? OriginalText { get; set; }
        public List<string> ImageFiles { get; set; } = [];
        public string? RewrittenText { get; set; }
        public string Status { get; set; } = "";
        public int Attempts { get; set; }
        public string? LastError { get; set; }
        public DateTime? NextAttemptAt { get; set; }
        public string? ThreadsPostId { get; set; }
        public DateTime ReceivedAt { get; set; }
        public DateTime? RewrittenAt { get; set; }
        public DateTime? PublishedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        public PostRecord ToRecord() => new(
            Id, TelegramChatId, TelegramMessageIds, MediaGroupId, TelegramDate, OriginalText, ImageFiles,
            RewrittenText, Status, Attempts, LastError, ThreadsPostId, ReceivedAt, RewrittenAt, PublishedAt, UpdatedAt);
    }

    private sealed class CounterDocument
    {
        public string Id { get; set; } = "";
        public long Seq { get; set; }
    }

    private sealed class SettingDocument
    {
        public string Id { get; set; } = "";
        public string Value { get; set; } = "";
    }
}
