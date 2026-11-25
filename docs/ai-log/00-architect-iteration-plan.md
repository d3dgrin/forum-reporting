<!-- Output of the Architect step: Claude Code plan mode, approved by me before implementation. Later "Carried from ... review" bullets were added as reviews refined the plan. -->

# Forum Reporting Prototype — Iterative Implementation Plan

## Context

This is Part 2 of an interview task: a working .NET prototype of a forum reporting feature, built with AI agents. The three requirements:
1. One report per reporter→target pair, however many posts the target has. Duplicates don't count.
2. Each report names a post by the target and gives a reason.
3. Five distinct reporters → the target is blocked.

The source is `reporting-feature-prototype-plan.md`, with the design diagrams in `diagrams/` (`forum_app.*` shows the full microservice design; `reporting_service.*` shows Reporting → Posts → Outbox → Broker → Account). The repo holds only these files so far. There is no code and no git repo yet. The environment has .NET SDK 11.0.100 and Docker 27.4.

The work is split into **8 small iterations**. Each one ends with a build, its tests and a **detailed code review** before the next one starts. Agent prompts, outputs and review findings are logged as we go, so that `AI_WORKFLOW.md` can be written from real material.

## Key design decisions (made up front, so iterations don't revisit them)

| Topic | Decision |
|---|---|
| Target framework | `net11.0`. If Npgsql EF Core or Testcontainers packages are not ready for 11 yet, fall back to `net10.0` LTS (checked in iteration 0). |
| IDs | User IDs are opaque **strings** (they look like an IdP `sub` claim and read well in the demo: `target-1`, `reporter-1..6`). Post IDs are strings (`post-1..`). Report, Outbox and Event IDs are `Guid`. |
| Messaging | **Raw `RabbitMQ.Client` 7.x**, not MassTransit (MassTransit v9 is commercial, and raw code makes the outbox and idempotency logic visible for the interview). Durable topic exchange `forum.events`, routing key `user.block-requested`, durable queue `account.user-block-requested` with a DLX/DLQ. Messages are persistent with `MessageId = EventId`. Publisher confirms are on. |
| Persistence | EF Core + Npgsql with snake_case naming (`EFCore.NamingConventions`). Migrations are applied at startup (dev only). The critical report transaction uses **explicit SQL** through `Database.SqlQuery`/`ExecuteSql` inside an EF transaction, so the concurrency guarantees are clear. |
| Report transaction | (a) `INSERT INTO reports … ON CONFLICT (reporter_user_id, reported_user_id) DO NOTHING RETURNING id`. No row → rollback, 409. (b) Counter upsert `… ON CONFLICT DO UPDATE SET unique_report_count = unique_report_count + 1 RETURNING unique_report_count`. The row lock serialises concurrent reports against the same target. (c) If count ≥ 5: `UPDATE reported_user_counters SET block_requested_at = now() WHERE reported_user_id=@u AND block_requested_at IS NULL RETURNING …`. One affected row → insert the `UserBlockRequested` outbox row. (d) Commit. |
| Outbox worker | `BackgroundService` that polls about every 2 s. `SELECT … WHERE published_at IS NULL ORDER BY created_at LIMIT 20 FOR UPDATE SKIP LOCKED`, publish, await the confirm, set `published_at`. On failure: `attempts++`, `last_error`. |
| Account consumer | Manual ack, prefetch 10. One transaction: `INSERT INTO processed_messages … ON CONFLICT DO NOTHING`. 0 rows → duplicate, ack. Otherwise set the account to `Blocked` (upsert if the account is missing, documented as an assumption). Commit, then ack. Exception → nack without requeue → DLQ. |
| Auth | `DemoHeaderAuthenticationHandler` reads `X-Demo-UserId`. It is named and commented as **not production auth**. Reporter ID comes only from `ClaimsPrincipal`. |
| Reason | Required, trimmed, 3–500 chars. |
| "BlockPending" | Account.Api only knows `Active`/`Blocked`. The pending state is shown by Reporting.Api: `GET /api/reports/users/{userId}/summary` returns `{ uniqueReportCount, blockRequestedAt }`. This keeps service ownership clean. |
| Ports | Posts 5101, Account 5102, Reporting 5103. Postgres **5433** on host (a local Windows PostgreSQL owns 5432), pgAdmin 5050, RabbitMQ 5672 / UI 15672. |

