using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Reporting.Api.Auth;
using Reporting.Api.Persistence;
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

// --- Persistence (reporting_db, owned by this service only) ---
var connectionString = builder.Configuration.GetConnectionString("Reporting")
    ?? throw new InvalidOperationException("Connection string 'Reporting' is not configured.");
builder.Services.AddDbContext<ReportingDbContext>(o => o
    .UseNpgsql(connectionString)
    .UseSnakeCaseNamingConvention());

// --- Reports ---
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddOptions<ReportingOptions>()
    .BindConfiguration(ReportingOptions.SectionName)
    .Validate(o => o.BlockThreshold >= 1, "Reporting:BlockThreshold must be >= 1.")
    .ValidateOnStart();
builder.Services.AddScoped<IReportService, PostgresReportService>();
builder.Services.AddScoped<SubmitReportHandler>();

var app = builder.Build();

app.Logger.LogWarning("Using DEMO header authentication ({Header}). Not for production use.", DemoHeaderAuthenticationHandler.HeaderName);

// Prototype convenience: apply migrations on startup (EF takes a database lock, so parallel instances are safe).
// Production: run migrations as a separate deployment step.
await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<ReportingDbContext>().Database.MigrateAsync();
}

app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.MapPost("/api/reports",
        (SubmitReportRequest? request, HttpContext http, SubmitReportHandler handler, CancellationToken ct) =>
            handler.HandleAsync(http.User.GetRequiredUserId(), request, ct))
    .RequireAuthorization();

// Demo/support view of the distinct-reporter count and block-request state.
// Production: require a moderator/support role, not just any authenticated user.
app.MapGet("/api/reports/users/{userId}/summary", ReportSummaryEndpoint.HandleAsync)
    .RequireAuthorization();

await app.RunAsync();

// Exposed for WebApplicationFactory in integration tests.
public partial class Program;
