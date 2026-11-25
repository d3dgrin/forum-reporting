# Iteration 5 — Account.Api and idempotent consumer

**Agent role:** Developer (Claude Code, Opus 5.5). Lean scope (demo; main-logic tests only).

**Prompt (summary):** "Implement Account.Api:
- account_db with `accounts` (seeded demo users, Active) and `processed_messages` (PK EventId);
- `GET /api/accounts/{userId}/status`;
- a RabbitMQ consumer that applies `UserBlockRequested` once per EventId in one local transaction and acks after commit;
- malformed messages go to the DLQ.
Copy the topology declaration verbatim from Reporting. Two tests: idempotent handler, and end-to-end via the broker."

## Output
- `Persistence/AccountDbContext.cs`: `accounts` (status stored as a string, `HasData` seed of target-1, target-2 and reporter-1..6), `processed_messages`, plus the migration.
- `Accounts/BlockUserHandler.cs`: one READ COMMITTED transaction.
  - `INSERT processed_messages … ON CONFLICT DO NOTHING RETURNING`: no row means a duplicate, so it rolls back and returns `AlreadyProcessed`.
  - Otherwise it upserts the account to `Blocked`. An unknown user is created as Blocked; an already-blocked account keeps its first `blocked_at` and reason.
- `Messaging/UserBlockRequestedConsumer.cs`:
  - manual ack with prefetch 10;
  - strict-contract JSON failure → nack without requeue → DLQ;
  - processing failure → short pause, then nack with requeue;
  - the broker connection is retried at startup.
- `Messaging/RabbitMq.cs`: a verbatim copy of Reporting's topology declaration (it must be identical, or RabbitMQ returns 406).
- `Program.cs`: DbContext, startup migration, consumer and the status endpoint.

## Verification
- Build clean. `dotnet test` → **88/88**.
- New tests (Postgres + RabbitMQ Testcontainers):
  - **plan tests 5 + 6:** an event blocks the account; the same EventId again → `AlreadyProcessed`, `BlockedAt` unchanged;
  - an event published to the real exchange is consumed, and the status endpoint goes from `Active` to `Blocked`.
- **Full chain, manual run against the Docker stack:** the `target-1` event published in Iteration 4 had been waiting in the quorum queue. Starting Account.Api consumed it:
  - `GET /api/accounts/target-1/status` → `Blocked`, "Reported by 5 distinct users";
  - target-2 stays `Active`;
  - the queue holds 0 messages with 1 consumer, and the DLQ is empty.

## Review (Reviewer agent, narrow scope: idempotency, ack order, consumer API, topology parity, tests)

**Result:** no blocker. A message is never acked before its change is committed, and an EventId is never applied twice. The topology copy is byte-identical to Reporting's.

| # | Finding | Decision |
|---|---|---|
| S1 | The end-to-end test could race the consumer's background startup: it publishes before the topology exists, and an unroutable `mandatory` publish with no return handler is dropped silently, so the test is flaky | **Fixed:** the test declares the topology itself (idempotent) before publishing |
| S2 | Docs implied there was no automatic recovery, but RabbitMQ.Client 7.3 enables connection + topology recovery **by default** | **Fixed:** the comment was corrected. This also corrects the Iteration 4 log: removing `AutomaticRecoveryEnabled = true` there did not turn recovery off. The publisher's manual reconnect simply sits on top of it, which is harmless |
| N1 | A misleading "not reachable" log, and a connection leaked per startup retry | **Fixed:** the exception is logged, and the connection is disposed on retry |
| N2 | A non-transient DB error (column length) is requeued forever | **Accepted:** it can't happen with Reporting's fixed reason text |
| N3 | The redelivery assertion never reaches the COALESCE path | **Accepted** (lean tests); dedup is what matters for the requirement |
| N4 | `target-2` is shared in the test DB | **Fixed:** comment added |

**Verification after fixes:** build clean, all tests pass.
