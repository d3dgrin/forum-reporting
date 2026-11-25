using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Reporting.Api.Auth;
using Reporting.Api.Posts;
using Reporting.Api.Reports;

var builder = WebApplication.CreateBuilder(args);

// --- Authentication (DEMO ONLY, see DemoHeaderAuthenticationHandler) ---
// Allow-list, not deny-list: Staging/QA/a typo'd environment name must not silently enable impersonation.
if (!builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing"))
{
    throw new InvalidOperationException(
        $"DemoHeader authentication trusts a client-supplied header and may only run in Development or Testing (current: '{builder.Environment.EnvironmentName}').");
}

builder.Services
    .AddAuthentication(DemoHeaderAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, DemoHeaderAuthenticationHandler>(DemoHeaderAuthenticationHandler.SchemeName, null);
builder.Services.AddAuthorization();
builder.Services.AddProblemDetails();

// --- Posts service client ---
builder.Services.AddOptions<PostsClientOptions>()
    .BindConfiguration(PostsClientOptions.SectionName)
    .Validate(o => o.BaseUrl is { IsAbsoluteUri: true }, "Services:Posts:BaseUrl must be an absolute URL.")
    .Validate(o => o.TimeoutSeconds is > 0 and <= 30, "Services:Posts:TimeoutSeconds must be 1-30.")
    .ValidateOnStart();
builder.Services.AddHttpClient<IPostsClient, PostsClient>((sp, http) =>
{
    var options = sp.GetRequiredService<IOptions<PostsClientOptions>>().Value;
    // Trailing slash so relative request paths append instead of replacing the last segment.
    http.BaseAddress = new Uri(options.BaseUrl.AbsoluteUri.TrimEnd('/') + "/");
    http.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
});

// --- Reports ---
// TODO(iteration-3): DELETE this line and register the PostgreSQL IReportService instead (single registration).
builder.Services.AddSingleton<IReportService, InMemoryReportService>();
builder.Services.AddScoped<SubmitReportHandler>();

var app = builder.Build();

app.Logger.LogWarning("Using DEMO header authentication ({Header}). Not for production use.", DemoHeaderAuthenticationHandler.HeaderName);

app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.MapPost("/api/reports",
        (SubmitReportRequest? request, HttpContext http, SubmitReportHandler handler, CancellationToken ct) =>
            handler.HandleAsync(http.User.GetRequiredUserId(), request, ct))
    .RequireAuthorization();

app.Run();

// Exposed for WebApplicationFactory in integration tests.
public partial class Program;