## Target layout

```
src/BuildingBlocks/Contracts/            UserBlockRequested record, MessagingTopology constants
src/Services/Posts.Api/                  in-memory seeded stub
src/Services/Reporting.Api/              Auth/, Posts/ (client), Reports/ (endpoint, service), Persistence/, Outbox/
src/Services/Account.Api/                Persistence/, Messaging/ (consumer), Accounts/ (endpoint)
tests/Reporting.UnitTests/               validation, auth handler, result mapping
tests/Reporting.IntegrationTests/        Testcontainers Postgres (+RabbitMQ for outbox)
tests/Account.IntegrationTests/          Testcontainers Postgres + RabbitMQ
deploy/docker-compose.yml, deploy/postgres/init.sql
scripts/demo.ps1, demo.http
docs/architecture.mmd (+ copies of diagrams), README.md, AI_WORKFLOW.md
Directory.Build.props, Directory.Packages.props, ForumReporting.sln, .gitignore
```

## Per-iteration workflow (repeat every time)

1. **Implement** the iteration scope (developer-agent role). Record the prompt and a summary in `docs/ai-log/iteration-N.md`.
2. **Verify**: `dotnet build -warnaserror` and `dotnet test` (plus manual curl/compose checks where noted).
3. **Code review**: you run `/code-review high` on the diff, or a reviewer subagent runs with the iteration's checklist below. Findings are triaged as fix / reject (with a reason) and go into the log.
4. Fix the accepted findings and re-run the tests.
5. With your OK, make **a git commit for each iteration**. This gives the review a clean diff and the interview a readable history.

---

## Iteration 0 — Repo scaffold and infrastructure
- `git init`, `.gitignore` (dotnet template), solution, empty projects (web APIs and xUnit test projects), and project references.
- `Directory.Build.props`: TargetFramework, Nullable, ImplicitUsings, `TreatWarningsAsErrors`. `Directory.Packages.props` for central package versions (Npgsql EF, NamingConventions, RabbitMQ.Client, Testcontainers.PostgreSql/RabbitMq, Mvc.Testing, xUnit, FluentAssertions-free assertions or Shouldly).
- `deploy/docker-compose.yml`: `postgres:17` with a healthcheck and `init.sql` that creates `reporting_db` and `account_db`; `dpage/pgadmin4` with a preconfigured server; `rabbitmq:4-management`.
- Each API has `/health`.
- **Done when:** `docker compose up -d` is healthy, the pgAdmin and RabbitMQ UIs open, `dotnet build` passes, and package compatibility with net11 is confirmed (or we fall back to net10).
- **Review focus:** package versions and licences, compose healthchecks and volumes, no secrets beyond dev defaults, project reference direction (services reference only Contracts).

## Iteration 1 — Contracts and Posts.Api stub
- `Contracts`: `UserBlockRequested(Guid EventId, string UserId, int ReportCount, string Reason, DateTimeOffset RequestedAt)`, plus `MessagingTopology` constants (exchange, routing key, queue names).
- Posts.Api: `GET /api/posts/{postId}` → `{ postId, authorId }` or 404. Seed data: `post-1..3` by `target-1`, `post-10..` by reporters, and a second target for the negative demo.
- **Done when:** curl returns the author, and an unknown post returns 404.
- **Review focus:** the contract is minimal and versionable, there is no shared domain logic in Contracts, and the stub stays a thin stub.

