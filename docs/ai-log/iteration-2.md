# Iteration 2 — Reporting.Api surface: auth, validation, post lookup

**Agent role:** Developer (Claude Code, Opus 5.5). The tests were written in the same session, acting as the Test agent.

**Prompt (summary):** "Implement Iteration 2: a DemoHeader auth handler (`X-Demo-UserId`, no header → 401), `POST /api/reports`
with validation (400 ProblemDetails), a typed `IPostsClient` (404 → not found, failure → 503), self-report → 422, and
`IReportService` returning `Created | Duplicate` mapped to 201/409. Use a temporary in-memory store. Apply the items carried
from the Iteration 1 review: postId format, escaping, any 404 = not found, a 200 with an empty author is an error, and user
IDs are opaque and case-sensitive."

## Output
| File | Responsibility |
|---|---|
| `Auth/DemoHeaderAuthenticationHandler.cs` | Reads `X-Demo-UserId`. Missing → NoResult (401 on challenge). Multiple values or invalid format → Fail (401). Otherwise a principal with a `NameIdentifier` claim. `GetRequiredUserId()` is the **only** source of the reporter ID. A large "NOT PRODUCTION" doc comment explains the production replacement (JWT from the IdP, `sub` claim). |
| `Identifiers.cs` | `[GeneratedRegex]` formats: user `^[A-Za-z0-9._@-]{1,64}$`, post `^[A-Za-z0-9-]{1,64}$`. IDs are opaque and ordinal. |
| `Posts/PostsClient.cs` | Typed HttpClient (IHttpClientFactory, base URL + 3 s timeout from validated options). Its own `PostInfo(PostId, AuthorId)` record, not shared with Posts.Api. Escapes the ID. **Any** 404 → `null`. Other statuses, transport errors, timeouts, bad JSON, an empty `authorId` or a mismatched `postId` → `PostsServiceUnavailableException`. It never guesses a target. |
| `Reports/ReportModels.cs` | The request has **no** reporter or target fields. The response is `(reportId, reportedUserId, uniqueReportCount, blockRequested)`. A closed `SubmitReportResult` union: `Created | Duplicate`. |
| `Reports/SubmitReportValidator.cs` | postId format, reason trimmed and 3–500 chars. Collects all errors together. |
| `Reports/SubmitReportHandler.cs` | Flow: validate → look up the author → self-report check (ordinal) → store → map the result: 400/404/422/503/201/409. Structured logging. |
| `Reports/InMemoryReportService.cs` | **Temporary.** Lock-guarded set plus counter, so the API runs before Iteration 3. |
| `Program.cs` | **Refuses to start in Production** with demo auth. Logs a startup warning. ProblemDetails + `UseStatusCodePages`. Options with `ValidateOnStart`. |

## Verification
- `dotnet build -warnaserror` → clean (xUnit1051 analyzer: the tests use `TestContext.Current.CancellationToken`).
- `dotnet test --solution ForumReporting.slnx` → **62/62**. The new tests are:
  - Unit, auth handler (8): missing, empty, whitespace, invalid characters, too long, multiple values, valid. The valid case also checks the ID is preserved exactly (`Reporter-1`).
  - Unit, validator (15): bounds, trimming, path-like IDs (`a/b`, `../admin`), all errors reported together.
  - Unit, handler (8): 201 targets the **post author**; block flag; 409; 422 is never stored; case-sensitive self-check; 404 and 503 are never stored; 400 never calls Posts.
  - Unit, PostsClient (13): 200; any 404 (ProblemDetails or empty) → null; 500/502/401 → unavailable; six unusable-200 variants; transport failure; path escaping (`a/b?c` → `a%2Fb%3Fc`).
  - Integration (WebApplicationFactory, fake Posts) (9): 401 with no or invalid identity; 201; **same reporter + another post by the same author → 409** with problem+json; **body-supplied `reporterUserId`/`reportedUserId` are ignored**; 422; 404; 400 problem; malformed JSON → 400.
- Manual run with Posts.Api (:5101) and Reporting.Api (:5103):
  - no header → 401;
  - reporter-1..5 on post-1 → counts 1..5, `blockRequested:true` on the 5th;
  - reporter-1 on post-2 → 409;
  - self → 422; post-999 → 404; invalid → 400 with both field errors;
  - Posts.Api stopped → 503;
  - `ASPNETCORE_ENVIRONMENT=Production` → the startup exception, as designed.

