using System.Text.Json;
using Account.Api.Accounts;
using ForumReporting.Contracts.Events;
using ForumReporting.Contracts.Messaging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Account.Api.Messaging;

/// <summary>
/// Consumes <see cref="UserBlockRequested"/> from Account's queue.
/// <list type="bullet">
/// <item>Ack only AFTER the local transaction committed (crash before ack → redelivery → deduplicated by EventId).</item>
/// <item>Malformed message (strict contract JSON fails) → nack without requeue → dead-letter queue.</item>
/// <item>Transient failure (e.g. DB down) → nack with requeue after a short pause.</item>
/// </list>
/// </summary>
public sealed class UserBlockRequestedConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<RabbitMqOptions> options,
    ILogger<UserBlockRequestedConsumer> logger) : BackgroundService
{
    private IConnection? _connection;
    private IChannel? _channel;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Retry until the broker is reachable; after that the client library delivers messages to OnReceivedAsync.
        // RabbitMQ.Client's automatic connection + topology recovery (on by default) handles later network blips.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _connection = await options.Value.CreateFactory("account-block-consumer").CreateConnectionAsync(stoppingToken);
                _channel = await _connection.CreateChannelAsync(cancellationToken: stoppingToken);
                await RabbitMqTopology.DeclareAsync(_channel, stoppingToken);
                await _channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 10, global: false, stoppingToken);

                var consumer = new AsyncEventingBasicConsumer(_channel);
                consumer.ReceivedAsync += OnReceivedAsync;
                await _channel.BasicConsumeAsync(MessagingTopology.Queues.AccountUserBlockRequested, autoAck: false, consumer, stoppingToken);

                logger.LogInformation("Consuming {Queue}", MessagingTopology.Queues.AccountUserBlockRequested);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not start consuming (broker unreachable or topology mismatch); retrying");
                if (_connection is not null)
                {
                    await _connection.DisposeAsync(); // don't leak a connection per retry
                    _connection = null;
                }
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    private async Task OnReceivedAsync(object sender, BasicDeliverEventArgs delivery)
    {
        var channel = ((AsyncEventingBasicConsumer)sender).Channel;

        UserBlockRequested? evt;
        try
        {
            evt = JsonSerializer.Deserialize<UserBlockRequested>(delivery.Body.Span, ContractJson.Options)
                  ?? throw new JsonException("null payload");
        }
        catch (JsonException ex)
        {
            logger.LogError("Malformed message {MessageId} dead-lettered: {Error}", delivery.BasicProperties.MessageId, ex.Message);
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false);
            return;
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<BlockUserHandler>().HandleAsync(evt, CancellationToken.None);
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Processing {EventId} failed, will retry: {Error}", evt.EventId, ex.Message);
            await Task.Delay(TimeSpan.FromSeconds(2));
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (_channel is not null) await _channel.DisposeAsync();
        if (_connection is not null) await _connection.DisposeAsync();
    }
}
