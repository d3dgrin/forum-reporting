using Posts.Api.Posts;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(PostsCatalog.CreateSeeded());
builder.Services.AddProblemDetails();

var app = builder.Build();

// ProblemDetails also for framework-generated errors (e.g. 404 on an unknown route).
app.UseStatusCodePages();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.MapGet("/api/posts", (PostsCatalog catalog) => Results.Ok(catalog.All()));

app.MapGet("/api/posts/{postId}", (string postId, PostsCatalog catalog) =>
    catalog.Find(postId) is { } post
        ? Results.Ok(post)
        : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Post not found", detail: $"Post '{postId}' does not exist."));

app.Run();

// Exposed for WebApplicationFactory in integration tests.
public partial class Program;
