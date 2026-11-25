using Posts.Api.Posts;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(PostsCatalog.CreateSeeded());
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();                                                              // /openapi/v1.json
    app.UseSwaggerUI(o => o.SwaggerEndpoint("/openapi/v1.json", "Posts.Api (stub)")); // /swagger
    app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();
}

// ProblemDetails also for framework-generated errors (e.g. 404 on an unknown route).
app.UseStatusCodePages();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).ExcludeFromDescription();

app.MapGet("/api/posts", (PostsCatalog catalog) => Results.Ok(catalog.All()))
    .Produces<IReadOnlyCollection<Post>>();

app.MapGet("/api/posts/{postId}", (string postId, PostsCatalog catalog) =>
    catalog.Find(postId) is { } post
        ? Results.Ok(post)
        : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Post not found", detail: $"Post '{postId}' does not exist."))
    .Produces<Post>()
    .ProducesProblem(StatusCodes.Status404NotFound);

app.Run();

// Exposed for WebApplicationFactory in integration tests.
public partial class Program;
