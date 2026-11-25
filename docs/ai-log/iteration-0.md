# Iteration 0 — Repo scaffold and infrastructure

**Agent role:** Developer (Claude Code, Opus 5.5), working from the approved iterative plan.

**Prompt (summary):** "Implement Iteration 0: git repo, solution, empty service and test projects,
central package management, docker-compose with PostgreSQL (+pgAdmin) and RabbitMQ (management UI),
/health endpoints. Verify net11 package compatibility."

## Output
- `ForumReporting.slnx` (the .NET 11 SDK's default XML solution format) with Contracts, Posts.Api, Account.Api, Reporting.Api and 3 test projects.
- `Directory.Build.props` (net11.0, nullable, warnings as errors except NuGet advisories), `Directory.Packages.props` (central versions).
- `nuget.config` (nuget.org only), `global.json` (SDK pin + Microsoft.Testing.Platform runner), `dotnet-tools.json` (dotnet-ef 11.0.0).
- `deploy/docker-compose.yml`, all ports bound to 127.0.0.1:
  - postgres:17.12 on host port **5433**, plus `init.sql` that creates `reporting_db` and `account_db`.
  - pgAdmin 4 on :5050 (dev login `admin@example.com` / `admin`).
  - rabbitmq:4.4-management on :5672, UI on :15672 (`forum` / `forum`).
- Each API: `GET /health`. Ports: Posts 5101, Account 5102, Reporting 5103.

## Problems hit and how they were resolved
| Problem | Resolution |
|---|---|
| Verified that net11 packages exist (Npgsql EF 11.0.0, NamingConventions 11.0.0, RabbitMQ.Client 7.3.0, Testcontainers 4.17.0, xunit.v3 4.0.3) | Stayed on net11.0, no fallback needed |
| Shell heredoc escaping broke the `ProjectReference` paths (`$3` / backslashes) | Rewrote them with forward slashes (MSBuild accepts both) |
| `dotnet sln add` refused two projects (same-name conflict in auto-created folder) | Wrote `.slnx` by hand |
| NU1507 under CPM: machine-level Telerik feed + nuget.org | Repo `nuget.config` with `<clear/>`; this also makes builds reproducible on other machines |
| `dotnet test` on .NET 10+ SDK refuses VSTest mode with xunit.v3 v4 (MTP) | `global.json` → `"test": { "runner": "Microsoft.Testing.Platform" }` |

## Verification
- `dotnet build ForumReporting.slnx -warnaserror` → 0 warnings, 0 errors.
- `dotnet test --solution ForumReporting.slnx` → 3/3 smoke tests pass.
- `docker compose -f deploy/docker-compose.yml up -d --wait` on fresh volumes:
  - postgres and rabbitmq are healthy, and both databases exist.
  - Host port 5433 is reachable.
  - The RabbitMQ node is `rabbit@rabbitmq`.
  - The RabbitMQ UI and pgAdmin return HTTP 200.

## Review (Reviewer agent: general-purpose subagent, read-only)

**Prompt (summary):** "Act as a Reviewer agent. Do a detailed read-only review of the Iteration 0 scaffold: package versions and licences,
reference direction, build settings, compose healthchecks, volumes, tags, secrets, ports, friction for later iterations,
and the accuracy of this log. Rank findings blocker / should-fix / nit with file:line, failure scenario and fix."

**Result:** 0 blockers, 4 should-fix, 5 nits. Licences are clean (MassTransit v9 and FluentAssertions 8 avoided), there are no vulnerable packages, and reference direction is correct.

| # | Finding | Verified by me? | Decision |
|---|---|---|---|
| S1 | The Postgres healthcheck `pg_isready -d …` ignores `-d` and can pass against the temporary socket-only init server before `init.sql` runs | Yes: `pg_isready -d nonexistent_db` → exit 0, while `psql -h 127.0.0.1 -d nonexistent_db` → exit 2 | **Fixed:** the healthcheck now runs `psql` over TCP against both DBs |
| S2 | No `hostname:` on RabbitMQ → node name follows the container ID → durable data lost on recreate | Yes: node was `rabbit@<container-id>` | **Fixed:** `hostname: rabbitmq` (node is now `rabbit@rabbitmq`) |
| S3 | `Microsoft.NET.Test.Sdk` / `xunit.runner.visualstudio` are VSTest-only and dead under MTP | Yes | **Fixed:** removed from CPM and the test csprojs; tests still pass |
| S4 | `dotnet-ef` not pinned | Yes | **Fixed:** tool manifest with dotnet-ef 11.0.0 |
| N1 | Ports exposed on all interfaces; 5432 may clash with a local Postgres | Yes, and it **was a real clash**: a local Windows PostgreSQL service listens on 5432. Earlier verification used `docker exec`, so it missed that apps would have connected to the wrong server | **Fixed:** all ports bound to 127.0.0.1; Postgres mapped to host **5433** |
| N2 | `init.sql` only runs on an empty volume | n/a | **Accepted:** comment in compose; will go in README |
| N3 | Floating image tags | n/a | **Partly accepted:** `postgres:17.12`, `rabbitmq:4.4-management`. pgAdmin stays on `:9` because it is a dev UI only and reproducibility doesn't matter there |
| N4 | `TreatWarningsAsErrors` makes NuGetAudit advisories fatal | n/a | **Fixed:** `WarningsNotAsErrors` NU1901-NU1904 |
| N5 | Stale AI log | Yes | **Fixed:** this file |

**Rejected / deferred:** none rejected. The reviewer's note about `public partial class Program` clashing if one test project references two services is deferred, because no test project does.

**Lesson for the workflow:** the reviewer's "nit" about port binding turned out to be the most important finding. A reviewer agent is worth running even on scaffolding, but I verify each claim before acting on it.
