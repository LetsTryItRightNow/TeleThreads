namespace TeleThreads.Storage;

public sealed class StorageOptions
{
    public string DataPath { get; set; } = "data";
    public string DatabasePath => Path.Combine(DataPath, "telethreads.db");
    public string MediaRoot => Path.Combine(DataPath, "media");
}
