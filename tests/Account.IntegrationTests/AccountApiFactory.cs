using System.Globalization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

[assembly: AssemblyFixture(typeof(Account.IntegrationTests.AccountApiFactory))]

namespace Account.IntegrationTests;

/// <summary>Account.Api host against real PostgreSQL + RabbitMQ (Testcontainers), consumer running.</summary>
public sealed class AccountApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17.12").WithDatabase("account_db").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4.4-management").Build();

    public RabbitMQ.Client.ConnectionFactory RabbitFactory => new() { Uri = new Uri(_rabbit.GetConnectionString()) };

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync());
        _ = Services; // build host → migrations + consumer start
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder
            .UseEnvironment("Testing")
            .UseSetting("ConnectionStrings:Account", _postgres.GetConnectionString())
            .UseSetting("RabbitMq:HostName", _rabbit.Hostname)
            .UseSetting("RabbitMq:Port", _rabbit.GetMappedPublicPort(5672).ToString(CultureInfo.InvariantCulture))
            .UseSetting("RabbitMq:UserName", RabbitMqBuilder.DefaultUsername)
            .UseSetting("RabbitMq:Password", RabbitMqBuilder.DefaultPassword);

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
        await _rabbit.DisposeAsync();
    }
}
