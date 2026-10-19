namespace TeleThreads.Storage;

public sealed class MediaOptions
{
    public string PublicBaseUrl { get; set; } = "http://localhost:8080";

    // relativePath is relative to the media root and uses '/' separators.
    public string UrlFor(string relativePath) => $"{PublicBaseUrl.TrimEnd('/')}/media/{relativePath}";
}
