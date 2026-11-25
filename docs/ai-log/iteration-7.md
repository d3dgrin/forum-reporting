# Iteration 7 — Documentation

**Agent role:** Developer (Claude Code, Opus 5.5).

**Prompt (summary):** "Write the README (architecture, the design choices behind duplicate prevention and the threshold, assumptions,
run/test instructions, what remains), docs/architecture.mmd for the implemented solution, and AI_WORKFLOW.md compiled from the
real per-iteration logs. Keep it concise; this is a demo."

## Output
- `README.md`: Mermaid architecture + sequence diagrams, a service ownership table, design choices and trade-offs, assumptions
  (including what "blocked" means), run/demo/test instructions, a table mapping requirements to tests, and next steps.
- `docs/architecture.mmd`: the implemented architecture. Part 1 stays in `diagrams/`.
- `AI_WORKFLOW.md`: Architect / Developer / Test / Reviewer examples with prompt, output, how it was verified, and what was changed or rejected.

## Verification
- Every test name quoted in the README was checked with grep and exists.
- Final full run: `dotnet build -warnaserror` → 0 warnings; `dotnet test --solution ForumReporting.slnx` → **88/88**.

## Review (Reviewer agent: docs vs code vs logs, read-only)

The Mermaid syntax, ports, routes, commands, status codes, config key, seeds and all design claims are accurate.

| Finding | Decision |
|---|---|
| README: "no control characters", but `\n\r\t` are allowed | **Fixed** |
| README: "about a second", but the worst case is the ~2 s poll interval | **Fixed** |
| AI_WORKFLOW: "every iteration ran against Docker / nothing accepted without running" was overstated | **Fixed:** the exceptions are named (iterations 1, 2 and 6, and PS7 untested) |
| AI_WORKFLOW: the Architect plan wasn't backed by a logged source | **Fixed:** the approved plan is now in `docs/ai-log/00-architect-iteration-plan.md` |
| "20 parallel identical reports", but they use different posts | **Fixed** |
| "Concurrency tests failed 3/3" is unspecific | **Fixed:** names test #8 and "mixed" |
| Staging impersonation was called a "nit", but it was should-fix | **Fixed** |
| "88 tests" is unconfirmed for the final tree | **Confirmed** by the final run above |
| Whether GitHub renders a standalone `.mmd` file | **Accepted:** the README embeds the diagrams, which GitHub does render |
