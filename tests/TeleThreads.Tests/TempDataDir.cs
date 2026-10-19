using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using TeleThreads.Storage;

namespace TeleThreads.Tests;

// A throwaway data directory with an initialized database.
public sealed class TempDataDir : IDisposable
{
    public TempDataDir()
    {
        Path = Directory.CreateTempSubdirectory("telethreads-tests").FullName;
        Storage = new StorageOptions { DataPath = Path };
        Repository = new PostRepository(Options.Create(Storage));
        Repository.Initialize();
    }

    public string Path { get; }
    public StorageOptions Storage { get; }
    public PostRepository Repository { get; }

    public void Dispose()
    {
        // Pooled connections keep the file open on Windows.
        SqliteConnection.ClearAllPools();
        Directory.Delete(Path, recursive: true);
    }
}
