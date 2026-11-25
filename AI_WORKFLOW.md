# AI-assisted workflow

The prototype was built with **Claude Code (Claude Opus 5.5)** in small iterations. Each iteration followed the same loop:

```
plan (Architect) → implement (Developer) → tests (Test) → build + run tests + run the services → review (Reviewer subagent)
→ I verify each finding → fix / accept / reject → log → commit after my approval
```

The full per-iteration logs, with the prompts, outputs, problems, review tables and verification, are in [`docs/ai-log/`](docs/ai-log/).
This file summarises one real example per agent role. Every iteration was built with `-warnaserror` and tested
(`dotnet test`) before its review. Most iterations were also run against the Docker stack. The exceptions:
- Iteration 1 ran only Posts.Api.
- Iteration 2 ran without a database.
- In Iteration 6, `start-services.ps1` was not executed, and the PowerShell 7 fix is untested (PS7 isn't installed).

Each review finding was checked by hand, and several were first reproduced with a failing test.

## 1. Architect agent: from requirements to an iteration plan

- **Tool:** Claude Code in *plan mode* (read-only).
- **Input:** the task description, my prototype plan (`reporting-feature-prototype-plan.md`) and my Part 1 diagrams (`diagrams/`).
- **Prompt (summary):** "Analyze the prototype plan and the design diagrams. Prepare an implementation plan broken into small
  iterations, with a detailed code review after each one."
- **Output:** [`docs/ai-log/00-architect-iteration-plan.md`](docs/ai-log/00-architect-iteration-plan.md), with 8 iterations (scaffold → contracts/stub → API surface → persistence → outbox → Account → demo → docs). The decisions
  were fixed up front so later iterations wouldn't revisit them:
  - raw `RabbitMQ.Client` instead of MassTransit (licence, and the outbox mechanics stay visible);
  - string user IDs;
  - an explicit-SQL transaction;
  - "BlockPending" shown by Reporting, not by Account, so service ownership stays clean.
- **How it was checked:** I approved it in plan mode. Later reviews refined it, and I recorded those changes in the plan as
  "carried from review" items: Postgres moved to host port 5433, the publisher declares Account's queue, and `created_at` is
  not a high-water mark.
- **Changed by me:** after Iteration 3, I cut the scope to a **demo** with **main-logic tests only**. Iterations 4–7 have
  deliberately fewer tests and narrower reviews.

## 2. Developer agent: the report transaction (Iteration 3)

- **Prompt (summary):** "PostgresReportService does everything in ONE transaction: `INSERT … ON CONFLICT DO NOTHING RETURNING` → no row means
  duplicate and rollback; counter upsert +1 `RETURNING`; a conditional `block_requested_at IS NULL` update; an outbox row with the
  serialized `UserBlockRequested`."
- **Output:** [`PostgresReportService.cs`](src/Services/Reporting.Api/Reports/PostgresReportService.cs), the EF model with the unique
  index, the migration, and the summary endpoint.
- **How it was tested:** integration tests against real PostgreSQL (Testcontainers), then a manual run with psql showing 6 reports,
  a counter of 6 and **one** outbox row.
- **Problems the agent hit and fixed:**
  - EF refused to compose `SingleAsync` over `INSERT … RETURNING`;
  - the `Reports` DbSet property shadowed the `Reports` namespace;
  - a **real bug found by a test**: the event's `RequestedAt` (100 ns ticks) did not equal the stored `block_requested_at` (µs). The fix is to truncate the time once per transaction.

## 3. Test agent: duplicate, threshold and concurrency tests

- **Prompt (summary):** "Tests for the plan's essentials against real PostgreSQL: first report; duplicate on a different post; 4
  reporters; 5th → exactly one outbox row; 20 parallel reports from the same reporter (different posts); 10 parallel distinct reporters."
- **Output:** [`ReportPersistenceTests.cs`](tests/Reporting.IntegrationTests/ReportPersistenceTests.cs). All tasks are released
  through a shared gate, each with its own DI scope, DbContext and connection, so the requests really overlap.
- **How it was checked — proving the tests can fail:**
  - The reviewer agent temporarily replaced the atomic increment with app-side read-then-write. Test #8 (10 parallel distinct reporters) and the "mixed" concurrency test **failed in 3 of 3 runs**. It then restored the file; I checked `git status`.
  - The review also asked for deterministic tests. I added:
    - holding the counter row `FOR UPDATE` → the report blocks, then increments the committed value;
    - a cancelled waiting request → nothing persisted;
    - an injected failure on the outbox INSERT → report, +1 and block request are all rolled back.

## 4. Reviewer agent: a fresh subagent per iteration

- **Prompt pattern:** the context and requirements, decisions not to re-litigate, a focused checklist, permission to run the build
  and tests (and in later iterations to *run and probe the services*), and the output as "blocker / should-fix / nit with
  file:line, failure scenario, fix".
- **What I did with the output:** I verified each finding first, and reproduced several with failing tests. Then I fixed, accepted or
  rejected it, with a reason, in the iteration log.

| Iteration | Finding (reviewer) | How I verified | Outcome |
|---|---|---|---|
| 0 | "Bind ports to localhost; 5432 may clash" (a nit) | `Get-NetTCPConnection`: a **local Windows PostgreSQL already owned 5432**. My earlier checks used `docker exec`, so the apps would have silently connected to the wrong server | Fixed: host port 5433 |
| 0 | `pg_isready -d` ignores `-d` and reports healthy before `init.sql` has run | `pg_isready -d nonexistent_db` → exit 0 | Fixed: psql healthcheck over TCP |
| 1 | Publisher confirms + an exchange with no bound queue = message confirmed then **dropped**, so the outbox would mark a lost event as published | RabbitMQ semantics | Designed in: the publisher declares Account's queue and uses `mandatory: true` |
| 1 | A malformed event (missing `userId`) deserialized with defaults | **Failing test first** | Fixed: strict JSON options |
| 2 | Demo auth guard was a deny-list: **impersonation worked in Staging** | Reviewer ran it with `ASPNETCORE_ENVIRONMENT=Staging`; I checked the logic, and after the fix re-verified that Staging refuses to start | Fixed: allow-list of Development and Testing |
| 2 | A reason of `\u0000` or zero-width spaces passed validation (NUL → 500 in PostgreSQL) | **Failing tests first** | Fixed |
| 3 | The transaction relied on the server default isolation level; Commit was cancellable by a client disconnect | Code reading | Fixed: explicit READ COMMITTED, Commit/Rollback not cancellable |
| 4 | An old test asserting "unpublished" became flaky once the worker ran in the test host | Reasoning about the 200 ms poll | Fixed |
| 5 | The end-to-end test could publish before the consumer declared the topology (silently unroutable) | Reasoning about .NET background startup | Fixed |
| 6 | The demo script printed empty error bodies on PowerShell 7 (wrong `$_` in a nested catch) | Code reading; PS7 not installed, so not executed | Fixed |

**Rejected or accepted findings (examples):**
- tests for the Posts stub (it is a stub, and it was verified with curl);
- a retry/resilience handler for the Posts call (fail fast with 503 + `Retry-After` is enough for a demo);
- 502 vs 503 for Posts auth errors;
- renaming a generated migration class;
- production concerns after the scope change (they are listed in the README as next steps).

## What I learned about working with the agents

- **Verify, don't trust.** Severity labels can be wrong: a "nit" (the port binding) turned out to be the most important finding of Iteration 0. Several
  "facts" were wrong when I checked them: the reviewer itself pointed out that *removing* `AutomaticRecoveryEnabled = true` does
  not disable recovery, because it is on by default.
- **Run the code during review.** The best findings came from reviewers that ran and probed the services, not from reading diffs.
- **Ask for proof that tests fail.** A temporary mutation that turns the tests red is much stronger evidence than "all green".
- **Small iterations plus a commit per iteration** kept each review diff small and gave the reviewer a clean `git diff HEAD`.
- **Tooling friction was real but cheap to fix:**
  - heredoc escaping in a shell broke csproj paths;
  - a machine-level NuGet feed broke central package management;
  - xunit.v3 needed the MTP runner opt-in on the .NET 10+ SDK.
