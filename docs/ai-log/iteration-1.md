# Iteration 1 — Contracts and Posts.Api stub

**Agent role:** Developer (Claude Code, Opus 5.5).

**Prompt (summary):** "Implement Iteration 1: event contract `UserBlockRequested` and shared messaging topology
names in `Contracts` (no domain logic); Posts.Api stub with `GET /api/posts/{postId}` → `{ postId, authorId }` or 404,
seeded with demo data (target-1 with several posts, a second target, posts by reporters)."
(The endpoint actually returns `{ postId, authorId, title }`.)

## Output
- `Contracts/Events/UserBlockRequested.cs`: a sealed record `(EventId, UserId, ReportCount, Reason, RequestedAt)` plus a
  versioned `EventType` constant `forum.reporting.user-block-requested.v1`.
- `Contracts/Messaging/MessagingTopology.cs` defines:
  - the exchange `forum.events` and the DLX `forum.events.dlx`;
  - the routing key `reporting.user-block-requested.v1`;
  - the queue `account.user-block-requested` and its `.dlq`.
- `Contracts/Messaging/ContractJson.cs`: shared `JsonSerializerOptions` (Web defaults, camelCase), because the wire format is part of the contract.
- `Posts.Api/Posts/PostsCatalog.cs`: an in-memory, read-only seeded catalogue, registered as a singleton.
- `Posts.Api/Program.cs`:
  - `GET /api/posts/{postId}` returns 200, or 404 as ProblemDetails;
  - `GET /api/posts` lists posts for the demo.
- `.gitattributes`: normalises line endings (follow-up from iteration 0).

### Design decision
- The routing key is **versioned and namespaced** (`reporting.user-block-requested.v1`), so a v2 contract can run alongside v1.
  (The plan named no routing key; an earlier draft of this log wrongly called this a deviation.)

## Verification
- `dotnet build -warnaserror` → clean. `dotnet test --solution ForumReporting.slnx` → 5/5. The new tests are:
  - the contract round-trips through `ContractJson.Options`;
  - the wire field names are exactly `eventId, userId, reportCount, reason, requestedAt`.
- Manual run of Posts.Api against :5101:
  - `/api/posts/post-2` → 200 `{"postId":"post-2","authorId":"target-1",...}`;
  - `/api/posts/nope` → 404 ProblemDetails;
  - `/api/posts/POST-2` → 404 (IDs are case-sensitive and ordinal);
  - `/api/posts` lists the 10 seeded posts.

## Review (Reviewer agent: general-purpose subagent, read-only)

**Prompt (summary):** "Review the uncommitted Iteration 1 diff. Check: contract design and versioning, the shared
JsonSerializerOptions, topology completeness for the outbox publisher and DLQ, the thinness of the Posts stub and whether
its seed data covers the demo, the tests, .gitattributes, and friction for Iteration 2's typed HttpClient. Rank findings
with file:line, failure scenario and fix."

**Result:** 0 blockers, 5 should-fix, 7 nits.

| # | Finding | Verified by me? | Decision |
|---|---|---|---|
| S1 | "Start order doesn't matter" is false: a message published with confirms to an exchange with **no bound queue** is confirmed and then dropped. The outbox would mark it published, and the block request would be **silently lost** if Reporting starts before Account.Api has ever run | Yes (RabbitMQ semantics: unroutable + confirm = ack) | **Fixed in docs/contract now, implementation in Iteration 4:** both sides declare the full topology, and the publisher uses `mandatory: true` and treats a basic.return as a failure. The topology remarks now state this. |
| S2 | Topology incomplete: no DLQ routing key or queue args; mismatched args → 406 PRECONDITION_FAILED | Yes | **Fixed:** `DeadLetterRoutingKeys`, `QueueArguments.ForAccountUserBlockRequested()` (quorum + DLX args, fresh dictionary per call) |
| S3 | `ContractJson.Options` is mutable until first use, so one service could change the wire format | Yes | **Fixed:** `MakeReadOnly(populateMissingResolver: true)` + test |
| S4 | A malformed message (missing/null `userId`, missing `eventId`) deserialized to defaults → NRE or bogus processing instead of DLQ | **Yes, reproduced** with a failing test first | **Fixed:** `RespectNullableAnnotations` + `RespectRequiredConstructorParameters`. The theory test covers 3 malformed payloads |
| S5 | Iteration 2: user-ID case sensitivity, post-ID format, URL escaping, a 404 with an empty body, a 200 with an empty `authorId` | Partly (unknown route `/api/posts/a/b` returned an empty 404) | **Carried into the Iteration 2 checklist.** Decision: user IDs are opaque and case-sensitive, like OIDC `sub` claims, and this will be documented, not normalised |
| N1 | "UTC" isn't enforced, and the round-trip test used offset 0, so a lost offset would go unnoticed | Yes | **Fixed:** the test now uses +02:00 and asserts the offset. The publisher will use `TimeProvider.GetUtcNow()` (Iteration 3) |
| N2 | `.gitattributes` gaps | n/a | **Fixed:** `*.ps1` crlf, Dockerfile lf, svg/jpg/ico |
| N3 | `AddProblemDetails` has no effect without status-code pages | Yes | **Fixed:** `UseStatusCodePages()`, so `/api/posts/a/b` now returns 404 `application/problem+json` |
| N4 | `FrozenDictionary` would state the intent | n/a | **Fixed** |
| N5 | `post-1{i}` breaks for i ≥ 10 | Yes | **Fixed:** `post-{10 + i}` |
| N6 | The log called the routing key a "deviation" and omitted `title` | Yes | **Fixed** |
| N7 | No Posts.Api tests | n/a | **Rejected:** it is a stub of an existing service, curl-verified here. Testing it would need a 4th test project (or a second `Program` reference in one project), which isn't worth it in a 4h timebox |

**Lesson for the workflow:** S1 is a classic distributed-systems trap: publisher confirms only mean "the broker took
responsibility", not "a consumer queue got it". The reviewer caught it at contract-design time, before any publisher code existed.

## Verification after fixes
- `dotnet build -warnaserror` → clean. `dotnet test --solution ForumReporting.slnx` → **9/9**.
- Posts.Api: `/api/posts/a/b` → 404 `application/problem+json`; `/api/posts/post-16` → `reporter-6`.
