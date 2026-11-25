# Iteration 3 — Reporting persistence and atomic transaction (core)

**Agent role:** Developer + Test agent (Claude Code, Opus 5.5).

**Prompt (summary):** "Implement Iteration 3: ReportingDbContext (reports, reported_user_counters, outbox_messages), a unique
index on (reporter, reported), and the initial migration applied at startup. PostgresReportService does it all in ONE transaction:
INSERT … ON CONFLICT DO NOTHING RETURNING → duplicate/rollback; counter upsert +1 RETURNING; a conditional
`block_requested_at IS NULL` update; an outbox row with the serialized UserBlockRequested. Add a summary endpoint. Delete the
in-memory store. Integration tests against real PostgreSQL (Testcontainers), plan tests 1–4, 7, 8. `blockRequested` is state."

## Output
| File | Responsibility |
|---|---|
| `Persistence/Entities.cs` | `Report`, `ReportedUserCounter`, `OutboxMessage` |
| `Persistence/ReportingDbContext.cs` | Snake-case model, **`ux_reports_reporter_reported` unique index**, check constraint `unique_report_count > 0`, `payload jsonb`, partial index `ix_outbox_messages_unpublished` (`WHERE published_at IS NULL`), design-time factory for `dotnet ef` |
| `Persistence/Migrations/*_InitialCreate` | Generated with the repo-pinned `dotnet-ef` 11.0.0 |
| `Reports/PostgresReportService.cs` | The 4-step transaction (see its doc comment). Explicit parameterised SQL (`FormattableString`, interpolated values become parameters). UUIDv7 IDs. Timestamps truncated to µs |
| `Reports/ReportSummaryEndpoint.cs` | `GET /api/reports/users/{userId}/summary` → `{userId, uniqueReportCount, blockThreshold, blockRequestedAt}`. A non-null `blockRequestedAt` means "BlockPending until Account processes it" |
| `Reports/ReportingOptions.cs` | `Reporting:BlockThreshold` (default 5, validated ≥ 1) |
| `Program.cs` | DbContext + Npgsql + snake case; `TimeProvider.System`; `MigrateAsync` at startup (prototype only); the in-memory store is **deleted** |
| `tests/…/ReportingApiFactory.cs` | xUnit v3 **assembly fixture**: one `postgres:17.12` Testcontainer and one host for all tests. Migrations run through the real startup path. Only Posts is faked. Tests isolate themselves by unique user IDs |

## Concurrency reasoning (READ COMMITTED)
- **Same pair, concurrently:** the second `INSERT … ON CONFLICT` blocks on the first one's uncommitted unique-index entry. After the first commits it sees the conflict → `DO NOTHING` → duplicate. If the first rolls back, the second inserts. Either way exactly one row exists.
- **Same target, different reporters:** the counter upsert takes a row lock held until commit. The next upsert waits and then increments the *latest committed* value (PostgreSQL re-evaluates `ON CONFLICT DO UPDATE` against the new row version), so no increment is lost.
- **Threshold:** while the counter row is locked, exactly one transaction sees `count >= 5 AND block_requested_at IS NULL` in the upsert's `RETURNING`, so exactly one outbox row is created. The guarantee is the **row lock + RETURNING**. The separate conditional `UPDATE … WHERE block_requested_at IS NULL` is a belt-and-braces safety net that makes the rule visible in SQL. (An earlier draft of this log credited the UPDATE; the reviewer corrected it.)
- `ix_reports_reported_user_id` exists because the unique index leads with `reporter_user_id` and can't serve per-target lookups.
- **Report and event are atomic:** a crash before commit leaves neither; after commit, both exist and publishing is the outbox worker's job (Iteration 4).

## Problems hit and how they were resolved
| Problem | Resolution |
|---|---|
| The `Reports` DbSet property shadowed the `Reporting.Api.Reports` namespace (CS1061) | `global::` qualified name |
| `SqlQuery(...).SingleAsync()` → EF tried to compose `LIMIT` over a non-composable `INSERT … RETURNING` | `ToListAsync()` + `.Single()` client-side |
| **Test 4 caught a real mismatch:** the event's `RequestedAt` (100 ns ticks) ≠ the stored `block_requested_at` (µs). The values differed by less than 1 µs | Truncate `now` to µs once per transaction. The event and the DB now hold the identical instant |

## Verification
- `dotnet build -warnaserror` → clean. `dotnet test --solution ForumReporting.slnx` → **81/81**, run **3 times in a row** with no flakes.
- New integration tests against real PostgreSQL:
  - **#1** first report stored, count 1;
  - **#2** same reporter + different post by the same author → Duplicate, count still 1;
  - the same reporter may report different users;
  - **#3** 4 reporters → 4, no outbox;
  - **#4** 7 reporters → counts 1..7, `blockRequested` F,F,F,F,T,T,T, exactly **1** outbox row whose payload deserializes with the strict v1 contract (`reportCount` 5, `eventId` = row id, `requestedAt` = `block_requested_at`);
  - **#7** 20 concurrent requests from the same reporter → 1 Created and 19 Duplicate, 1 row, count 1;
  - **#8** 10 concurrent distinct reporters → counts are exactly {1..10} (none lost or repeated), 1 outbox row;
  - mixed: 6 reporters × 4 concurrent requests → 6 reports, count 6, 1 outbox row;
  - the summary endpoint (0 before, 5 + `blockRequestedAt` after, 400 for a bad ID, 401 without identity).
  - The concurrency tests release all tasks through a gate, each with its own DI scope, DbContext and connection.
