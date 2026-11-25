namespace ForumReporting.Contracts.Events;

/// <summary>
/// Integration event published by Reporting when a user reaches the distinct-reporter threshold.
/// Account owns the actual status change; this event is only a request.
/// </summary>
/// <remarks>
/// Wire contract (JSON, camelCase via <see cref="Messaging.ContractJson.Options"/>).
/// Evolve additively only: new optional properties are fine; renames/removals need a new version
/// (new <see cref="EventType"/> and routing key) so existing consumers keep working.
/// </remarks>
/// <param name="EventId">Unique id; consumers use it for idempotency (at-least-once delivery).</param>
/// <param name="UserId">The user to block.</param>
/// <param name="ReportCount">Distinct-reporter count when the threshold was reached.</param>
/// <param name="Reason">Human-readable reason, e.g. "Reported by 5 distinct users".</param>
/// <param name="RequestedAt">When Reporting decided to request the block (UTC).</param>
public sealed record UserBlockRequested(
    Guid EventId,
    string UserId,
    int ReportCount,
    string Reason,
    DateTimeOffset RequestedAt)
{
    /// <summary>Stable, versioned type name stored in the outbox and sent as the AMQP message type.</summary>
    public const string EventType = "forum.reporting.user-block-requested.v1";
}