## Iteration 2 — Reporting.Api surface: auth, validation, post lookup
- `DemoHeaderAuthenticationHandler` with an authorization policy that requires an authenticated user. No header → 401.
- `POST /api/reports` (Minimal API) with `{ postId, reason }` and validation (400 ProblemDetails).
- Typed `IPostsClient` (HttpClient, base URL from config, short timeout). Not found → 404. Posts unavailable → 503.
- Self-report → 422. Calls `IReportService.SubmitAsync(reporterId, reportedUserId, postId, reason)` and returns a discriminated result (`Created | Duplicate`), mapped to 201/409. The persistence implementation comes in iteration 3; a temporary in-memory one is allowed only to run the API.
- **Unit tests:** auth handler (missing, blank, valid header), request validation, result→HTTP mapping, self-report rejection (fake posts client).
- **Carried from the Iteration 1 review:**
  - validate the postId format (`^[A-Za-z0-9-]{1,64}$`) and `Uri.EscapeDataString` it;
  - treat any 404 from Posts as "post not found";
  - treat a 200 with an empty `authorId` as an upstream error;
  - user IDs are opaque and case-sensitive (documented, not normalised);
  - use `TimeProvider` for timestamps.
- **Review focus:** the reporter/target is never taken from the body, error mapping is correct, the HttpClient lifetime and timeout are sound, and the demo auth cannot be mistaken for real auth.

## Iteration 3 — Reporting persistence and atomic transaction (core)
- `ReportingDbContext`: `Report`, `ReportedUserCounter`, `OutboxMessage`. Unique index `ux_reports_reporter_reported`, a partial index on `outbox_messages(published_at IS NULL)`, and the initial migration.
- `ReportService` implements the SQL transaction from the decisions table. The JSON outbox payload is the serialized `UserBlockRequested`. The reason is something like `"Reported by 5 distinct users"`.
- `GET /api/reports/users/{userId}/summary`.
- **Carried from the Iteration 2 review:**
  - DELETE `InMemoryReportService` and its `TODO(iteration-3)` registration;
  - `blockRequested` in the response is STATE (`block_requested_at IS NOT NULL` after the transaction), while the outbox event is created once;
  - the existing `ReportsEndpointTests` must keep passing against Postgres; they already use a unique author per test.
- **Integration tests** (Testcontainers Postgres + `WebApplicationFactory`, fake posts client) — plan tests 1–4, 7, 8:
  1. First report stored, count = 1.
  2. Same reporter, different post by the same author → 409, count unchanged, 1 row.
  3. Four distinct reporters → count 4, no outbox row.
  4. Fifth → count 5, exactly one outbox row. A sixth reporter → count 6, still one outbox row.
  7. 20 parallel identical reports from one reporter → 1 report, count 1.
  8. N parallel reports from distinct reporters → count N. With N = 10, exactly one outbox row.
- **Review focus (most important review):** isolation level and lock behaviour, rollback on duplicate, that the count can't drift from the report rows, the threshold firing exactly once, SQL injection safety (parameterised `FormattableString`), and that tests really run concurrently (`Task.WhenAll` with separate scopes/connections).

