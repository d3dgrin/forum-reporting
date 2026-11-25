using ForumReporting.Contracts.Messaging;
using RabbitMQ.Client;

namespace Reporting.Api.Outbox;

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    public string HostName { get; init; } = "localhost";
    public int Port { get; init; } = 5672;
    public string UserName { get; init; } = "guest";
    public string Password { get; init; } = "guest";

    public ConnectionFactory CreateFactory(string clientName) => new()
    {
        HostName = HostName,
        Port = Port,
        UserName = UserName,
        Password = Password,
        ClientProvidedName = clientName,
    };
}

public static class RabbitMqTopology
{
    /// <summary>
    /// Declares the full topology idempotently, including Account's queue and its binding, so a message published
    /// before Account.Api has ever started is still routed and stored (see <see cref="MessagingTopology"/>).
    /// </summary>
    public static async Task DeclareAsync(IChannel channel, CancellationToken cancellationToken)
    {
        await channel.ExchangeDeclareAsync(MessagingTopology.EventsExchange, ExchangeType.Topic, durable: true, cancellationToken: cancellationToken);
        await channel.ExchangeDeclareAsync(MessagingTopology.DeadLetterExchange, ExchangeType.Direct, durable: true, cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(MessagingTopology.Queues.AccountUserBlockRequested, durable: true, exclusive: false, autoDelete: false,
            arguments: MessagingTopology.QueueArguments.ForAccountUserBlockRequested(), cancellationToken: cancellationToken);
        await channel.QueueBindAsync(MessagingTopology.Queues.AccountUserBlockRequested, MessagingTopology.EventsExchange,
            MessagingTopology.RoutingKeys.UserBlockRequested, cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(MessagingTopology.Queues.AccountUserBlockRequestedDlq, durable: true, exclusive: false, autoDelete: false,
            cancellationToken: cancellationToken);
        await channel.QueueBindAsync(MessagingTopology.Queues.AccountUserBlockRequestedDlq, MessagingTopology.DeadLetterExchange,
            MessagingTopology.DeadLetterRoutingKeys.AccountUserBlockRequested, cancellationToken: cancellationToken);
    }
}
