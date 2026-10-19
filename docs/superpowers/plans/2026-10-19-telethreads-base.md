# TeleThreads Base Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Сервис, который забирает посты из Telegram-канала, сокращает текст через Gemini и (пока в dry-run) публикует их в Threads, с GET-API для просмотра состояния постов.

**Architecture:** Один ASP.NET Core сервис (.NET 10). `TelegramScannerService` (long polling) сохраняет посты и фото в SQLite и `/data/media`; `PostProcessor` забирает посты из БД по статусу и прогоняет через `IPostRewriter` (Gemini) и `IThreadsPublisher` (dry-run или реальный). БД — одновременно очередь и журнал. Фото раздаются по `/media/*`, состояние — по `/api/posts`.

**Tech Stack:** .NET 10, ASP.NET Core minimal API, Telegram.Bot 22.10.3.3, Microsoft.Data.Sqlite 10.0.13, xUnit v3 4.0.2, Docker.

Спецификация: `docs/superpowers/specs/2026-10-19-telethreads-design.md`.

---

## Карта файлов

```
global.json                                   — фиксирует SDK 10
TeleThreads.csproj                            — пакеты, исключение tests/, prompts в output
TeleThreads.slnx                              — + тестовый проект
appsettings.json                              — уровни логирования
prompts/system.txt                            — системный промпт Gemini (лимит 500)
Program.cs                                    — DI, /health, /media, /api/posts
TelegramScanner/TelegramOptions.cs
TelegramScanner/IncomingMessage.cs            — одно сообщение канала (без типов Telegram.Bot)
TelegramScanner/IncomingPost.cs               — собранный пост (одиночный или альбом)
TelegramScanner/MediaGroupAggregator.cs       — склейка альбомов
TelegramScanner/TelegramScannerService.cs     — long polling + скачивание фото + запись в БД
Rewriting/GeminiOptions.cs
Rewriting/IPostRewriter.cs
Rewriting/TextLimiter.cs                      — страховочная обрезка до 500
Rewriting/GeminiRewriter.cs
Threads/ThreadsOptions.cs
Threads/ThreadsPost.cs
Threads/IThreadsPublisher.cs
Threads/IThreadsTokenProvider.cs
Threads/DryRunThreadsPublisher.cs
Threads/ThreadsPublisher.cs
Threads/ThreadsTokenService.cs
Storage/StorageOptions.cs
Storage/MediaOptions.cs
Storage/PostStatus.cs
Storage/PostRecord.cs
Storage/PostRepository.cs
Pipeline/PostProcessor.cs
Api/PostDto.cs
Api/PostsEndpoints.cs
tests/TeleThreads.Tests/TeleThreads.Tests.csproj
tests/TeleThreads.Tests/StubHttpHandler.cs
tests/TeleThreads.Tests/ManualTimeProvider.cs
tests/TeleThreads.Tests/TempDataDir.cs
tests/TeleThreads.Tests/*Tests.cs
Dockerfile, docker-compose.yml, .env.example, .gitignore, .dockerignore
docs/deploy.md
```

Пространства имён: `TeleThreads.TelegramScanner` (не `TeleThreads.Telegram` — иначе конфликт с пространством имён `Telegram.Bot`), `TeleThreads.Rewriting`, `TeleThreads.Threads`, `TeleThreads.Storage`, `TeleThreads.Pipeline`, `TeleThreads.Api`.

Все тесты запускаются командой `dotnet test` из корня репозитория.

---

### Task 1: Каркас — SDK, пакеты, тестовый проект

**Files:**
- Create: `global.json`, `tests/TeleThreads.Tests/TeleThreads.Tests.csproj`, `tests/TeleThreads.Tests/StubHttpHandler.cs`, `tests/TeleThreads.Tests/ManualTimeProvider.cs`, `appsettings.json`
- Modify: `TeleThreads.csproj`, `TeleThreads.slnx`, `.gitignore`, `.dockerignore`

- [ ] **Step 1: global.json** (на машине есть SDK 11 RC — фиксируем 10, как в Docker)

```json
{
  "sdk": {
    "version": "10.0.100",
    "rollForward": "latestFeature"
  }
}
```

- [ ] **Step 2: TeleThreads.csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <DockerDefaultTargetOS>Linux</DockerDefaultTargetOS>
    <DockerfileContext>.</DockerfileContext>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.13" />
    <PackageReference Include="Microsoft.VisualStudio.Azure.Containers.Tools.Targets" Version="1.24.2-preview.1" />
    <PackageReference Include="Telegram.Bot" Version="22.10.3.3" />
  </ItemGroup>

  <!-- The test project lives under this folder; keep its sources out of the app. -->
  <ItemGroup>
    <Compile Remove="tests/**" />
    <Content Remove="tests/**" />
    <EmbeddedResource Remove="tests/**" />
    <None Remove="tests/**" />
  </ItemGroup>

  <ItemGroup>
    <None Remove="prompts/**" />
    <Content Include="prompts/**" CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest" />
  </ItemGroup>

</Project>
```

- [ ] **Step 3: tests/TeleThreads.Tests/TeleThreads.Tests.csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.11.0" />
    <PackageReference Include="xunit.v3" Version="4.0.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="4.0.1" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\TeleThreads.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 4: Общие тестовые помощники**

`tests/TeleThreads.Tests/StubHttpHandler.cs`:

```csharp
using System.Net;
using System.Text;

namespace TeleThreads.Tests;

public sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request, body));
        return respond(request);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
```

`tests/TeleThreads.Tests/ManualTimeProvider.cs`:

```csharp
namespace TeleThreads.Tests;

public sealed class ManualTimeProvider(DateTime utcNow) : TimeProvider
{
    public DateTime UtcNow { get; set; } = utcNow;

    public override DateTimeOffset GetUtcNow() => new(UtcNow, TimeSpan.Zero);
}
```

`TempDataDir.cs` создаётся в Task 5 (ему нужен `PostRepository`).

- [ ] **Step 5: appsettings.json**

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "System.Net.Http.HttpClient": "Warning"
    }
  },
  "AllowedHosts": "*"
}
```

- [ ] **Step 6: Solution, .gitignore, .dockerignore**

`TeleThreads.slnx`:

```xml
<Solution>
  <Project Path="TeleThreads.csproj" />
  <Project Path="tests/TeleThreads.Tests/TeleThreads.Tests.csproj" />
</Solution>
```

В конец `.gitignore` добавить:

```
### App
.env
data/
```

В конец `.dockerignore` добавить:

```
data
docs
tests
```

- [ ] **Step 7: Проверить сборку**

Run: `dotnet build TeleThreads.slnx`
Expected: `Build succeeded`, 0 errors.

- [ ] **Step 8: Commit**

```bash
git add global.json TeleThreads.csproj TeleThreads.slnx appsettings.json .gitignore .dockerignore tests/
git commit -m "Add test project, packages and SDK pin"
```

---

### Task 2: Классы настроек

**Files:**
- Create: `TelegramScanner/TelegramOptions.cs`, `Rewriting/GeminiOptions.cs`, `Threads/ThreadsOptions.cs`, `Storage/StorageOptions.cs`, `Storage/MediaOptions.cs`

- [ ] **Step 1: Создать файлы**

`TelegramScanner/TelegramOptions.cs`:

```csharp
namespace TeleThreads.TelegramScanner;

public sealed class TelegramOptions
{
    public string BotToken { get; set; } = "";
    public long ChannelId { get; set; }
}
```

`Rewriting/GeminiOptions.cs`:

```csharp
namespace TeleThreads.Rewriting;

public sealed class GeminiOptions
{
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gemini-3.5-flash";
    public string SystemPromptPath { get; set; } = "prompts/system.txt";
}
```

`Threads/ThreadsOptions.cs`:

```csharp
namespace TeleThreads.Threads;

public sealed class ThreadsOptions
{
    public bool DryRun { get; set; } = true;
    public string UserId { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public TimeSpan StatusPollInterval { get; set; } = TimeSpan.FromSeconds(3);
    public int StatusPollAttempts { get; set; } = 20;
}
```

`Storage/StorageOptions.cs`:

```csharp
namespace TeleThreads.Storage;

public sealed class StorageOptions
{
    public string DataPath { get; set; } = "data";
    public string DatabasePath => Path.Combine(DataPath, "telethreads.db");
    public string MediaRoot => Path.Combine(DataPath, "media");
}
```

`Storage/MediaOptions.cs`:

