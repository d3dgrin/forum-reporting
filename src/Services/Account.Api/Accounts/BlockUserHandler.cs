using System.Data;
using Account.Api.Persistence;
using ForumReporting.Contracts.Events;
using Microsoft.EntityFrameworkCore;

namespace Account.Api.Accounts;

public enum BlockOutcome
{
    Blocked,
    AlreadyProcessed,
}

/// <summary>
/// Applies a <see cref="UserBlockRequested"/> exactly once per EventId, in one local transaction:
/// record the EventId (dedup) + set the account to Blocked. Delivery is at-least-once, so redeliveries
/// and duplicate publishes hit the processed_messages primary key and change nothing.
/// </summary>
public sealed class BlockUserHandler(AccountDbContext db, TimeProvider time, ILogger<BlockUserHandler> logger)
{
    public async Task<BlockOutcome> HandleAsync(UserBlockRequested evt, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        // Two consumers racing on the same redelivered event: the second waits on the PK, then gets no row.
        var firstTime = await db.Database.SqlQuery<Guid>($"""
            INSERT INTO processed_messages (event_id, processed_at) VALUES ({evt.EventId}, {now})
            ON CONFLICT (event_id) DO NOTHING
            RETURNING event_id AS "Value"
            """).ToListAsync(cancellationToken);

        if (firstTime.Count == 0)
        {
            await tx.RollbackAsync(CancellationToken.None);
            logger.LogInformation("Event {EventId} already processed; ignoring duplicate delivery", evt.EventId);
            return BlockOutcome.AlreadyProcessed;
        }

        // Upsert: a user unknown to this demo DB is created as Blocked rather than losing the request.
        // An already-blocked account keeps its original BlockedAt/BlockReason.
        var blocked = nameof(AccountStatus.Blocked);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO accounts (user_id, status, blocked_at, block_reason)
            VALUES ({evt.UserId}, {blocked}, {now}, {evt.Reason})
            ON CONFLICT (user_id) DO UPDATE
                SET status = EXCLUDED.status,
                    blocked_at = COALESCE(accounts.blocked_at, EXCLUDED.blocked_at),
                    block_reason = COALESCE(accounts.block_reason, EXCLUDED.block_reason)
            """, cancellationToken);

        await tx.CommitAsync(CancellationToken.None);
        logger.LogInformation("Account {UserId} BLOCKED ({Reason}); event {EventId}", evt.UserId, evt.Reason, evt.EventId);
        return BlockOutcome.Blocked;
    }
}
