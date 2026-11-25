using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Account.Api.Accounts;
using Account.Api.Persistence;
using ForumReporting.Contracts.Events;
using ForumReporting.Contracts.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;

namespace Account.IntegrationTests;

public class BlockUserTests(AccountApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record StatusResponse(string UserId, string Status, DateTimeOffset? BlockedAt, string? BlockReason);

    private static UserBlockRequested NewEvent(string userId) =>
        new(Guid.CreateVersion7(), userId, 5, "Reported by 5 distinct users", DateTimeOffset.UtcNow);

    private async Task<BlockOutcome> Handle(UserBlockRequested evt)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<BlockUserHandler>().HandleAsync(evt, Ct);
    }

    private async Task<UserAccount?> Account(string userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AccountDbContext>().Accounts.AsNoTracking()
            .SingleOrDefaultAsync(a => a.UserId == userId, Ct);
    }

    [Fact] // plan tests 5 + 6
    public async Task Event_blocks_the_account_once_and_redelivery_changes_nothing()
    {
        var userId = $"user-{Guid.NewGuid():N}"[..20];
        var evt = NewEvent(userId);

        (await Handle(evt)).ShouldBe(BlockOutcome.Blocked);
        var first = await Account(userId);
        first!.Status.ShouldBe(AccountStatus.Blocked);
        first.BlockReason.ShouldBe(evt.Reason);

        (await Handle(evt)).ShouldBe(BlockOutcome.AlreadyProcessed);
        (await Account(userId))!.BlockedAt.ShouldBe(first.BlockedAt);
    }

    [Fact]
    public async Task Published_event_is_consumed_and_status_endpoint_shows_Blocked()
    {
        // NOTE: only this test may use the seeded 'target-2' (the test DB is shared across tests).
        var client = factory.CreateClient();
        (await client.GetFromJsonAsync<StatusResponse>("/api/accounts/target-2/status", Ct))!.Status.ShouldBe("Active"); // seeded

        await using var connection = await factory.RabbitFactory.CreateConnectionAsync(Ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Ct);
        // The consumer declares the topology on a background thread; declare it here too (idempotent) so the
        // publish can't race the consumer's startup and be silently unroutable.
        await global::Account.Api.Messaging.RabbitMqTopology.DeclareAsync(channel, Ct);
        var evt = NewEvent("target-2");
        await channel.BasicPublishAsync(MessagingTopology.EventsExchange, MessagingTopology.RoutingKeys.UserBlockRequested,
            mandatory: true, new BasicProperties { MessageId = evt.EventId.ToString(), Persistent = true },
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(evt, ContractJson.Options)), Ct);

        var deadline = DateTime.UtcNow.AddSeconds(20);
        StatusResponse? status;
        do
        {
            await Task.Delay(200, Ct);
            status = await client.GetFromJsonAsync<StatusResponse>("/api/accounts/target-2/status", Ct);
        }
        while (status!.Status != "Blocked" && DateTime.UtcNow < deadline);

        status.Status.ShouldBe("Blocked");
        status.BlockReason.ShouldBe(evt.Reason);
    }
}