```csharp
namespace TeleThreads.Storage;

public sealed class MediaOptions
{
    public string PublicBaseUrl { get; set; } = "http://localhost:8080";

    // relativePath is relative to the media root and uses '/' separators.
    public string UrlFor(string relativePath) => $"{PublicBaseUrl.TrimEnd('/')}/media/{relativePath}";
}
```

- [ ] **Step 2: Build**

Run: `dotnet build TeleThreads.slnx` — Expected: `Build succeeded`.

- [ ] **Step 3: Commit**

```bash
git add TelegramScanner Rewriting Threads Storage
git commit -m "Add options classes"
```

---

### Task 3: TextLimiter

**Files:**
- Create: `Rewriting/TextLimiter.cs`
- Test: `tests/TeleThreads.Tests/TextLimiterTests.cs`

- [ ] **Step 1: Failing tests**

```csharp
using TeleThreads.Rewriting;

namespace TeleThreads.Tests;

public class TextLimiterTests
{
    [Fact]
    public void ShortText_IsReturnedTrimmed()
    {
        Assert.Equal("hi", TextLimiter.Limit("  hi  "));
    }

    [Fact]
    public void LongText_IsCutAtLastSentenceEnd()
    {
        var text = new string('a', 300) + ". " + new string('b', 300);

        Assert.Equal(new string('a', 300) + ".", TextLimiter.Limit(text));
    }

    [Fact]
    public void LongText_WithoutSentenceEnd_IsCutAtWordWithEllipsis()
    {
        var text = string.Join(" ", Enumerable.Repeat("word", 200));

        var result = TextLimiter.Limit(text);

        Assert.True(result.Length <= TextLimiter.ThreadsMaxLength);
        Assert.EndsWith("word…", result);
    }

    [Fact]
    public void LongText_WithoutSpaces_IsHardCut()
    {
        var result = TextLimiter.Limit(new string('x', 600));

        Assert.Equal(TextLimiter.ThreadsMaxLength, result.Length);
        Assert.EndsWith("…", result);
    }
}
```

- [ ] **Step 2: Run — fail**

Run: `dotnet test --filter TextLimiterTests` — Expected: build error `TextLimiter does not exist`.

- [ ] **Step 3: Implement** `Rewriting/TextLimiter.cs`

```csharp
namespace TeleThreads.Rewriting;

public static class TextLimiter
{
    public const int ThreadsMaxLength = 500;

    // Gemini is asked to stay within the limit; this is the safety net if it doesn't.
    public static string Limit(string text, int maxLength = ThreadsMaxLength)
    {
        text = text.Trim();
        if (text.Length <= maxLength)
            return text;

        var cut = text[..maxLength];
        var sentenceEnd = cut.LastIndexOfAny(['.', '!', '?', '…']);
        if (sentenceEnd >= maxLength / 2)
            return cut[..(sentenceEnd + 1)];

        // Leave room for the ellipsis.
        var space = cut.LastIndexOf(' ', maxLength - 2);
        if (space > 0)
            return cut[..space].TrimEnd() + "…";

        var end = maxLength - 1;
        if (char.IsLowSurrogate(text[end]))
            end--;
        return text[..end] + "…";
    }
}
```

- [ ] **Step 4: Run — pass**

Run: `dotnet test --filter TextLimiterTests` — Expected: 4 passed.

- [ ] **Step 5: Commit**

```bash
git add Rewriting/TextLimiter.cs tests/TeleThreads.Tests/TextLimiterTests.cs
git commit -m "Add 500-char text limiter"
```

---

### Task 4: MediaGroupAggregator

**Files:**
- Create: `TelegramScanner/IncomingMessage.cs`, `TelegramScanner/IncomingPost.cs`, `TelegramScanner/MediaGroupAggregator.cs`
- Test: `tests/TeleThreads.Tests/MediaGroupAggregatorTests.cs`

- [ ] **Step 1: Failing tests**

```csharp
using TeleThreads.TelegramScanner;

namespace TeleThreads.Tests;

public class MediaGroupAggregatorTests
{
    private static readonly DateTime T0 = new(2026, 10, 19, 10, 0, 0, DateTimeKind.Utc);
    private readonly MediaGroupAggregator _aggregator = new(TimeSpan.FromSeconds(3));

    private static IncomingMessage Message(int id, string? group, string? text = null, string? photo = null) =>
        new(-100, id, group, T0, text, photo);

    [Fact]
    public void StandaloneMessage_IsReturnedImmediately()
    {
        var post = _aggregator.Add(Message(1, null, "hello", "p1"), T0);

        Assert.NotNull(post);
        Assert.Equal([1], post.MessageIds);
        Assert.Equal("hello", post.Text);
        Assert.Equal(["p1"], post.PhotoFileIds);
        Assert.False(_aggregator.HasPending);
    }

    [Fact]
    public void AlbumParts_AreHeldUntilQuietPeriodPasses()
    {
        Assert.Null(_aggregator.Add(Message(12, "g", photo: "p2"), T0));
        Assert.Null(_aggregator.Add(Message(11, "g", "caption", "p1"), T0.AddSeconds(1)));
        Assert.Null(_aggregator.Add(Message(13, "g", photo: "p3"), T0.AddSeconds(2)));

        Assert.Empty(_aggregator.TakeCompleted(T0.AddSeconds(4)));
        var post = Assert.Single(_aggregator.TakeCompleted(T0.AddSeconds(5)));

        Assert.Equal([11, 12, 13], post.MessageIds);
        Assert.Equal(11, post.FirstMessageId);
        Assert.Equal(["p1", "p2", "p3"], post.PhotoFileIds);
        Assert.Equal("caption", post.Text);
        Assert.Equal("g", post.MediaGroupId);
        Assert.False(_aggregator.HasPending);
    }

    [Fact]
    public void TwoAlbums_CompleteIndependently()
    {
        _aggregator.Add(Message(1, "a", photo: "a1"), T0);
        _aggregator.Add(Message(5, "b", photo: "b1"), T0.AddSeconds(2));

        var first = Assert.Single(_aggregator.TakeCompleted(T0.AddSeconds(3)));
        Assert.Equal("a", first.MediaGroupId);
        Assert.True(_aggregator.HasPending);

        var second = Assert.Single(_aggregator.TakeCompleted(T0.AddSeconds(5)));
        Assert.Equal("b", second.MediaGroupId);
    }
}
```

- [ ] **Step 2: Run — fail**

Run: `dotnet test --filter MediaGroupAggregatorTests` — Expected: build error, types missing.

- [ ] **Step 3: Implement**

`TelegramScanner/IncomingMessage.cs`:

```csharp
namespace TeleThreads.TelegramScanner;

// One channel message, reduced to what the pipeline needs.
public sealed record IncomingMessage(
    long ChatId,
    int MessageId,
    string? MediaGroupId,
    DateTime Date,
    string? Text,
    string? PhotoFileId);
```

`TelegramScanner/IncomingPost.cs`:

```csharp
namespace TeleThreads.TelegramScanner;

// A complete channel post: a single message or a whole album.
public sealed record IncomingPost(
    long ChatId,
    IReadOnlyList<int> MessageIds,
    string? MediaGroupId,
    DateTime Date,
    string? Text,
    IReadOnlyList<string> PhotoFileIds)
{
    public int FirstMessageId => MessageIds[0];
}
```

`TelegramScanner/MediaGroupAggregator.cs`:

```csharp
namespace TeleThreads.TelegramScanner;

// Telegram delivers an album as separate messages sharing media_group_id; only one of them carries the caption.
public sealed class MediaGroupAggregator(TimeSpan quietPeriod)
{
    private readonly Dictionary<string, PendingGroup> _groups = [];

    public bool HasPending => _groups.Count > 0;

    // Standalone messages come back as a post right away; album parts are held until TakeCompleted.
    public IncomingPost? Add(IncomingMessage message, DateTime now)
    {
        if (message.MediaGroupId is null)
            return ToPost([message]);

        if (!_groups.TryGetValue(message.MediaGroupId, out var group))
            _groups[message.MediaGroupId] = group = new PendingGroup();
        group.Messages.Add(message);
        group.LastSeen = now;
        return null;
    }

    public IReadOnlyList<IncomingPost> TakeCompleted(DateTime now)
    {
        var completed = _groups.Where(g => now - g.Value.LastSeen >= quietPeriod).ToList();
        foreach (var (id, _) in completed)
            _groups.Remove(id);
        return completed.Select(g => ToPost(g.Value.Messages)).ToList();
    }

    private static IncomingPost ToPost(List<IncomingMessage> messages)
    {
        var ordered = messages.OrderBy(m => m.MessageId).ToList();
        var first = ordered[0];
        return new IncomingPost(
            first.ChatId,
            ordered.Select(m => m.MessageId).ToList(),
            first.MediaGroupId,
            first.Date,
            ordered.Select(m => m.Text).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)),
            ordered.Select(m => m.PhotoFileId).OfType<string>().ToList());
    }

    private sealed class PendingGroup
    {
        public List<IncomingMessage> Messages { get; } = [];
        public DateTime LastSeen { get; set; }
    }
}
```

