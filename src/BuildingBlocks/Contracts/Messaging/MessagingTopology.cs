namespace ForumReporting.Contracts.Messaging;

/// <summary>
/// Broker topology shared by publishers and consumers.
/// </summary>
/// <remarks>
/// Both Reporting (publisher) and Account (consumer) declare the FULL topology idempotently at startup:
/// exchanges, the Account queue with <see cref="QueueArguments"/>, the DLQ, and the bindings.
/// Declaring the consumer's queue on the publisher side means a message published before Account.Api
/// has ever started is still routed and stored. The publisher also publishes with <c>mandatory: true</c>
/// and treats a basic.return (unroutable) as a failure, so the outbox row stays unpublished and is retried.
/// The queue arguments must be identical on every declaration, otherwise RabbitMQ rejects the
/// redeclaration with 406 PRECONDITION_FAILED.
/// </remarks>
public static class MessagingTopology
{
    /// <summary>Durable <b>topic</b> exchange for all forum integration events.</summary>
    public const string EventsExchange = "forum.events";

    /// <summary>Durable <b>direct</b> exchange receiving messages that consumers reject (poison messages).</summary>
    public const string DeadLetterExchange = "forum.events.dlx";

    public static class RoutingKeys
    {
        public const string UserBlockRequested = "reporting.user-block-requested.v1";
    }

    public static class DeadLetterRoutingKeys
    {
        public const string AccountUserBlockRequested = "account.user-block-requested.dlq";
    }

    public static class Queues
    {
        /// <summary>Account service's durable (quorum) queue for block requests.</summary>
        public const string AccountUserBlockRequested = "account.user-block-requested";

        /// <summary>Dead-letter queue paired with <see cref="AccountUserBlockRequested"/>.</summary>
        public const string AccountUserBlockRequestedDlq = "account.user-block-requested.dlq";
    }

    /// <summary>Arguments that every declaration of <see cref="Queues.AccountUserBlockRequested"/> must use.</summary>
    public static class QueueArguments
    {
        public const string QueueTypeKey = "x-queue-type";
        public const string QueueType = "quorum";
        public const string DeadLetterExchangeKey = "x-dead-letter-exchange";
        public const string DeadLetterRoutingKeyKey = "x-dead-letter-routing-key";

        /// <summary>Returns a new dictionary on each call, so callers cannot mutate a shared instance.</summary>
        public static IDictionary<string, object?> ForAccountUserBlockRequested() => new Dictionary<string, object?>
        {
            [QueueTypeKey] = QueueType,
            [DeadLetterExchangeKey] = DeadLetterExchange,
            [DeadLetterRoutingKeyKey] = DeadLetterRoutingKeys.AccountUserBlockRequested,
        };
    }
}
