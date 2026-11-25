using System.Data;
using System.Text.Json;
using ForumReporting.Contracts.Events;
using ForumReporting.Contracts.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Reporting.Api.Persistence;

namespace Reporting.Api.Reports;

/// <summary>
/// Stores a report and maintains the distinct-reporter count in ONE PostgreSQL transaction.
/// The correctness argument below REQUIRES <see cref="IsolationLevel.ReadCommitted"/> (set explicitly):
/// <list type="number">
/// <item><b>Insert report</b> with <c>ON CONFLICT (reporter, reported) DO NOTHING</c>. No row returned → duplicate → rollback.
///   A concurrent insert of the same pair waits on the unique index until the other transaction finishes, then sees the conflict.</item>
/// <item><b>Upsert counter</b> (+1) <c>RETURNING count, block_requested_at</c>. The row lock taken here serialises concurrent
///   reports against the same user until commit; the increment is applied to the latest committed row version, so no
///   increment is lost, and the returned values are authoritative while the lock is held.</item>
/// <item><b>Request block once</b>: because of that lock, exactly one transaction sees <c>count &gt;= threshold</c> with
///   <c>block_requested_at IS NULL</c>. The conditional <c>UPDATE … WHERE block_requested_at IS NULL</c> is a cheap safety net
///   that makes the "once" rule explicit in SQL; the guarantee comes from the row lock + RETURNING.</item>
/// <item><b>Outbox insert</b> of <see cref="UserBlockRequested"/>, committed atomically with the report.</item>
/// </list>
/// Under REPEATABLE READ / SERIALIZABLE the same statements would raise serialization failures (40001) instead.
/// </summary>
public sealed class PostgresReportService(
    ReportingDbContext db,
    TimeProvider time,
    IOptions<ReportingOptions> options,
    ILogger<PostgresReportService> logger) : IReportService
{
    /// <summary>
    /// Ad-hoc result type for <c>SqlQuery</c>. Its properties map to <c>unique_report_count</c> / <c>block_requested_at</c>
    /// only because the snake_case naming convention also applies to ad-hoc types: removing UseSnakeCaseNamingConvention()
    /// breaks this mapping (caught by the threshold integration tests).
    /// </summary>
    private sealed record CounterRow(int UniqueReportCount, DateTimeOffset? BlockRequestedAt);

    public async Task<SubmitReportResult> SubmitAsync(NewReport report, CancellationToken cancellationToken)
    {
        // PostgreSQL timestamptz has microsecond precision (.NET: 100 ns). Truncate once so the stored
        // block_requested_at and the event's RequestedAt are exactly the same instant.
        var utcNow = time.GetUtcNow();
        var now = utcNow.AddTicks(-(utcNow.Ticks % TimeSpan.TicksPerMicrosecond));
        var threshold = options.Value.BlockThreshold;

        // Note: if EnableRetryOnFailure is ever configured, this block must run inside Database.CreateExecutionStrategy().
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        // 1. Insert the report unless this reporter already reported this user.
        var reportId = Guid.CreateVersion7(now);
        var inserted = await db.Database.SqlQuery<Guid>($"""
            INSERT INTO reports (id, reporter_user_id, reported_user_id, post_id, reason, created_at)
            VALUES ({reportId}, {report.ReporterUserId}, {report.ReportedUserId}, {report.PostId}, {report.Reason}, {now})
            ON CONFLICT (reporter_user_id, reported_user_id) DO NOTHING
            RETURNING id AS "Value"
            """).ToListAsync(cancellationToken);

        if (inserted.Count == 0)
        {
            // Not cancellable: the outcome is already decided; a cancelled rollback would turn a clean 409 into a 500.
            await tx.RollbackAsync(CancellationToken.None);
            return new SubmitReportResult.Duplicate();
        }

        // 2. Count it (only reached when the report is new).
        // ToListAsync, not SingleAsync: EF must not compose (LIMIT) over a non-composable INSERT … RETURNING.
        var counter = (await db.Database.SqlQuery<CounterRow>($"""
            INSERT INTO reported_user_counters (reported_user_id, unique_report_count, block_requested_at, updated_at)
            VALUES ({report.ReportedUserId}, 1, NULL, {now})
            ON CONFLICT (reported_user_id) DO UPDATE
                SET unique_report_count = reported_user_counters.unique_report_count + 1,
                    updated_at = EXCLUDED.updated_at
            RETURNING unique_report_count, block_requested_at
            """).ToListAsync(cancellationToken)).Single();

        var blockRequestedAt = counter.BlockRequestedAt;

        // 3. Cross the threshold at most once per user.
        if (blockRequestedAt is null && counter.UniqueReportCount >= threshold)
        {
            var claimed = await db.Database.ExecuteSqlAsync($"""
                UPDATE reported_user_counters
                SET block_requested_at = {now}
                WHERE reported_user_id = {report.ReportedUserId}
                  AND block_requested_at IS NULL
                  AND unique_report_count >= {threshold}
                """, cancellationToken);

            if (claimed == 1)
            {
                blockRequestedAt = now;

                // 4. The block request travels with the report commit; publishing happens later (outbox worker).
                var evt = new UserBlockRequested(
                    EventId: Guid.CreateVersion7(now),
                    UserId: report.ReportedUserId,
                    ReportCount: counter.UniqueReportCount,
                    Reason: $"Reported by {counter.UniqueReportCount} distinct users",
                    RequestedAt: now);
                var payload = JsonSerializer.Serialize(evt, ContractJson.Options);

                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO outbox_messages (id, event_type, payload, created_at, attempts)
                    VALUES ({evt.EventId}, {UserBlockRequested.EventType}, {payload}::jsonb, {now}, 0)
                    """, cancellationToken);

                logger.LogInformation("Threshold reached for {ReportedUserId} ({Count} distinct reporters); block requested, event {EventId}",
                    report.ReportedUserId, counter.UniqueReportCount, evt.EventId);
            }
        }

        // Not cancellable: once we decide to commit, a client disconnect must not turn a (possibly) committed
        // report into a 500. Cancellation is honoured on the data statements above (including lock waits).
        await tx.CommitAsync(CancellationToken.None);

        return new SubmitReportResult.Created(reportId, counter.UniqueReportCount, BlockRequested: blockRequestedAt is not null);
    }
}