- [ ] **Step 4: Run — pass**

Run: `dotnet test --filter MediaGroupAggregatorTests` — Expected: 3 passed.

- [ ] **Step 5: Commit**

```bash
git add TelegramScanner tests/TeleThreads.Tests/MediaGroupAggregatorTests.cs
git commit -m "Add Telegram album aggregator"
```

---

### Task 5: PostRepository (SQLite)

**Files:**
- Create: `Storage/PostStatus.cs`, `Storage/PostRecord.cs`, `Storage/PostRepository.cs`, `tests/TeleThreads.Tests/TempDataDir.cs`
- Test: `tests/TeleThreads.Tests/PostRepositoryTests.cs`

- [ ] **Step 1: Тестовый помощник** `tests/TeleThreads.Tests/TempDataDir.cs`

```csharp
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
```

- [ ] **Step 2: Failing tests** `tests/TeleThreads.Tests/PostRepositoryTests.cs`

```csharp
using TeleThreads.Storage;
using TeleThreads.TelegramScanner;

namespace TeleThreads.Tests;

public sealed class PostRepositoryTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 19, 10, 0, 0, DateTimeKind.Utc);
    private readonly TempDataDir _data = new();
    private PostRepository Repo => _data.Repository;

    public void Dispose() => _data.Dispose();

    private static IncomingPost Post(int firstId, string? text = "hello") =>
        new(-100, [firstId, firstId + 1], "g1", Now.AddMinutes(-1), text, ["f1", "f2"]);

    [Fact]
    public void Insert_ThenGet_RoundTripsFields()
    {
        var id = Repo.Insert(Post(10), ["a/0.jpg", "a/1.jpg"], Now);

        var post = Repo.Get(id)!;
        Assert.Equal(-100, post.TelegramChatId);
        Assert.Equal([10, 11], post.TelegramMessageIds);
        Assert.Equal("g1", post.MediaGroupId);
        Assert.Equal(Now.AddMinutes(-1), post.TelegramDate);
        Assert.Equal("hello", post.OriginalText);
        Assert.Equal(["a/0.jpg", "a/1.jpg"], post.ImageFiles);
        Assert.Equal(2, post.ImageCount);
        Assert.Equal(PostStatus.Received, post.Status);
        Assert.Equal(Now, post.ReceivedAt);
        Assert.Null(post.RewrittenText);
    }

    [Fact]
    public void Exists_IsTrueOnlyAfterInsert()
    {
        Assert.False(Repo.Exists(-100, 10));
        Repo.Insert(Post(10), [], Now);
        Assert.True(Repo.Exists(-100, 10));
    }

    [Fact]
    public void ClaimNext_MovesPostToInProgressStatusOnce()
    {
        var id = Repo.Insert(Post(10), [], Now);

        var claimed = Repo.ClaimNext(PostStatus.Received, PostStatus.Rewriting, Now);

        Assert.Equal(id, claimed!.Id);
        Assert.Equal(PostStatus.Rewriting, claimed.Status);
        Assert.Null(Repo.ClaimNext(PostStatus.Received, PostStatus.Rewriting, Now));
    }

    [Fact]
    public void MarkRewrittenAndPublished_StoreStageResults()
    {
        var id = Repo.Insert(Post(10), [], Now);

        Repo.MarkRewritten(id, "short", Now.AddSeconds(1));
        Repo.MarkPublished(id, "dry-run", Now.AddSeconds(2));

        var post = Repo.Get(id)!;
        Assert.Equal("short", post.RewrittenText);
        Assert.Equal(Now.AddSeconds(1), post.RewrittenAt);
        Assert.Equal(PostStatus.Published, post.Status);
        Assert.Equal("dry-run", post.ThreadsPostId);
        Assert.Equal(Now.AddSeconds(2), post.PublishedAt);
    }

    [Fact]
    public void RecordFailure_RetriesAfterDelayThenFails()
    {
        var id = Repo.Insert(Post(10), [], Now);
        var delay = TimeSpan.FromSeconds(30);

        Repo.ClaimNext(PostStatus.Received, PostStatus.Rewriting, Now);
        Assert.Equal(PostStatus.Received, Repo.RecordFailure(id, PostStatus.Received, "boom", Now, 3, delay));
        Assert.Null(Repo.ClaimNext(PostStatus.Received, PostStatus.Rewriting, Now.AddSeconds(29)));

        Assert.NotNull(Repo.ClaimNext(PostStatus.Received, PostStatus.Rewriting, Now.AddSeconds(30)));
        Assert.Equal(PostStatus.Received, Repo.RecordFailure(id, PostStatus.Received, "boom", Now.AddSeconds(30), 3, delay));

        Assert.NotNull(Repo.ClaimNext(PostStatus.Received, PostStatus.Rewriting, Now.AddMinutes(5)));
        Assert.Equal(PostStatus.Failed, Repo.RecordFailure(id, PostStatus.Received, "boom", Now.AddMinutes(5), 3, delay));

        var post = Repo.Get(id)!;
        Assert.Equal(3, post.Attempts);
        Assert.Equal("boom", post.LastError);
    }

    [Fact]
    public void ResetInProgress_ReturnsStuckPostsToStableStatus()
    {
        var rewriting = Repo.Insert(Post(10), [], Now);
        var publishing = Repo.Insert(Post(20), [], Now);
        Repo.ClaimNext(PostStatus.Received, PostStatus.Rewriting, Now);
        Repo.MarkRewritten(publishing, "x", Now);
        Repo.ClaimNext(PostStatus.Rewritten, PostStatus.Publishing, Now);

        Repo.ResetInProgress(Now);

        Assert.Equal(PostStatus.Received, Repo.Get(rewriting)!.Status);
        Assert.Equal(PostStatus.Rewritten, Repo.Get(publishing)!.Status);
    }

    [Fact]
    public void List_FiltersByStatusNewestFirst()
    {
        var a = Repo.Insert(Post(10), [], Now);
        var b = Repo.Insert(Post(20), [], Now);
        var c = Repo.Insert(Post(30), [], Now);
        Repo.MarkRewritten(b, "x", Now);

        Assert.Equal([c, b, a], Repo.List(50, null).Select(p => p.Id));
        Assert.Equal([c, a], Repo.List(50, PostStatus.Received).Select(p => p.Id));
        Assert.Equal([c], Repo.List(1, null).Select(p => p.Id));
    }

    [Fact]
    public void Settings_AreUpserted()
    {
        Assert.Null(Repo.GetSetting("k"));
        Repo.SetSetting("k", "1");
        Repo.SetSetting("k", "2");
        Assert.Equal("2", Repo.GetSetting("k"));
    }
}
```

- [ ] **Step 3: Run — fail**

Run: `dotnet test --filter PostRepositoryTests` — Expected: build error, types missing.

- [ ] **Step 4: Implement**

`Storage/PostStatus.cs`:

```csharp
namespace TeleThreads.Storage;

// Received → Rewriting → Rewritten → Publishing → Published, or Failed after too many attempts.
public static class PostStatus
{
    public const string Received = "Received";
    public const string Rewriting = "Rewriting";
    public const string Rewritten = "Rewritten";
    public const string Publishing = "Publishing";
    public const string Published = "Published";
    public const string Failed = "Failed";
}
```

`Storage/PostRecord.cs`:

```csharp
namespace TeleThreads.Storage;

public sealed record PostRecord(
    long Id,
    long TelegramChatId,
    IReadOnlyList<int> TelegramMessageIds,
    string? MediaGroupId,
    DateTime TelegramDate,
    string? OriginalText,
    IReadOnlyList<string> ImageFiles,
    string? RewrittenText,
    string Status,
    int Attempts,
    string? LastError,
    string? ThreadsPostId,
    DateTime ReceivedAt,
    DateTime? RewrittenAt,
    DateTime? PublishedAt,
    DateTime UpdatedAt)
{
    public int ImageCount => ImageFiles.Count;
}
```

`Storage/PostRepository.cs`:

```csharp
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
```

- [ ] **Step 5: Run — pass**