- Existing endpoint tests now run against PostgreSQL unchanged (they already used unique authors).
- Manual run against the Docker Postgres (:5433) with Posts.Api and Reporting.Api:
  - reporter-1..5 → 201;
  - reporter-1 on another post by target-1 → **409**;
  - reporter-6 → 201;
  - the summary shows `uniqueReportCount 6` and `blockRequestedAt` set;
  - psql shows 6 reports, the counter at 6, and 1 outbox row (`reportCount` 5, unpublished, 0 attempts).

## Review (Reviewer agent: general-purpose subagent; allowed temporary mutations, required to restore the tree)

**Prompt (summary):** "The most important review. Think like a database engineer. Verify every concurrency claim in this log
under READ COMMITTED: ON CONFLICT blocking, upsert re-evaluation, threshold once/never-twice, cancellation, deadlocks, count =
rows. Check SQL safety, mixing raw SQL with EF, schema, timestamps, the startup-migration lock claim, and whether the tests would
**fail** on a broken implementation. You may make temporary mutations but must restore the tree."

**Result:** 0 blockers, 3 should-fix, 7 nits. The design was confirmed correct.

The reviewer **proved** that the tests can fail. It temporarily replaced the atomic increment with app-side read-then-write, and tests #8 and "mixed" failed in 3 of 3 runs. It then restored the file (md5 verified). I confirmed afterwards that `git status` showed only my changes, no stash, and the increment intact.

| # | Finding | Verified by me? | Decision |
|---|---|---|---|
| S1 | The transaction relied on the server default isolation level. Under REPEATABLE READ or SERIALIZABLE, concurrent upserts would fail with 40001 and return 500 | Yes | **Fixed:** `BeginTransactionAsync(IsolationLevel.ReadCommitted)`, and the doc comment says the argument depends on it. A note covers a future `EnableRetryOnFailure` (it needs an execution strategy) |
| S2 | Commit and rollback used the request token. A disconnect during COMMIT could return 500 for a report that was actually committed, and a cancelled rollback turned a clean 409 into a 500 | Yes | **Fixed:** `CancellationToken.None` for Commit and Rollback. The token is still honoured on the data statements and lock waits |
| S3 | Missing tests: rollback when a later step fails; cancellation; lock behaviour rested only on timing | Yes | **Fixed with 3 deterministic tests.** (a) An interceptor fails the **outbox INSERT** (the last step) for the 5th report → report, +1 and `block_requested_at` all rolled back; the retry crosses the threshold and creates 1 event. (b) Hold the counter row `FOR UPDATE` → the report blocks (asserted not complete after 500 ms) → release → it increments the committed value (3). (c) Same lock, then cancel the waiting request → `OperationCanceledException`, nothing persisted |
| N1 | `SaveChangesAsync` inside a user transaction adds an auto-savepoint round trip | Yes | **Fixed:** the outbox insert is now raw parameterised SQL (`{payload}::jsonb`), the same style as the other steps |
| N2 | `CounterRow` maps only because snake_case also applies to ad-hoc types, which is fragile | Yes | **Fixed:** a doc comment states the dependency. Test #4 catches a break |
| N3 | The conditional UPDATE is redundant; the guarantee is the lock + RETURNING | Yes | **Kept** as a cheap safety net (it runs once per user); the **docs were corrected** to credit the right mechanism |
| N4 | `$` in .NET regex matches before a trailing `\n`, so `"post-1\n"` / `"reporter-1\n"` passed | Yes | **Fixed:** `\z` anchors, plus 2 tests |
| N5 | Outbox `created_at` comes from the app clock before the lock wait, so commit order ≠ `created_at` order | n/a | **Carried to Iteration 4:** the worker must not use `created_at` as a high-water mark. SKIP LOCKED polling of all unpublished rows is fine |
| N6 | Startup migration lock: EF 9+ `AcquireDatabaseLock` exists in Npgsql 11, but the exact lock mechanism is unverified | Partly | **Accepted:** the Program.cs comment is kept; the README will say migrations belong in a deployment step for production |
| N7 | The migration class name `_20261125102352_InitialCreate` | n/a | **Rejected:** generated and harmless |

**Lesson for the workflow:** asking the reviewer to *prove* that a test fails on a broken implementation (mutation testing)
gives much stronger evidence than "the tests pass". It also needs a guardrail: the reviewer must restore the tree, and I check that it did.

## Verification after fixes
- `dotnet build -warnaserror` → clean. `dotnet test --solution ForumReporting.slnx` → **86/86**, 3 consecutive runs.
