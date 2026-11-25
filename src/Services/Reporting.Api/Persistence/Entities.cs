namespace Reporting.Api.Persistence;

/// <summary>A stored report. At most one per (reporter, reported user): enforced by a unique index.</summary>
public sealed class Report
{
    public Guid Id { get; set; }
    public required string ReporterUserId { get; set; }
    public required string ReportedUserId { get; set; }
    /// <summary>The post that was reported (evidence for moderators). Not part of the uniqueness rule.</summary>
    public required string PostId { get; set; }
    public required string Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Number of DISTINCT reporters per reported user, maintained atomically in the same transaction as the report insert.
/// Kept alongside the reports (instead of COUNT(*) on every request) so the threshold check is one locked row.
/// </summary>
public sealed class ReportedUserCounter
{
    public required string ReportedUserId { get; set; }
    public int UniqueReportCount { get; set; }
    /// <summary>Set exactly once, when the count first reaches the threshold. Guards against duplicate block events.</summary>
    public DateTimeOffset? BlockRequestedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Transactional outbox: integration events committed together with the state change that caused them.</summary>
public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    public required string EventType { get; set; }
    /// <summary>The serialized event (jsonb).</summary>
    public required string Payload { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}
