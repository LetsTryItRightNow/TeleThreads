using System.Text.Encodings.Web;
using System.Text.Unicode;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using TeleThreads.Storage;
using TeleThreads.Threads;

namespace TeleThreads.Api;

// Pages the account owner opens in a browser to connect Threads, plus Meta's deauthorization callbacks.
public static class ThreadsAuthEndpoints
{
    private static readonly HtmlEncoder Html = HtmlEncoder.Create(UnicodeRanges.All);

    public static void MapThreadsAuth(this IEndpointRouteBuilder app)
    {
        app.MapGet("/threads/login", (IOptions<ThreadsOptions> options, ThreadsAuthClient auth, OAuthStateStore states) =>
            string.IsNullOrEmpty(options.Value.AppId) || string.IsNullOrEmpty(options.Value.AppSecret)
                ? Page(StatusCodes.Status503ServiceUnavailable, "Подключение Threads не настроено.")
                : Results.Redirect(auth.BuildAuthorizeUrl(states.Create())));

        app.MapGet("/threads/callback", async (
            string? code,
            string? state,
            string? error,
            [FromQuery(Name = "error_description")] string? errorDescription,
            ThreadsConnector connector,
            IOptions<MediaOptions> media,
            ILoggerFactory loggers,
            CancellationToken ct) =>
        {
            if (!string.IsNullOrEmpty(error))
                return Page(StatusCodes.Status400BadRequest, "Доступ не выдан.", errorDescription);

            var loginAgain = Page(StatusCodes.Status400BadRequest,
                "Ссылка устарела или уже использована. Откройте ссылку для подключения ещё раз:", LoginUrl(media.Value));
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
                return loginAgain;

            try
            {
                var account = await connector.ConnectAsync(code, state, ct);
                return Page(StatusCodes.Status200OK,
                    $"Готово! Аккаунт @{account.Username} подключён.", "Эту страницу можно закрыть.");
            }
            catch (InvalidOAuthStateException)
            {
                return loginAgain;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                loggers.CreateLogger(typeof(ThreadsAuthEndpoints)).LogError(ex, "Connecting the Threads account failed");
                return Page(StatusCodes.Status502BadGateway, "Не удалось подключить аккаунт, попробуйте ещё раз.");
            }
        });

        app.MapGet("/threads/status", (ThreadsAccountStore store, IOptions<ThreadsOptions> options) =>
        {
            var account = store.Get();
            return Results.Ok(new
            {
                connected = account is not null,
                username = account?.Username,
                tokenCheckedAt = account is null ? null : store.CheckedAt,
                dryRun = options.Value.DryRun,
            });
        });

        // Meta calls this when the owner removes the app in Threads settings.
        app.MapPost("/threads/uninstall", async (HttpRequest request, ThreadsAccountStore store,
            IOptions<ThreadsOptions> options, ILoggerFactory loggers) =>
            await ClearAccountAsync(request, store, options.Value, loggers, "uninstall")
                ? Results.Ok()
                : Results.BadRequest());

        // Meta calls this when the owner asks to delete their data.
        app.MapPost("/threads/delete", async (HttpRequest request, ThreadsAccountStore store,
            IOptions<ThreadsOptions> options, IOptions<MediaOptions> media, ILoggerFactory loggers) =>
            await ClearAccountAsync(request, store, options.Value, loggers, "data deletion")
                ? Results.Json(new
                {
                    url = $"{media.Value.PublicBaseUrl.TrimEnd('/')}/threads/status",
                    confirmation_code = Guid.NewGuid().ToString("N"),
                })
                : Results.BadRequest());
    }

    // Reads the form by hand to stay clear of minimal-API form binding and its antiforgery requirement.
    private static async Task<bool> ClearAccountAsync(
        HttpRequest request, ThreadsAccountStore store, ThreadsOptions options, ILoggerFactory loggers, string callback)
    {
        if (!request.HasFormContentType)
            return false;
        var form = await request.ReadFormAsync(request.HttpContext.RequestAborted);
        if (!SignedRequest.TryParse(form["signed_request"].ToString(), options.AppSecret, out var payload))
            return false;

        store.Clear();
        loggers.CreateLogger(typeof(ThreadsAuthEndpoints)).LogWarning(
            "Threads account removed by Meta {Callback} callback for user {UserId}", callback, payload?["user_id"]);
        return true;
    }

    private static string LoginUrl(MediaOptions media) => $"{media.PublicBaseUrl.TrimEnd('/')}/threads/login";

    // Every paragraph is HTML-encoded, so callers may pass values that came from the request.
    private static IResult Page(int statusCode, params string?[] paragraphs)
    {
        var body = string.Concat(paragraphs.Where(p => !string.IsNullOrEmpty(p)).Select(p => $"<p>{Html.Encode(p!)}</p>"));
        var html = $$"""
            <!doctype html>
            <html lang="ru">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>TeleThreads</title>
            </head>
            <body style="font-family: sans-serif; display: flex; flex-direction: column; justify-content: center; align-items: center; min-height: 90vh; margin: 0 16px; text-align: center;">
            {{body}}
            </body>
            </html>
            """;
        return Results.Content(html, "text/html; charset=utf-8", statusCode: statusCode);
    }
}
