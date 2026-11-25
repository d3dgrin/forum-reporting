using Account.Api.Accounts;
using Account.Api.Messaging;
using Account.Api.Persistence;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Account")
    ?? throw new InvalidOperationException("Connection string 'Account' is not configured.");
builder.Services.AddDbContext<AccountDbContext>(o => o
    .UseNpgsql(connectionString)
    .UseSnakeCaseNamingConvention());

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<BlockUserHandler>();
builder.Services.AddOptions<RabbitMqOptions>().BindConfiguration(RabbitMqOptions.SectionName);
builder.Services.AddHostedService<UserBlockRequestedConsumer>();
builder.Services.AddProblemDetails();

var app = builder.Build();

// Demo convenience: apply migrations (incl. seeded demo accounts) on startup.
await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<AccountDbContext>().Database.MigrateAsync();
}

app.UseStatusCodePages();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

// Demo inspection endpoint (no auth in the demo).
app.MapGet("/api/accounts/{userId}/status", async (string userId, AccountDbContext db, CancellationToken ct) =>
    await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.UserId == userId, ct) is { } account
        ? Results.Ok(new { account.UserId, Status = account.Status.ToString(), account.BlockedAt, account.BlockReason })
        : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Account not found"));

await app.RunAsync();

// Exposed for WebApplicationFactory in integration tests.
public partial class Program;