Run: `dotnet test --filter PostRepositoryTests` — Expected: 8 passed.

- [ ] **Step 6: Commit**

```bash
git add Storage tests/TeleThreads.Tests/TempDataDir.cs tests/TeleThreads.Tests/PostRepositoryTests.cs
git commit -m "Add SQLite post repository"
```

---

### Task 6: Gemini rewriter и системный промпт

**Files:**
- Create: `Rewriting/IPostRewriter.cs`, `Rewriting/GeminiRewriter.cs`, `prompts/system.txt`
- Test: `tests/TeleThreads.Tests/GeminiRewriterTests.cs`

- [ ] **Step 1: Failing tests**

```csharp
using System.Net;
using Microsoft.Extensions.Options;
using TeleThreads.Rewriting;

namespace TeleThreads.Tests;

public sealed class GeminiRewriterTests : IDisposable
{
    private readonly string _promptPath = Path.GetTempFileName();

    public GeminiRewriterTests() => File.WriteAllText(_promptPath, "SYSTEM PROMPT");

    public void Dispose() => File.Delete(_promptPath);

    private GeminiRewriter Create(StubHttpHandler handler) =>
        new(new HttpClient(handler), Options.Create(new GeminiOptions
        {
            ApiKey = "key-1",
            Model = "test-model",
            SystemPromptPath = _promptPath,
        }));

    private static string Answer(string text) =>
        $$"""{"candidates":[{"content":{"role":"model","parts":[{"text":{{System.Text.Json.JsonSerializer.Serialize(text)}}}]}}]}""";

    [Fact]
    public async Task RewriteAsync_SendsSystemPromptAndText()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json(Answer(" short version ")));

        var result = await Create(handler).RewriteAsync("long original", CancellationToken.None);

        Assert.Equal("short version", result);
        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal(
            "https://generativelanguage.googleapis.com/v1beta/models/test-model:generateContent",
            request.RequestUri!.ToString());
        Assert.Equal("key-1", request.Headers.GetValues("x-goog-api-key").Single());
        Assert.Contains("SYSTEM PROMPT", body);
        Assert.Contains("long original", body);
    }

    [Fact]
    public async Task RewriteAsync_TooLongAnswer_IsLimited()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json(Answer(new string('x', 800))));

        var result = await Create(handler).RewriteAsync("text", CancellationToken.None);

        Assert.True(result.Length <= TextLimiter.ThreadsMaxLength);
    }

    [Fact]
    public async Task RewriteAsync_ErrorStatus_Throws()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json("""{"error":"bad"}""", HttpStatusCode.BadRequest));

        await Assert.ThrowsAsync<HttpRequestException>(() => Create(handler).RewriteAsync("text", CancellationToken.None));
    }

    [Fact]
    public void ShippedPrompt_StatesThe500CharLimit()
    {
        var prompt = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "prompts", "system.txt"));

        Assert.Contains("500", prompt);
    }
}
```

- [ ] **Step 2: Run — fail**

Run: `dotnet test --filter GeminiRewriterTests` — Expected: build error.

- [ ] **Step 3: Implement**

`Rewriting/IPostRewriter.cs`:

```csharp
namespace TeleThreads.Rewriting;

public interface IPostRewriter
{
    Task<string> RewriteAsync(string text, CancellationToken ct);
}
```

`Rewriting/GeminiRewriter.cs`:

```csharp
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace TeleThreads.Rewriting;

public sealed class GeminiRewriter(HttpClient http, IOptions<GeminiOptions> options) : IPostRewriter
{
    private readonly GeminiOptions _options = options.Value;

    public async Task<string> RewriteAsync(string text, CancellationToken ct)
    {
        var systemPrompt = await File.ReadAllTextAsync(ResolvePromptPath(), ct);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://generativelanguage.googleapis.com/v1beta/models/{_options.Model}:generateContent")
        {
            Content = JsonContent.Create(new
            {
                systemInstruction = new { parts = new[] { new { text = systemPrompt } } },
                contents = new[] { new { role = "user", parts = new[] { new { text } } } },
            }),
        };
        request.Headers.Add("x-goog-api-key", _options.ApiKey);

        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Gemini returned {(int)response.StatusCode}: {body}");

        var parts = JsonNode.Parse(body)?["candidates"]?[0]?["content"]?["parts"]?.AsArray();
        var result = string.Concat(parts?.Select(p => p?["text"]?.GetValue<string>()) ?? Enumerable.Empty<string?>());
        if (string.IsNullOrWhiteSpace(result))
            throw new InvalidOperationException($"Gemini returned no text: {body}");

        return TextLimiter.Limit(result);
    }

    private string ResolvePromptPath() =>
        Path.IsPathRooted(_options.SystemPromptPath)
            ? _options.SystemPromptPath
            : Path.Combine(AppContext.BaseDirectory, _options.SystemPromptPath);
}
```

`prompts/system.txt`:

```
Ты редактор, который адаптирует посты из Telegram-канала для социальной сети Threads.

Перепиши присланный текст поста так, чтобы он стал короче и подходил для Threads.

Требования:
- Длина результата строго не больше 500 символов, включая пробелы, эмодзи и хештеги. Оптимально 300–450 символов.
- Сохрани язык оригинала, главный смысл, тон и манеру автора.
- Не добавляй фактов, которых нет в оригинале.
- Убери упоминания Telegram, призывы подписаться на канал и служебные пометки.
- Не используй Markdown-разметку: Threads её не поддерживает.
- Верни только готовый текст поста, без пояснений, вариантов и кавычек.
```

- [ ] **Step 4: Run — pass**

Run: `dotnet test --filter GeminiRewriterTests` — Expected: 4 passed.

- [ ] **Step 5: Commit**

```bash
git add Rewriting prompts tests/TeleThreads.Tests/GeminiRewriterTests.cs
git commit -m "Add Gemini rewriter with system prompt"
```

---

### Task 7: Threads publishers

**Files:**
- Create: `Threads/ThreadsPost.cs`, `Threads/IThreadsPublisher.cs`, `Threads/IThreadsTokenProvider.cs`, `Threads/DryRunThreadsPublisher.cs`, `Threads/ThreadsPublisher.cs`
- Test: `tests/TeleThreads.Tests/ThreadsPublisherTests.cs`

- [ ] **Step 1: Failing tests**

```csharp
using System.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TeleThreads.Threads;

namespace TeleThreads.Tests;

public class ThreadsPublisherTests
{
    private sealed class FakeTokens : IThreadsTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken ct) => Task.FromResult("tok");
    }

    private int _containers;

    private StubHttpHandler Handler(string status = "FINISHED") => new(request =>
        request.RequestUri!.AbsolutePath switch
        {
            "/v1.0/42/threads" => StubHttpHandler.Json($$"""{"id":"c{{++_containers}}"}"""),
            "/v1.0/42/threads_publish" => StubHttpHandler.Json("""{"id":"media-1"}"""),
            _ => StubHttpHandler.Json($$"""{"status":"{{status}}"}"""),
        });

    private static ThreadsPublisher Create(StubHttpHandler handler) =>
        new(new HttpClient(handler), new FakeTokens(), Options.Create(new ThreadsOptions
        {
            DryRun = false,
            UserId = "42",
            StatusPollInterval = TimeSpan.Zero,
            StatusPollAttempts = 3,
        }));

    private static List<System.Collections.Specialized.NameValueCollection> Posts(StubHttpHandler handler, string path) =>
        handler.Requests
            .Where(r => r.Request.Method == HttpMethod.Post && r.Request.RequestUri!.AbsolutePath == path)
            .Select(r => HttpUtility.ParseQueryString(r.Body!))
            .ToList();

    [Fact]
    public async Task TextOnly_CreatesTextContainerAndPublishesIt()
    {
        var handler = Handler();

        var id = await Create(handler).PublishAsync(new ThreadsPost("hello world", []), CancellationToken.None);

        Assert.Equal("media-1", id);
        var container = Assert.Single(Posts(handler, "/v1.0/42/threads"));
        Assert.Equal("TEXT", container["media_type"]);
        Assert.Equal("hello world", container["text"]);
        Assert.Equal("tok", container["access_token"]);
        Assert.Equal("c1", Assert.Single(Posts(handler, "/v1.0/42/threads_publish"))["creation_id"]);
    }

    [Fact]
    public async Task SingleImage_CreatesImageContainer()
    {
        var handler = Handler();

        await Create(handler).PublishAsync(new ThreadsPost("t", ["https://x/media/a/0.jpg"]), CancellationToken.None);

        var container = Assert.Single(Posts(handler, "/v1.0/42/threads"));
        Assert.Equal("IMAGE", container["media_type"]);
        Assert.Equal("https://x/media/a/0.jpg", container["image_url"]);
        Assert.Equal("t", container["text"]);
    }

    [Fact]
    public async Task SeveralImages_CreateCarousel()
    {
        var handler = Handler();

        await Create(handler).PublishAsync(
            new ThreadsPost("t", ["https://x/0.jpg", "https://x/1.jpg", "https://x/2.jpg"]), CancellationToken.None);

        var containers = Posts(handler, "/v1.0/42/threads");
        Assert.Equal(4, containers.Count);
        Assert.All(containers.Take(3), c =>
        {
            Assert.Equal("IMAGE", c["media_type"]);
            Assert.Equal("true", c["is_carousel_item"]);
            Assert.Null(c["text"]);
        });
        Assert.Equal("CAROUSEL", containers[3]["media_type"]);
        Assert.Equal("c1,c2,c3", containers[3]["children"]);
        Assert.Equal("t", containers[3]["text"]);
        Assert.Equal("c4", Assert.Single(Posts(handler, "/v1.0/42/threads_publish"))["creation_id"]);
    }

    [Fact]
    public async Task ContainerError_Throws()
    {
        var handler = Handler(status: "ERROR");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Create(handler).PublishAsync(new ThreadsPost("t", []), CancellationToken.None));
        Assert.Empty(Posts(handler, "/v1.0/42/threads_publish"));
    }

    [Fact]
    public async Task ContainerNeverFinishes_TimesOut()
    {
        var handler = Handler(status: "IN_PROGRESS");

        await Assert.ThrowsAsync<TimeoutException>(
            () => Create(handler).PublishAsync(new ThreadsPost("t", []), CancellationToken.None));
    }

    [Fact]
    public async Task DryRun_ReturnsMarkerWithoutHttp()
    {
        var publisher = new DryRunThreadsPublisher(NullLogger<DryRunThreadsPublisher>.Instance);

        var id = await publisher.PublishAsync(new ThreadsPost("t", ["https://x/0.jpg"]), CancellationToken.None);

        Assert.Equal(DryRunThreadsPublisher.PostId, id);
    }
}
```

