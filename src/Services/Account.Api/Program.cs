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
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();                                                           // /openapi/v1.json
    app.UseSwaggerUI(o => o.SwaggerEndpoint("/openapi/v1.json", "Account.Api")); // /swagger
}

// Demo convenience: apply migrations (incl. seeded demo accounts) on startup.
await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<AccountDbContext>().Database.MigrateAsync();
}

app.UseStatusCodePages();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).ExcludeFromDescription();

// Demo inspection endpoint (no auth in the demo).
app.MapGet("/api/accounts/{userId}/status", async (string userId, AccountDbContext db, CancellationToken ct) =>
        await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.UserId == userId, ct) is { } account
            ? Results.Ok(new AccountStatusResponse(account.UserId, account.Status.ToString(), account.BlockedAt, account.BlockReason))
            : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Account not found"))
    .WithSummary("Account status (Active / Blocked)")
    .Produces<AccountStatusResponse>()
    .ProducesProblem(StatusCodes.Status404NotFound);

await app.RunAsync();

public sealed record AccountStatusResponse(string UserId, string Status, DateTimeOffset? BlockedAt, string? BlockReason);

// Exposed for WebApplicationFactory in integration tests.
public partial class Program;
