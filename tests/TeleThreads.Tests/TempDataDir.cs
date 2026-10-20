using Microsoft.Extensions.Options;
using MongoDB.Driver;
using TeleThreads.Storage;

namespace TeleThreads.Tests;

// A throwaway media directory and a throwaway database with an initialized repository.
public sealed class TempDataDir : IDisposable
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("TELETHREADS_TEST_MONGO") is { Length: > 0 } value
            ? value
            : "mongodb://localhost:27017";

    public TempDataDir()
    {
        Path = Directory.CreateTempSubdirectory("telethreads-tests").FullName;
        Storage = new StorageOptions { DataPath = Path };
        Mongo = new MongoOptions { ConnectionString = ConnectionString, Database = $"telethreads_test_{Guid.NewGuid():N}" };
        Repository = new PostRepository(Options.Create(Mongo));
        Repository.Initialize();
    }

    public string Path { get; }
    public StorageOptions Storage { get; }
    public MongoOptions Mongo { get; }
    public PostRepository Repository { get; }

    public void Dispose()
    {
        new MongoClient(Mongo.ConnectionString).DropDatabase(Mongo.Database);
        Directory.Delete(Path, recursive: true);
    }
}
