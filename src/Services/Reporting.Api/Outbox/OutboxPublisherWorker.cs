using System.Text;
using ForumReporting.Contracts.Events;
using ForumReporting.Contracts.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using Reporting.Api.Persistence;

namespace Reporting.Api.Outbox;

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    public int PollIntervalMilliseconds { get; init; } = 2000;
    public int BatchSize { get; init; } = 20;
}

/// <summary>
/// Transactional-outbox relay: publishes committed-but-unpublished events to RabbitMQ, then marks them published.
/// <list type="bullet">
/// <item>Rows are claimed with <c>FOR UPDATE SKIP LOCKED</c>, so several instances never publish the same row concurrently.</item>
/// <item>Publisher confirms + <c>mandatory</c>: a row is marked published only after the broker confirmed it AND routed it to a queue.</item>
/// <item>At-least-once: a crash between the confirm and the <c>published_at</c> update republishes the event later.
///   That is fine because Account.Api deduplicates by EventId (= AMQP MessageId).</item>
/// </list>
/// </summary>
public sealed class OutboxPublisherWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<RabbitMqOptions> rabbitOptions,
    IOptions<OutboxOptions> outboxOptions,
    TimeProvider time,
    ILogger<OutboxPublisherWorker> logger) : BackgroundService
{
    private IConnection? _connection;
    private IChannel? _channel;
    private bool _resetChannel;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMilliseconds(outboxOptions.Value.PollIntervalMilliseconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var channel = await EnsureChannelAsync(stoppingToken);
                var published = await PublishBatchAsync(channel, stoppingToken);
                if (published > 0)
                {
                    continue; // more may be waiting
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Broker or DB unavailable: rows stay unpublished and are retried on the next poll.
                logger.LogWarning("Outbox publish cycle failed: {Error}", ex.Message);
                await ResetChannelAsync();
            }

            await Task.Delay(interval, time, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private async Task<int> PublishBatchAsync(IChannel channel, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ReportingDbContext>();

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var batch = await db.OutboxMessages
            .FromSql($"""
                SELECT * FROM outbox_messages
                WHERE published_at IS NULL
                ORDER BY created_at
                LIMIT {outboxOptions.Value.BatchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);

        var published = 0;
        foreach (var message in batch)
        {
            try
            {
                var properties = new BasicProperties
                {
                    MessageId = message.Id.ToString(),
                    Type = message.EventType,
                    ContentType = "application/json",
                    DeliveryMode = DeliveryModes.Persistent,
                    Timestamp = new AmqpTimestamp(message.CreatedAt.ToUnixTimeSeconds()),
                };

                // Awaits the broker confirm; throws PublishException on nack or basic.return (unroutable).
                await channel.BasicPublishAsync(MessagingTopology.EventsExchange, RoutingKeyFor(message.EventType),
                    mandatory: true, properties, Encoding.UTF8.GetBytes(message.Payload), cancellationToken);

                message.PublishedAt = time.GetUtcNow();
                published++;
                logger.LogInformation("Published {EventType} {EventId}", message.EventType, message.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                message.Attempts++;
                message.LastError = ex.Message;
                logger.LogWarning("Publishing {EventId} failed (attempt {Attempts}): {Error}", message.Id, message.Attempts, ex.Message);
                // Reconnect + redeclare topology next cycle (e.g. queue/binding deleted → unroutable).
                // Note: head-of-line by design (single event type in this demo); rows keep their order.
                _resetChannel = true;
                break;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return published;
    }

    private static string RoutingKeyFor(string eventType) => eventType switch
    {
        UserBlockRequested.EventType => MessagingTopology.RoutingKeys.UserBlockRequested,
        _ => throw new InvalidOperationException($"No routing key for event type '{eventType}'."),
    };

    private async Task<IChannel> EnsureChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true } && !_resetChannel)
        {
            return _channel;
        }

        await ResetChannelAsync();
        _connection = await rabbitOptions.Value.CreateFactory("reporting-outbox-publisher").CreateConnectionAsync(cancellationToken);
        _channel = await _connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            cancellationToken);
        await RabbitMqTopology.DeclareAsync(_channel, cancellationToken);
        logger.LogInformation("Outbox publisher connected to RabbitMQ");
        return _channel;
    }

    private async Task ResetChannelAsync()
    {
        try
        {
            if (_channel is not null) await _channel.DisposeAsync();
            if (_connection is not null) await _connection.DisposeAsync();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Ignoring error while closing RabbitMQ connection");
        }
        finally
        {
            _channel = null;
            _connection = null;
            _resetChannel = false;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await ResetChannelAsync();
    }
}