- [ ] **Step 2: Run — fail**

Run: `dotnet test --filter ThreadsPublisherTests` — Expected: build error.

- [ ] **Step 3: Implement**

`Threads/ThreadsPost.cs`:

```csharp
namespace TeleThreads.Threads;

public sealed record ThreadsPost(string? Text, IReadOnlyList<string> ImageUrls);
```

`Threads/IThreadsPublisher.cs`:

```csharp
namespace TeleThreads.Threads;

public interface IThreadsPublisher
{
    // Returns the id of the published Threads post.
    Task<string> PublishAsync(ThreadsPost post, CancellationToken ct);
}
```

`Threads/IThreadsTokenProvider.cs`:

```csharp
namespace TeleThreads.Threads;

public interface IThreadsTokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken ct);
}
```

`Threads/DryRunThreadsPublisher.cs`:

```csharp
namespace TeleThreads.Threads;

// Used while Threads:DryRun is on: shows what would be published without calling Threads.
public sealed class DryRunThreadsPublisher(ILogger<DryRunThreadsPublisher> logger) : IThreadsPublisher
{
    public const string PostId = "dry-run";

    public Task<string> PublishAsync(ThreadsPost post, CancellationToken ct)
    {
        logger.LogInformation(
            "[dry-run] Would publish to Threads: {Text} with {Count} image(s): {Urls}",
            post.Text, post.ImageUrls.Count, string.Join(", ", post.ImageUrls));
        return Task.FromResult(PostId);
    }
}
```

`Threads/ThreadsPublisher.cs`:

```csharp
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace TeleThreads.Threads;

// Publishing is two-step: create a media container, wait until Threads has processed it, then publish it.
public sealed class ThreadsPublisher(HttpClient http, IThreadsTokenProvider tokens, IOptions<ThreadsOptions> options)
    : IThreadsPublisher
{
    public const int MaxCarouselItems = 20;
    private const string BaseUrl = "https://graph.threads.net/v1.0/";
    private readonly ThreadsOptions _options = options.Value;

    public async Task<string> PublishAsync(ThreadsPost post, CancellationToken ct)
    {
        var token = await tokens.GetAccessTokenAsync(ct);
        var images = post.ImageUrls.Take(MaxCarouselItems).ToList();

        var containerId = images.Count switch
        {
            0 => await CreateContainerAsync(token, new() { ["media_type"] = "TEXT", ["text"] = post.Text }, ct),
            1 => await CreateContainerAsync(
                token, new() { ["media_type"] = "IMAGE", ["image_url"] = images[0], ["text"] = post.Text }, ct),
            _ => await CreateCarouselAsync(token, images, post.Text, ct),
        };
        await WaitUntilFinishedAsync(token, containerId, ct);

        return await PostAsync($"{_options.UserId}/threads_publish", token, new() { ["creation_id"] = containerId }, ct);
    }

    private async Task<string> CreateCarouselAsync(string token, List<string> images, string? text, CancellationToken ct)
    {
        var children = new List<string>();
        foreach (var url in images)
        {
            var id = await CreateContainerAsync(
                token, new() { ["media_type"] = "IMAGE", ["image_url"] = url, ["is_carousel_item"] = "true" }, ct);
            await WaitUntilFinishedAsync(token, id, ct);
            children.Add(id);
        }
        return await CreateContainerAsync(
            token, new() { ["media_type"] = "CAROUSEL", ["children"] = string.Join(",", children), ["text"] = text }, ct);
    }

    private Task<string> CreateContainerAsync(string token, Dictionary<string, string?> fields, CancellationToken ct) =>
        PostAsync($"{_options.UserId}/threads", token, fields, ct);

    private async Task<string> PostAsync(string path, string token, Dictionary<string, string?> fields, CancellationToken ct)
    {
        fields["access_token"] = token;
        using var content = new FormUrlEncodedContent(
            fields.Where(f => f.Value is not null).Select(f => KeyValuePair.Create(f.Key, f.Value!)));
        using var response = await http.PostAsync(BaseUrl + path, content, ct);
        var json = await ReadJsonAsync(response, ct);
        return json["id"]?.GetValue<string>() ?? throw new InvalidOperationException($"Threads response has no id: {json}");
    }

    private async Task WaitUntilFinishedAsync(string token, string containerId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < _options.StatusPollAttempts; attempt++)
        {
            using var response = await http.GetAsync(
                $"{BaseUrl}{containerId}?fields=status,error_message&access_token={Uri.EscapeDataString(token)}", ct);
            var json = await ReadJsonAsync(response, ct);
            switch (json["status"]?.GetValue<string>())
            {
                case "FINISHED":
                    return;
                case "ERROR" or "EXPIRED":
                    throw new InvalidOperationException($"Threads container {containerId} failed: {json}");
            }
            await Task.Delay(_options.StatusPollInterval, ct);
        }
        throw new TimeoutException($"Threads container {containerId} was not processed in time");
    }

    private static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Threads returned {(int)response.StatusCode}: {body}");
        return JsonNode.Parse(body) ?? throw new InvalidOperationException("Threads returned an empty body");
    }
}
```

- [ ] **Step 4: Run — pass**

Run: `dotnet test --filter ThreadsPublisherTests` — Expected: 6 passed.

- [ ] **Step 5: Commit**

```bash
git add Threads tests/TeleThreads.Tests/ThreadsPublisherTests.cs
git commit -m "Add Threads publisher and dry-run publisher"
```

---

### Task 8: ThreadsTokenService

**Files:**
- Create: `Threads/ThreadsTokenService.cs`
- Test: `tests/TeleThreads.Tests/ThreadsTokenServiceTests.cs`

- [ ] **Step 1: Failing tests**

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TeleThreads.Threads;

namespace TeleThreads.Tests;

public sealed class ThreadsTokenServiceTests : IDisposable
{
    private readonly TempDataDir _data = new();
    private readonly ManualTimeProvider _time = new(new DateTime(2026, 10, 19, 10, 0, 0, DateTimeKind.Utc));
    private readonly StubHttpHandler _handler =
        new(_ => StubHttpHandler.Json("""{"access_token":"refreshed","token_type":"bearer","expires_in":5183944}"""));

    public void Dispose() => _data.Dispose();

