using System.Text.Encodings.Web;
using System.Text.Unicode;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using TeleThreads.Api;
using TeleThreads.Pipeline;
using TeleThreads.Rewriting;
using TeleThreads.Storage;
using TeleThreads.TelegramScanner;
using TeleThreads.Threads;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<TelegramOptions>(builder.Configuration.GetSection("Telegram"));
builder.Services.Configure<GeminiOptions>(builder.Configuration.GetSection("Gemini"));
builder.Services.Configure<ThreadsOptions>(builder.Configuration.GetSection("Threads"));
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
builder.Services.Configure<MongoOptions>(builder.Configuration.GetSection("Mongo"));
builder.Services.Configure<MediaOptions>(builder.Configuration.GetSection("Media"));
builder.Services.Configure<TestingOptions>(builder.Configuration.GetSection("Testing"));
// Keep Cyrillic readable in API responses instead of \uXXXX escapes.
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Encoder = JavaScriptEncoder.Create(UnicodeRanges.All));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<PostRepository>();
builder.Services.AddHttpClient("telegram");
builder.Services.AddHttpClient<IPostRewriter, GeminiRewriter>();
builder.Services.AddSingleton<ThreadsAccountStore>();
builder.Services.AddSingleton<OAuthStateStore>();
builder.Services.AddHttpClient<IThreadsAccountProvider, ThreadsTokenService>();
builder.Services.AddHttpClient<ThreadsAuthClient>();
builder.Services.AddTransient<ThreadsConnector>();
if (builder.Configuration.GetValue("Threads:DryRun", true))
    builder.Services.AddSingleton<IThreadsPublisher, DryRunThreadsPublisher>();
else
    builder.Services.AddHttpClient<IThreadsPublisher, ThreadsPublisher>();
builder.Services.AddHostedService<TelegramScannerService>();
builder.Services.AddHostedService<PostProcessor>();

var app = builder.Build();

var storage = app.Services.GetRequiredService<IOptions<StorageOptions>>().Value;
var repository = app.Services.GetRequiredService<PostRepository>();
repository.Initialize();
repository.ResetInProgress(DateTime.UtcNow);
Directory.CreateDirectory(storage.MediaRoot);

if (!app.Services.GetRequiredService<IOptions<ThreadsOptions>>().Value.DryRun
    && app.Services.GetRequiredService<ThreadsAccountStore>().Get() is null)
{
    var publicBaseUrl = app.Services.GetRequiredService<IOptions<MediaOptions>>().Value.PublicBaseUrl.TrimEnd('/');
    app.Logger.LogWarning(
        "Threads publishing is on but no account is connected: open {LoginUrl}", $"{publicBaseUrl}/threads/login");
}

app.MapGet("/health", () => Results.Text("ok"));

// Threads downloads images by URL, so the photos taken from Telegram are served from here.
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(Path.GetFullPath(storage.MediaRoot)),
    RequestPath = "/media",
});

app.MapPostsApi();
app.MapThreadsAuth();
app.MapTestApi();

app.Run();