> The verification above describes the **first** implementation (62 tests). The counts of 15 validator and 13 PostsClient
> tests were wrong; the reviewer counted 16 and 14. The state after the review fixes is described below.

## Review (Reviewer agent: general-purpose subagent, read-only; it ran the services and probed them with curl)

**Prompt (summary):** "Review the uncommitted Iteration 2 diff. Check: security (can the body choose the reporter or target,
can demo auth leak to prod), status mapping, PostsClient timeout/exception/escaping, validator edge cases (Unicode, control
characters), PII in logs, test independence and order dependence, the temporary in-memory store surviving into Iteration 3, and
the accuracy of this log."

**Result:** 0 blockers, 6 should-fix, 9 nits.

| # | Finding | Verified by me? | Decision |
|---|---|---|---|
| S1 | The Production guard is a **deny-list**: with `ASPNETCORE_ENVIRONMENT=Staging` the impersonation header worked | Yes (logic) | **Fixed:** allow-list of Development and Testing. Re-verified: Staging now refuses to start |
| S2 | A reason of `\u0000\u0000\u0000` or zero-width spaces passed validation. NUL would make a PostgreSQL `text` insert fail with a 500 in Iteration 3 | **Yes, reproduced** with 2 failing probe tests first | **Fixed:** reject control characters (except `\n\r\t`) and require at least one letter or digit. 4 rejection + 2 acceptance tests (including Cyrillic text) |
| S3 | Is `blockRequested` state (count ≥ 5) or the transition ("this report triggered it")? Iteration 3's one-shot update would silently change its meaning | Yes | **Decided: state.** Documented on the response record; an end-to-end test asserts `[F,F,F,F,T,T]` for 6 reporters. The *event* is still created exactly once (Iteration 3) |
| S4 | `AuthorId` from Posts was only checked for null or whitespace, but it becomes the stored reported-user ID | Yes | **Fixed:** must pass `Identifiers.IsValidUserId`. 2 new tests (space, 65 characters) |
| S5 | The in-memory store was registered in Program **and** re-registered by the test factory, so it would silently override Postgres in Iteration 3 | Yes | **Fixed:** the factory override was removed (only Posts is faked). Program has a `TODO(iteration-3): DELETE` marker on the single registration |
| S6 | Missing integration tests: 5 distinct reporters end-to-end, wired 503, and a weak `>= 1` assertion caused by a shared author | Yes | **Fixed:** each test now gets its own author through a per-test fake post factory. Added the threshold test (6 reporters), a duplicate that is not counted (the next reporter is #2), and a 503 with `Retry-After` |
| N1 | `Retry-After` on 503 | n/a | **Fixed:** `RetryAfterResult` decorator, still exposing `IStatusCodeHttpResult` for the unit tests |
| N2 | Redundant `FrameworkReference` in the unit tests | Yes (the build passes without it) | **Fixed:** removed |
| N3 | Wrong test counts in this log | Yes | **Fixed:** the note above |
| N4 | The Developer exception page leaks the stack trace and request headers in Development | n/a | **Accepted** as Development-only; goes in the README |
| N5 | Returning `uniqueReportCount` helps coordinated brigading | n/a | **Accepted** trade-off, since the task plan asks for it; goes in the README |
| N6 | 201 without `Location` | n/a | **Accepted:** there is no per-report GET; goes in the README |
| N7 | Reporter and target IDs logged at Information are pseudonymous PII | n/a | **Accepted** for the prototype. README: production needs a retention or redaction policy. The reason text is never logged |
| N8 | No retry or resilience on the Posts GET | n/a | **Rejected for the prototype:** fail fast + 503 + `Retry-After` is explicit and testable. In production, add `AddStandardResilienceHandler` and drop `HttpClient.Timeout` |
| N9 | Posts 401/403 → 503 vs 502 | n/a | **Rejected:** keep 503, because from the client's view it is "retry later". The upstream status is logged |

**Lesson for the workflow:** the two most valuable findings came from the reviewer **running** the service (Staging
impersonation, NUL in the reason) rather than only reading the code. Since Iteration 2, reviewer prompts explicitly allow running
and probing the service.

## Verification after fixes
- `dotnet build -warnaserror` → clean. `dotnet test --solution ForumReporting.slnx` → **72/72** (60 unit, 11 Reporting integration, 1 Account placeholder).
- `ASPNETCORE_ENVIRONMENT=Staging` → refuses to start with an explicit message.
