# Iteration 6 — End-to-end demo

**Agent role:** Developer (Claude Code, Opus 5.5). Lean scope.

**Prompt (summary):** "Create a repeatable demo: a script that resets the demo data, shows the rejected cases (401/422/404/400),
4 distinct reporters, a duplicate on a different post by the same author (409, not counted), the 5th reporter, then polls the
Account status until Blocked. Add a .http file and a script to start the three services. Verify the broker-outage scenario manually."

## Output
- `scripts/start-services.ps1`: builds, starts Posts/Account/Reporting each in its own PowerShell window, and waits for `/health`.
- `scripts/demo.ps1` (PowerShell 5.1 and 7):
  - resets `reporting_db`, `account_db` and the queue (`-SkipReset` keeps the data);
  - prints each request with its status and body;
  - measures how long the asynchronous block takes.
- `demo.http`: the same requests for an IDE REST client.

## Verification
- `demo.ps1` was run **twice in a row** with identical results:

  | Step | Result |
  |---|---|
  | Initial state | Active, count 0 |
  | Not logged in | 401 |
  | Self-report | 422 |
  | Unknown post | 404 |
  | Empty reason | 400 |
  | reporter-1..4 | 201, counts 1–4 |
  | **reporter-1 on post-3 (a different post by target-1)** | **409**, count stays 4 |
  | reporter-5 | 201, count 5, `blockRequested: true` |
  | Account status | **Blocked after ~1.1 s** |

- **Broker-outage scenario** (manual):
  - `docker stop` RabbitMQ, then 5 reports → all 201 (Reporting does not depend on the broker);
  - the outbox holds 1 unpublished event, and the account is still Active;
  - the publisher logged "None of the specified endpoints were reachable" and kept retrying;
  - after `docker start` RabbitMQ, the publisher reconnected and published, and Account's consumer recovered automatically (client auto-recovery) → **Blocked**.
  - This shows the transactional outbox at work: the block request survives a broker outage.
- `start-services.ps1` was **not executed** in this session, because it opens three interactive windows. The services were started the same way (`dotnet run --no-build --project …`) in the background instead.

## Review (Reviewer agent, read-only, scripts not executed)

| # | Finding | Decision |
|---|---|---|
| S1 | **PS7 bug:** in `Invoke-Api` the inner `catch`'s `$_` is the *inner* error, so the `ErrorDetails` fallback never worked. On PS7, `Response` is an `HttpResponseMessage` (it has no `GetResponseStream`), so every 4xx would print an empty body | **Fixed:** capture `$err` first and prefer `ErrorDetails.Message`. Read the stream only for a 5.1 `WebResponse`. Re-run on 5.1 (bodies still printed). PS7 is not installed on this machine, so it is untested there |
| S2 | A failed `docker exec` reset is silent (native exit codes don't throw), so the demo would run on stale data | **Fixed:** check `$LASTEXITCODE` after each reset command, with a helpful message |
| N1 | The DLQ is not purged | **Fixed** |
| N2 | If Account.Api is down, the polling loop throws instead of printing the helpful message | **Fixed:** try/catch inside the loop |
| N3 | A `'` in the repo path would break `start-services.ps1` | **Accepted** (demo) |
| N4 | Non-ASCII characters in this Markdown log | **Accepted:** the scripts themselves are pure ASCII |

**Verification after fixes:** `demo.ps1` re-run (PS 5.1): same results, **Blocked after 0.6 s**.
