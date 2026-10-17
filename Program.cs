var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/health", () => Results.Text("ok"));

// Echo: returns the request body as-is with the same content type
app.MapPost("/echo", async (HttpRequest request) =>
{
    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync();
    return Results.Content(body, request.ContentType ?? "text/plain");
});

// Echo via query string: GET /echo?msg=hello
app.MapGet("/echo", (string? msg) => Results.Text(msg ?? string.Empty));

app.Run();
