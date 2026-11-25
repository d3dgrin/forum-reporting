using System.Text;
using System.Text.Json;
using ForumReporting.Contracts.Events;
using ForumReporting.Contracts.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using Reporting.Api.Reports;

namespace Reporting.IntegrationTests;

/// <summary>Main flow: 5th distinct report → outbox row → worker publishes to Account's queue → row marked published.</summary>
public class OutboxPublishingTests(ReportingApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Block_request_is_published_to_the_account_queue_and_marked_published()
    {
        var target = TestUsers.New("target");
        for (var i = 0; i < 5; i++)
        {
            await using var scope = factory.NewScope();
            await scope.ServiceProvider.GetRequiredService<IReportService>()
                .SubmitAsync(new NewReport(TestUsers.New("r"), target, "post-x", "Spam"), Ct);
        }

        // The worker marks the row published after the broker confirmed and routed it.
        var message = await Eventually(async () =>
        {
            await using var scope = factory.NewScope();
            return await factory.Db(scope).OutboxMessages.AsNoTracking().SingleOrDefaultAsync(m =>
                m.PublishedAt != null && EF.Functions.JsonContains(m.Payload, JsonSerializer.Serialize(new { userId = target })), Ct);
        });

        // The message is on Account's queue (declared by the publisher), with MessageId = EventId.
        await using var connection = await factory.RabbitFactory.CreateConnectionAsync(Ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Ct);
        var delivered = await Eventually(async () =>
        {
            // Other tests' events share the queue: scan until ours shows up.
            while (await channel.BasicGetAsync(MessagingTopology.Queues.AccountUserBlockRequested, autoAck: true, Ct) is { } get)
            {
                if (get.BasicProperties.MessageId == message.Id.ToString())
                {
                    return get;
                }
            }
            return null;
        });

        delivered.BasicProperties.Type.ShouldBe(UserBlockRequested.EventType);
        delivered.BasicProperties.DeliveryMode.ShouldBe(DeliveryModes.Persistent);
        var evt = JsonSerializer.Deserialize<UserBlockRequested>(Encoding.UTF8.GetString(delivered.Body.Span), ContractJson.Options)!;
        evt.UserId.ShouldBe(target);
        evt.ReportCount.ShouldBe(5);
        evt.EventId.ShouldBe(message.Id);
    }

    private static async Task<T> Eventually<T>(Func<Task<T?>> probe) where T : class
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (await probe() is { } value)
            {
                return value;
            }
            await Task.Delay(200, Ct);
        }
        throw new TimeoutException("Condition not met within 20 s.");
    }
}
