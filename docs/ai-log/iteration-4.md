# Iteration 4 — Outbox publisher

**Agent role:** Developer (Claude Code, Opus 5.5).

**Scope note from the user (after Iteration 3):** "Don't focus on production at all, it's only a demo project. Don't create a lot of
tests, cover only main logic." From this iteration on, tests and reviews are deliberately lean.

**Prompt (summary):** "Implement the outbox publisher: a BackgroundService that polls unpublished rows with FOR UPDATE SKIP LOCKED,
publishes to RabbitMQ with publisher confirms and `mandatory: true` (an unroutable message is a failure), and marks rows published.
The publisher declares the full topology, including Account's queue. Never use `created_at` as a high-water mark. One end-to-end test."

## Output
- `Outbox/RabbitMq.cs`:
  - `RabbitMqOptions` (connection factory with automatic recovery);
  - `RabbitMqTopology.DeclareAsync` declares the topic exchange, the direct DLX, Account's quorum queue with DLX arguments and its binding, and the DLQ with its binding.
- `Outbox/OutboxPublisherWorker.cs`:
  - Polls every `Outbox:PollIntervalMilliseconds` (default 2 s) and claims a batch with `FOR UPDATE SKIP LOCKED`.
  - Publishes persistent messages with `MessageId = EventId`, `Type = EventType`, `mandatory: true`.
  - The `await` returns only after the broker confirm. A nack or basic.return throws `PublishException`, which increments `attempts`, stores `last_error`, and leaves the row for the next cycle.
  - If the broker or DB is down, it logs a warning, resets the connection and retries on the next poll.
- `Program.cs` registers the options and the hosted service. `appsettings.Development.json` has the RabbitMQ settings.

## Verification
- `dotnet build -warnaserror` → clean. `dotnet test` → **87/87**.
- New test (a real RabbitMQ Testcontainer in the assembly fixture):
  - 5 distinct reports lead to the outbox row being marked published;
  - the message is on `account.user-block-requested` with `MessageId = EventId`, `Type = …v1`, persistent delivery and a strict-contract payload (`reportCount` 5).
- Manual run against the Docker stack: the leftover unpublished `target-1` row from Iteration 3 was published within seconds of startup.
  - `rabbitmqctl list_queues` shows `account.user-block-requested` as **quorum** with 1 message and the DLX arguments, and the DLQ is empty.
  - The event waits there for Account.Api (Iteration 5), which shows that start order doesn't matter.

## Review (Reviewer agent, narrow scope: event loss, stuck worker, client API usage, test flakiness)

**Result:** no blocker. There is no path that marks a row published without a broker confirm and routing.

| # | Finding | Decision |
|---|---|---|
| S1 | An existing Iteration 3 test asserted `PublishedAt == null` / `Attempts == 0`, but the worker now runs in the test host every 200 ms, so the test is **flaky on slow machines** | **Fixed:** removed the two asserts. Publishing is covered by `OutboxPublishingTests` |
| S2 | After a basic.return (e.g. the queue was deleted in the UI) the channel stays open, so the topology is never redeclared and the outbox is stuck until restart | **Fixed (cheap):** a publish failure sets `_resetChannel`, and the next cycle reconnects and redeclares |
| S3 | Head-of-line blocking if one row always fails | **Accepted:** there is a single event type in the demo. Documented with a code comment |
| N1 | Account must declare an identical topology or get 406 | **Iteration 5:** Account copies `RabbitMqTopology` verbatim. Contracts stays free of a RabbitMQ.Client dependency |
| N2 | `AutomaticRecoveryEnabled` overlaps the manual reconnect | **Fixed:** removed; the manual reconnect is enough |
| N3 | `ContinueWith` to swallow cancellation is hard to read | **Fixed:** `ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing)` |
| N4 | The unroutable path is untested | **Accepted** (lean test scope) |

**Verification after fixes:** build clean, **87/87**.
