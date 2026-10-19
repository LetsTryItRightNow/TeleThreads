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