## Iteration 4 — Outbox publisher
- **Carried from the Iteration 1 review:** the publisher declares the FULL topology (including Account's queue, with `MessagingTopology.QueueArguments`) and publishes with `mandatory: true`. A basic.return counts as a failure: the row is not marked published. Without this, confirmed-but-unroutable messages are silently lost.
- **Carried from the Iteration 3 review:** outbox `created_at` is NOT commit order, so never use it as a high-water mark. Poll all unpublished rows with `FOR UPDATE SKIP LOCKED`; ordering by `created_at` is fine as a best-effort hint.
- `RabbitMqConnectionProvider` (one shared connection), topology declaration (exchange, queue, DLX/DLQ binding) at startup in both services, so start order doesn't matter.
- `OutboxPublisherWorker` as described above, with configurable interval and batch size, and logging.
- **Integration tests** (Postgres + RabbitMQ containers): (a) an unpublished row is published, a test consumer receives it with the correct `MessageId`/payload, and `published_at` is set; (b) broker unreachable → the row stays unpublished, `attempts` increases, no exception escapes the worker.
- **Review focus:** what happens on a crash between the confirm and the `published_at` update (a duplicate publish is accepted because the consumer is idempotent; document this), `SKIP LOCKED` correctness with several instances, channel thread-safety, and reconnect behaviour.

## Iteration 5 — Account.Api and idempotent consumer
- `AccountDbContext`: `Account(UserId, Status, BlockedAt, BlockReason)`, `ProcessedMessage(EventId PK, ProcessedAt)`, a migration, and seed accounts for every demo user (`Active`).
- `GET /api/accounts/{userId}/status` → `{ userId, status, blockedAt, blockReason }`, or 404.
- `UserBlockRequestedConsumer` (`BackgroundService`) and `BlockUserHandler` (the transactional logic, kept separate so it can be tested without the broker).
- **Integration tests** — plan tests 5 and 6: the handler blocks the account; the same EventId twice → processed once and `BlockedAt` unchanged; a message published to the real RabbitMQ container → the account becomes Blocked; a malformed message → DLQ.
- **Review focus:** ack only after commit, poison-message handling, the race where two consumers get the same redelivered event (the PK on `processed_messages` makes it safe), and Account never trusting Reporting's DB.

## Iteration 6 — End-to-end demo
- `demo.http` (VS/Rider/VS Code REST client) and `scripts/demo.ps1`: reset/seed, five reporters report posts by `target-1`, reporter-1 reports another of target-1's posts (409), self-report (422), unknown post (404), poll the summary and then the account status until `Blocked`.
- Optional E2E smoke test that runs the three hosts in-process against containers (only if time permits).
- **Done when:** the full stack runs from the README commands and the script shows `Blocked`. Also do a manual broker-outage demo: stop RabbitMQ, submit the 5th report, start RabbitMQ again → the account gets blocked.
- **Review focus:** the demo is repeatable (it resets state), there are no hard-coded ports outside config, and logs are readable for the live demo.

## Iteration 7 — Documentation and interview material
- `README.md`: architecture summary, assumptions (from plan §7, plus missing account → upsert, string IDs, and BlockPending shown via Reporting), run/test instructions, trade-offs (unique constraint vs app check, atomic counter vs `COUNT(*)`, outbox vs direct publish, async vs sync blocking), and what remains / production gaps.
- `docs/architecture.mmd` for the implemented solution (from plan §3), with links to `diagrams/` for the full Part 1 design.
- `AI_WORKFLOW.md`, compiled from `docs/ai-log/*`: Architect / Developer / Test / Reviewer agent examples, each with prompt, output, how it was verified, and what was changed or rejected.
- **Review focus:** the docs match the code (endpoints, ports, commands), the assumptions are complete, and there are no claims that the tests don't back up.

---

## Verification (final)
1. `docker compose -f deploy/docker-compose.yml up -d` → all containers healthy.
2. `dotnet test` → all unit and integration tests green (Docker must be running for Testcontainers).
3. Run Posts, Account and Reporting with `dotnet run`, then run `scripts/demo.ps1`. The expected output: counts 1→5, the duplicate gets 409, and `target-1` ends up with status `Blocked`. The RabbitMQ UI shows the message flow, and pgAdmin shows `reports`, `reported_user_counters`, `outbox_messages` (published) and `processed_messages`.

## Timebox guidance (~4 h total)
Iterations 0–1: 30 min · 2: 30 min · 3: 60 min · 4: 30 min · 5: 40 min · 6: 20 min · 7: 30 min. If time runs short, iterations 3 and 5 plus their tests are the minimum. Iteration 6's E2E test and the DLQ test are optional.