    private ThreadsTokenService Create(string configuredToken) =>
        new(new HttpClient(_handler), _data.Repository,
            Options.Create(new ThreadsOptions { AccessToken = configuredToken }),
            _time, NullLogger<ThreadsTokenService>.Instance);

    [Fact]
    public async Task FirstUse_ReturnsConfiguredTokenWithoutRefresh()
    {
        Assert.Equal("initial", await Create("initial").GetAccessTokenAsync(CancellationToken.None));
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task AfterRefreshInterval_RefreshesAndStoresToken()
    {
        var service = Create("initial");
        await service.GetAccessTokenAsync(CancellationToken.None);

        _time.UtcNow = _time.UtcNow.AddDays(8);
        Assert.Equal("refreshed", await service.GetAccessTokenAsync(CancellationToken.None));
        Assert.Equal("refreshed", await service.GetAccessTokenAsync(CancellationToken.None));

        var request = Assert.Single(_handler.Requests).Request;
        Assert.Equal("/refresh_access_token", request.RequestUri!.AbsolutePath);
        Assert.Contains("access_token=initial", request.RequestUri.Query);
    }

    [Fact]
    public async Task NewConfiguredToken_ReplacesStoredOne()
    {
        await Create("old").GetAccessTokenAsync(CancellationToken.None);

        Assert.Equal("new", await Create("new").GetAccessTokenAsync(CancellationToken.None));
    }
}
```

- [ ] **Step 2: Run — fail**

Run: `dotnet test --filter ThreadsTokenServiceTests` — Expected: build error.

- [ ] **Step 3: Implement** `Threads/ThreadsTokenService.cs`

```csharp
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using TeleThreads.Storage;

namespace TeleThreads.Threads;

// Long-lived Threads tokens expire after 60 days; refreshing one regularly keeps it alive.
public sealed class ThreadsTokenService(
    HttpClient http,
    PostRepository repository,
    IOptions<ThreadsOptions> options,
    TimeProvider time,
    ILogger<ThreadsTokenService> logger) : IThreadsTokenProvider
{
    private const string TokenKey = "threads_access_token";
    private const string SeedKey = "threads_seed_token";
    private const string CheckedAtKey = "threads_token_checked_at";
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromDays(7);

    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var configured = options.Value.AccessToken;

        // A token put into config by hand (e.g. after re-issuing it) replaces the stored one.
        if (repository.GetSetting(SeedKey) != configured)
        {
            repository.SetSetting(SeedKey, configured);
            repository.SetSetting(TokenKey, configured);
            repository.SetSetting(CheckedAtKey, Format(now));
        }

        var token = repository.GetSetting(TokenKey)!;
        var checkedAt = repository.GetSetting(CheckedAtKey) is { } value
            ? DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            : DateTime.MinValue;
        if (now - checkedAt < RefreshInterval)
            return token;

        repository.SetSetting(CheckedAtKey, Format(now));
        try
        {
            using var response = await http.GetAsync(
                "https://graph.threads.net/refresh_access_token?grant_type=th_refresh_token&access_token="
                + Uri.EscapeDataString(token), ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Threads token refresh returned {(int)response.StatusCode}: {body}");

            var refreshed = JsonNode.Parse(body)?["access_token"]?.GetValue<string>()
                ?? throw new InvalidOperationException("Threads token refresh returned no token");
            repository.SetSetting(TokenKey, refreshed);
            logger.LogInformation("Threads access token refreshed");
            return refreshed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Threads token refresh failed; using the current token");
            return token;
        }
    }

    private static string Format(DateTime value) => value.ToString("O", CultureInfo.InvariantCulture);
}
```

- [ ] **Step 4: Run — pass**

Run: `dotnet test --filter ThreadsTokenServiceTests` — Expected: 3 passed.

- [ ] **Step 5: Commit**

```bash
git add Threads/ThreadsTokenService.cs tests/TeleThreads.Tests/ThreadsTokenServiceTests.cs
git commit -m "Add Threads token refresh"
```

---

### Task 9: PostProcessor

**Files:**
- Create: `Pipeline/PostProcessor.cs`
- Test: `tests/TeleThreads.Tests/PostProcessorTests.cs`

- [ ] **Step 1: Failing tests**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TeleThreads.Pipeline;
using TeleThreads.Rewriting;
using TeleThreads.Storage;
using TeleThreads.TelegramScanner;
using TeleThreads.Threads;

namespace TeleThreads.Tests;

public sealed class PostProcessorTests : IDisposable
{
    private readonly TempDataDir _data = new();
    private readonly ManualTimeProvider _time = new(new DateTime(2026, 10, 19, 10, 0, 0, DateTimeKind.Utc));
    private readonly FakeRewriter _rewriter = new();
    private readonly FakePublisher _publisher = new();

    public void Dispose() => _data.Dispose();

    private sealed class FakeRewriter : IPostRewriter
    {
        public bool Fail { get; set; }
        public int Calls { get; private set; }

        public Task<string> RewriteAsync(string text, CancellationToken ct)
        {
            Calls++;
            return Fail ? throw new InvalidOperationException("gemini down") : Task.FromResult("short: " + text);
        }
    }

    private sealed class FakePublisher : IThreadsPublisher
    {
        public List<ThreadsPost> Published { get; } = [];

        public Task<string> PublishAsync(ThreadsPost post, CancellationToken ct)
        {
            Published.Add(post);
            return Task.FromResult("threads-1");
        }
    }

    private PostProcessor Create(bool dryRun = true)
    {
        var scopes = new ServiceCollection()
            .AddSingleton<IPostRewriter>(_rewriter)
            .AddSingleton<IThreadsPublisher>(_publisher)
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
        return new PostProcessor(
            _data.Repository, scopes,
            Options.Create(new MediaOptions { PublicBaseUrl = "https://x" }),
            Options.Create(_data.Storage),
            Options.Create(new ThreadsOptions { DryRun = dryRun }),
            _time, NullLogger<PostProcessor>.Instance);
    }

    private int _nextMessageId = 1;

    private long Insert(string? text, params string[] files) =>
        _data.Repository.Insert(
            new IncomingPost(-100, [_nextMessageId++], null, _time.UtcNow, text, []), files, _time.UtcNow);

    [Fact]
    public async Task Post_IsRewrittenAndPublished()
    {
        var id = Insert("long text", "a/0.jpg", "a/1.jpg");

        await Create().ProcessPendingAsync(CancellationToken.None);

        var post = _data.Repository.Get(id)!;
        Assert.Equal(PostStatus.Published, post.Status);
        Assert.Equal("short: long text", post.RewrittenText);
        Assert.Equal("threads-1", post.ThreadsPostId);
        var published = Assert.Single(_publisher.Published);
        Assert.Equal("short: long text", published.Text);
        Assert.Equal(["https://x/media/a/0.jpg", "https://x/media/a/1.jpg"], published.ImageUrls);
    }

    [Fact]
    public async Task PostWithoutText_SkipsRewriterAndPublishesImagesOnly()
    {
        Insert(null, "a/0.jpg");

        await Create().ProcessPendingAsync(CancellationToken.None);

        Assert.Equal(0, _rewriter.Calls);
        Assert.Null(Assert.Single(_publisher.Published).Text);
    }

    [Fact]
    public async Task RewriterFailure_IsRecordedForRetry()
    {
        var id = Insert("text");
        _rewriter.Fail = true;

        await Create().ProcessPendingAsync(CancellationToken.None);

        var post = _data.Repository.Get(id)!;
        Assert.Equal(PostStatus.Received, post.Status);
        Assert.Equal(1, post.Attempts);
        Assert.Equal("gemini down", post.LastError);
        Assert.Empty(_publisher.Published);
    }

    [Fact]
    public async Task RealPublish_DeletesMediaFolder_DryRunKeepsIt()
    {
        var folder = Path.Combine(_data.Storage.MediaRoot, "a");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "0.jpg"), "x");
        Insert("text", "a/0.jpg");

        await Create(dryRun: true).ProcessPendingAsync(CancellationToken.None);
        Assert.True(Directory.Exists(folder));

        Insert("text", "a/0.jpg");
        await Create(dryRun: false).ProcessPendingAsync(CancellationToken.None);
        Assert.False(Directory.Exists(folder));
    }
}
```

- [ ] **Step 2: Run — fail**

Run: `dotnet test --filter PostProcessorTests` — Expected: build error.

- [ ] **Step 3: Implement** `Pipeline/PostProcessor.cs`

```csharp
using Microsoft.Extensions.Options;
using TeleThreads.Rewriting;
using TeleThreads.Storage;
using TeleThreads.Threads;

namespace TeleThreads.Pipeline;

// Moves posts through the stages: Received → (Gemini) → Rewritten → (Threads) → Published.
public sealed class PostProcessor(
    PostRepository repository,
    IServiceScopeFactory scopes,
    IOptions<MediaOptions> media,
    IOptions<StorageOptions> storage,
    IOptions<ThreadsOptions> threads,
    TimeProvider time,
    ILogger<PostProcessor> logger) : BackgroundService
{
    public const int MaxAttempts = 3;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Post processing loop failed");
            }
            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    public async Task ProcessPendingAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var rewriter = scope.ServiceProvider.GetRequiredService<IPostRewriter>();
        var publisher = scope.ServiceProvider.GetRequiredService<IThreadsPublisher>();

        while (repository.ClaimNext(PostStatus.Received, PostStatus.Rewriting, Now) is { } post)
            await RunStageAsync(post, PostStatus.Received, () => RewriteAsync(post, rewriter, ct), ct);

        while (repository.ClaimNext(PostStatus.Rewritten, PostStatus.Publishing, Now) is { } post)
            await RunStageAsync(post, PostStatus.Rewritten, () => PublishAsync(post, publisher, ct), ct);
    }

    private async Task RunStageAsync(PostRecord post, string retryStatus, Func<Task> stage, CancellationToken ct)
    {
        try
        {
            await stage();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var delay = post.Attempts == 0 ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(2);
            var status = repository.RecordFailure(post.Id, retryStatus, ex.Message, Now, MaxAttempts, delay);
            logger.LogError(ex, "Post {Id} failed while {Stage}; status is now {Status}", post.Id, post.Status, status);
        }
    }

    private async Task RewriteAsync(PostRecord post, IPostRewriter rewriter, CancellationToken ct)
    {
        var text = string.IsNullOrWhiteSpace(post.OriginalText)
            ? ""
            : await rewriter.RewriteAsync(post.OriginalText, ct);
        repository.MarkRewritten(post.Id, text, Now);
        logger.LogInformation("Post {Id} rewritten: {Length} chars", post.Id, text.Length);
    }

    private async Task PublishAsync(PostRecord post, IThreadsPublisher publisher, CancellationToken ct)
    {
        var threadsPost = new ThreadsPost(
            string.IsNullOrEmpty(post.RewrittenText) ? null : post.RewrittenText,
            post.ImageFiles.Select(media.Value.UrlFor).ToList());
        var threadsId = await publisher.PublishAsync(threadsPost, ct);
        repository.MarkPublished(post.Id, threadsId, Now);
        logger.LogInformation("Post {Id} published to Threads as {ThreadsId}", post.Id, threadsId);

        // In dry-run the photos stay, so their public URLs can be checked by hand.
        if (!threads.Value.DryRun)
            DeleteMedia(post);
    }

    private void DeleteMedia(PostRecord post)
    {
        foreach (var folder in post.ImageFiles.Select(Path.GetDirectoryName).Distinct())
        {
            if (string.IsNullOrEmpty(folder))
                continue;
            var path = Path.Combine(storage.Value.MediaRoot, folder);
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
    }
}
```

- [ ] **Step 4: Run — pass**

Run: `dotnet test --filter PostProcessorTests` — Expected: 4 passed.

- [ ] **Step 5: Commit**

```bash
git add Pipeline tests/TeleThreads.Tests/PostProcessorTests.cs
git commit -m "Add post processing pipeline"
```

---

### Task 10: TelegramScannerService

Без юнит-тестов (это тонкая обёртка над Telegram.Bot; логика склейки покрыта в Task 4). Проверяется сборкой и ручным прогоном.

**Files:**
- Create: `TelegramScanner/TelegramScannerService.cs`

- [ ] **Step 1: Implement**

```csharp
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TeleThreads.Storage;

namespace TeleThreads.TelegramScanner;

// Long-polls the bot for channel posts, downloads their photos and stores them as Received posts.
public sealed class TelegramScannerService(
    IHttpClientFactory httpClientFactory,
    PostRepository repository,
    IOptions<TelegramOptions> telegram,
    IOptions<StorageOptions> storage,
    TimeProvider time,
    ILogger<TelegramScannerService> logger) : BackgroundService
{
    private static readonly TimeSpan AlbumQuietPeriod = TimeSpan.FromSeconds(3);
    private readonly MediaGroupAggregator _aggregator = new(AlbumQuietPeriod);

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(telegram.Value.BotToken))
        {
            logger.LogWarning("Telegram:BotToken is not set; the Telegram scanner is disabled");
            return;
        }

        var bot = new TelegramBotClient(telegram.Value.BotToken, httpClientFactory.CreateClient("telegram"), stoppingToken);
        var offset = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Poll briefly while an album is being collected, so it is saved soon after its last part.
                var updates = await bot.GetUpdates(
                    offset,
                    timeout: _aggregator.HasPending ? 1 : 25,
                    allowedUpdates: [UpdateType.ChannelPost],
                    cancellationToken: stoppingToken);
                foreach (var update in updates)
                {
                    offset = update.Id + 1;
                    if (update.ChannelPost is { } message)
                        await HandleAsync(bot, message, stoppingToken);
                }
                foreach (var post in _aggregator.TakeCompleted(Now))
                    await SaveAsync(bot, post, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Telegram polling failed");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task HandleAsync(ITelegramBotClient bot, Message message, CancellationToken ct)
    {
        if (message.Chat.Id != telegram.Value.ChannelId)
        {
            // Also the easiest way to find out the channel id when setting the bot up.
            logger.LogInformation(
                "Ignoring post from chat {ChatId} ({Title}); Telegram:ChannelId is {ChannelId}",
                message.Chat.Id, message.Chat.Title, telegram.Value.ChannelId);
            return;
        }

        var incoming = new IncomingMessage(
            message.Chat.Id,
            message.Id,
            message.MediaGroupId,
            message.Date,
            message.Text ?? message.Caption,
            message.Photo?.MaxBy(p => p.Width * p.Height)?.FileId);
        // Album parts without a photo (e.g. a video) are kept: one of them may carry the caption.
        if (incoming.Text is null && incoming.PhotoFileId is null && incoming.MediaGroupId is null)
        {
            logger.LogInformation("Skipping message {MessageId}: no text or photo", message.Id);
            return;
        }

        if (_aggregator.Add(incoming, Now) is { } post)
            await SaveAsync(bot, post, ct);
    }

    private async Task SaveAsync(ITelegramBotClient bot, IncomingPost post, CancellationToken ct)
    {
        try
        {
            if (repository.Exists(post.ChatId, post.FirstMessageId))
                return;

            var folder = $"{post.ChatId}_{post.FirstMessageId}";
            Directory.CreateDirectory(Path.Combine(storage.Value.MediaRoot, folder));
            var files = new List<string>();
            for (var i = 0; i < post.PhotoFileIds.Count; i++)
            {
                var relativePath = $"{folder}/{i}.jpg";
                await using var file = File.Create(Path.Combine(storage.Value.MediaRoot, relativePath));
                await bot.GetInfoAndDownloadFile(post.PhotoFileIds[i], file, ct);
                files.Add(relativePath);
            }

            var id = repository.Insert(post, files, Now);
            logger.LogInformation("Saved post {Id} from Telegram with {Count} image(s)", id, files.Count);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Failed to save Telegram post {MessageId}", post.FirstMessageId);
        }
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build TeleThreads.slnx` — Expected: `Build succeeded`. Если имена методов Telegram.Bot 22.x отличаются (`GetUpdates`, `GetInfoAndDownloadFile`, `Message.Id`), сверить с XML-документацией пакета и поправить вызовы, не меняя логику.

- [ ] **Step 3: Commit**

```bash
git add TelegramScanner/TelegramScannerService.cs
git commit -m "Add Telegram channel scanner"
```

---

### Task 11: GET-API

**Files:**
- Create: `Api/PostDto.cs`, `Api/PostsEndpoints.cs`
- Test: `tests/TeleThreads.Tests/PostDtoTests.cs`

- [ ] **Step 1: Failing test**

```csharp
using TeleThreads.Api;
using TeleThreads.Storage;

namespace TeleThreads.Tests;

public class PostDtoTests
{
    [Fact]
    public void From_MapsFieldsAndBuildsImageUrls()
    {
        var at = new DateTime(2026, 10, 19, 10, 0, 0, DateTimeKind.Utc);
        var record = new PostRecord(
            7, -100, [1, 2], "g", at, "orig", ["-100_1/0.jpg", "-100_1/1.jpg"], "short", PostStatus.Published,
            0, null, "dry-run", at, at, at, at);

        var dto = PostDto.From(record, new MediaOptions { PublicBaseUrl = "https://t.example/" });

        Assert.Equal(7, dto.Id);
        Assert.Equal(2, dto.ImageCount);
        Assert.Equal(["https://t.example/media/-100_1/0.jpg", "https://t.example/media/-100_1/1.jpg"], dto.ImageUrls);
        Assert.Equal("orig", dto.OriginalText);
        Assert.Equal("short", dto.RewrittenText);
        Assert.Equal(PostStatus.Published, dto.Status);
        Assert.Equal("dry-run", dto.ThreadsPostId);
    }
}
```

- [ ] **Step 2: Run — fail**

Run: `dotnet test --filter PostDtoTests` — Expected: build error.

- [ ] **Step 3: Implement**

`Api/PostDto.cs`:

```csharp
using TeleThreads.Storage;

namespace TeleThreads.Api;

public sealed record PostDto(
    long Id,
    IReadOnlyList<int> TelegramMessageIds,
    DateTime TelegramDate,
    int ImageCount,
    IReadOnlyList<string> ImageUrls,
    string? OriginalText,
    string? RewrittenText,
    string Status,
    int Attempts,
    string? LastError,
    string? ThreadsPostId,
    DateTime ReceivedAt,
    DateTime? RewrittenAt,
    DateTime? PublishedAt)
{
    public static PostDto From(PostRecord post, MediaOptions media) => new(
        post.Id,
        post.TelegramMessageIds,
        post.TelegramDate,
        post.ImageCount,
        post.ImageFiles.Select(media.UrlFor).ToList(),
        post.OriginalText,
        post.RewrittenText,
        post.Status,
        post.Attempts,
        post.LastError,
        post.ThreadsPostId,
        post.ReceivedAt,
        post.RewrittenAt,
        post.PublishedAt);
}
```

`Api/PostsEndpoints.cs`:

```csharp
using Microsoft.Extensions.Options;
using TeleThreads.Storage;

namespace TeleThreads.Api;

public static class PostsEndpoints
{
    public static void MapPostsApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/posts", (PostRepository repository, IOptions<MediaOptions> media, int? limit, string? status) =>
            repository.List(Math.Clamp(limit ?? 50, 1, 500), status).Select(p => PostDto.From(p, media.Value)));

        app.MapGet("/api/posts/{id:long}", (long id, PostRepository repository, IOptions<MediaOptions> media) =>
            repository.Get(id) is { } post ? Results.Ok(PostDto.From(post, media.Value)) : Results.NotFound());
    }
}
```

- [ ] **Step 4: Run — pass**

Run: `dotnet test --filter PostDtoTests` — Expected: 1 passed.

- [ ] **Step 5: Commit**

```bash
git add Api tests/TeleThreads.Tests/PostDtoTests.cs
git commit -m "Add posts GET API"
```

---

### Task 12: Program.cs — сборка всего вместе

**Files:**
- Modify: `Program.cs` (полная замена; echo-эндпоинты удаляются)

- [ ] **Step 1: Заменить Program.cs**

```csharp
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
builder.Services.Configure<MediaOptions>(builder.Configuration.GetSection("Media"));
// Keep Cyrillic readable in API responses instead of \uXXXX escapes.
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Encoder = JavaScriptEncoder.Create(UnicodeRanges.All));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<PostRepository>();
builder.Services.AddHttpClient("telegram");
builder.Services.AddHttpClient<IPostRewriter, GeminiRewriter>();
builder.Services.AddHttpClient<IThreadsTokenProvider, ThreadsTokenService>();
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

app.MapGet("/health", () => Results.Text("ok"));

// Threads downloads images by URL, so the photos taken from Telegram are served from here.
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(Path.GetFullPath(storage.MediaRoot)),
    RequestPath = "/media",
});

app.MapPostsApi();

app.Run();
```

- [ ] **Step 2: Build + все тесты**

Run: `dotnet build TeleThreads.slnx && dotnet test`
Expected: `Build succeeded`; все тесты (33) passed.

- [ ] **Step 3: Локальный запуск без секретов**

Run: `dotnet run --project TeleThreads.csproj` (в фоне), затем:
- `curl http://localhost:8080/health` → `ok`
- `curl http://localhost:8080/api/posts` → `[]`
- в логе: `Telegram:BotToken is not set; the Telegram scanner is disabled`

Остановить процесс, удалить созданный каталог `data/` (он в .gitignore, но не нужен).

- [ ] **Step 4: Commit**

```bash
git add Program.cs
git commit -m "Wire up scanner, pipeline and API; remove echo endpoints"
```

---

### Task 13: Docker и конфигурация окружения

**Files:**
- Modify: `Dockerfile`, `docker-compose.yml`
- Create: `.env.example`

- [ ] **Step 1: Dockerfile** — финальная стадия получает каталог `/data`, принадлежащий пользователю приложения (named volume унаследует владельца при первом создании):

```dockerfile
# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["TeleThreads.csproj", "."]
RUN dotnet restore "./TeleThreads.csproj"
COPY . .
RUN dotnet publish "./TeleThreads.csproj" -c $BUILD_CONFIGURATION -o /app/publish --no-restore /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
ENV Storage__DataPath=/data
RUN mkdir -p /data && chown $APP_UID /data
EXPOSE 8080
USER $APP_UID
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "TeleThreads.dll"]
```

- [ ] **Step 2: docker-compose.yml**

```yaml
services:
  telethreads:
    build: .
    image: telethreads:latest
    container_name: telethreads
    restart: unless-stopped
    env_file: .env
    ports:
      - "127.0.0.1:8088:8080"
    volumes:
      - telethreads-data:/data

volumes:
  telethreads-data:
```

- [ ] **Step 3: .env.example**

```
# Telegram bot added as an admin of the channel
Telegram__BotToken=
# Channel id like -1001234567890 (the app logs ids of channels it ignores)
Telegram__ChannelId=

Gemini__ApiKey=
Gemini__Model=gemini-3.5-flash

# Keep true until the Threads app and token are ready
Threads__DryRun=true
Threads__UserId=
Threads__AccessToken=

# Public address the photos are served from (Threads downloads them from here)
Media__PublicBaseUrl=https://telethreads.dergach.xyz
```

- [ ] **Step 4: Проверить образ локально (если Docker доступен)**

Run: `cp .env.example .env && docker compose up -d --build && curl http://127.0.0.1:8088/health && curl http://127.0.0.1:8088/api/posts && docker compose down && rm .env`
Expected: `ok`, затем `[]`. Если Docker на машине не запущен — шаг пропустить и отметить это.

- [ ] **Step 5: Commit**

```bash
git add Dockerfile docker-compose.yml .env.example
git commit -m "Add data volume and env configuration for Docker"
```

---

### Task 14: Инструкция по деплою

**Files:**
- Create: `docs/deploy.md`

Содержимое зависит от reverse proxy на сервере (nginx / Caddy / Traefik) — уточнить у пользователя перед написанием. Обязательные разделы:

1. **Подготовка секретов**: создать Telegram-бота у @BotFather, добавить его админом канала; получить Gemini API key в Google AI Studio.
2. **Обновление кода на сервере**: `cd <путь к проекту> && git pull`.
3. **`.env`**: `cp .env.example .env`, заполнить; `Telegram__ChannelId` можно оставить пустым, опубликовать тестовый пост и взять id из `docker logs telethreads` (строка `Ignoring post from chat ...`).
4. **Пересборка**: `docker compose up -d --build` — заменяет старый echo-контейнер (имя `telethreads` то же); `docker image prune -f` удаляет старый образ.
5. **Прокси и домен**: переключить конфиг с `echo.dergach.xyz` на `telethreads.dergach.xyz` → `127.0.0.1:8088`, выпустить сертификат, удалить старый хост.
6. **Проверка**: `curl https://telethreads.dergach.xyz/health`, опубликовать пост в канале, смотреть `docker logs -f telethreads` и `https://telethreads.dergach.xyz/api/posts`; открыть URL фото из ответа API.
7. **Включение Threads (позже)**: приложение в Meta for Developers с Threads API (`threads_basic`, `threads_content_publish`), получить long-lived token и user id, прописать в `.env`, `Threads__DryRun=false`, `docker compose up -d`.

- [ ] **Step 1: Написать `docs/deploy.md`** с командами под конкретный прокси пользователя.
- [ ] **Step 2: Commit**

```bash
git add docs/deploy.md
git commit -m "Add deployment guide"
```
