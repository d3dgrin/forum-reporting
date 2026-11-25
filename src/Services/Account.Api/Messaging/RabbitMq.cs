using ForumReporting.Contracts.Messaging;
using RabbitMQ.Client;

namespace Account.Api.Messaging;

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

/// <summary>
/// VERBATIM copy of Reporting.Api's RabbitMqTopology (declarations must be identical on both sides, or RabbitMQ answers
/// 406 PRECONDITION_FAILED). Kept as a copy so Contracts stays free of a RabbitMQ.Client dependency.
/// </summary>
public static class RabbitMqTopology
{
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
