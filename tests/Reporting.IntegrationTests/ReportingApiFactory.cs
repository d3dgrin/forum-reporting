using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Reporting.Api.Persistence;
using Reporting.Api.Posts;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

[assembly: AssemblyFixture(typeof(Reporting.IntegrationTests.ReportingApiFactory))]

namespace Reporting.IntegrationTests;

/// <summary>
/// One real PostgreSQL (Testcontainers) and one Reporting.Api host for the whole test assembly.
/// The app applies its migrations on startup, exactly as in local runs. Only the Posts service is faked.
/// Tests isolate themselves by using unique user IDs, never by resetting the database.
/// </summary>
public sealed class ReportingApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17.12")
        .WithDatabase("reporting_db")
        .Build();

    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4.4-management").Build();

    public FakePosts Posts { get; } = new();

    public RabbitMQ.Client.ConnectionFactory RabbitFactory => new() { Uri = new Uri(_rabbit.GetConnectionString()) };

    public string ConnectionString => _postgres.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync());
        _ = Services; // build the host now → migrations run once, before any test
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder
            .UseEnvironment("Testing")
            .UseSetting("ConnectionStrings:Reporting", _postgres.GetConnectionString())
            .UseSetting("RabbitMq:HostName", _rabbit.Hostname)
            .UseSetting("RabbitMq:Port", _rabbit.GetMappedPublicPort(5672).ToString(System.Globalization.CultureInfo.InvariantCulture))
            .UseSetting("RabbitMq:UserName", RabbitMqBuilder.DefaultUsername)
            .UseSetting("RabbitMq:Password", RabbitMqBuilder.DefaultPassword)
            .UseSetting("Outbox:PollIntervalMilliseconds", "200")
            .ConfigureTestServices(services => services.AddSingleton<IPostsClient>(Posts));

    /// <summary>A fresh DbContext on its own connection, for assertions and for concurrency tests.</summary>
    public AsyncServiceScope NewScope() => Services.CreateAsyncScope();

    public ReportingDbContext Db(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<ReportingDbContext>();

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
        await _rabbit.DisposeAsync();
    }
}

/// <summary>Posts are created per test. <see cref="DownPostId"/> simulates an unavailable Posts service.</summary>
public sealed class FakePosts : IPostsClient
{
    public const string DownPostId = "post-down";

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PostInfo> _posts = new(StringComparer.Ordinal);

    public string NewPostBy(string authorId)
    {
        var postId = $"p-{Guid.NewGuid():N}";
        _posts[postId] = new PostInfo(postId, authorId);
        return postId;
    }

    public Task<PostInfo?> FindPostAsync(string postId, CancellationToken cancellationToken) =>
        postId == DownPostId
            ? throw new PostsServiceUnavailableException("simulated outage")
            : Task.FromResult(_posts.GetValueOrDefault(postId));
}

public static class TestUsers
{
    public static string New(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..24];
}
