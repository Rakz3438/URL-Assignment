# CLAUDE.md — Custom instructions for AI assistants on this repo

These rules apply to any AI coding assistant (Claude Code, GitHub Copilot) working in this
repository. They encode the engineer's standards; the AI works *within* them. The engineer owns
every merged line.

## Project
URL shortener service — .NET 8, ASP.NET Core, Clean Architecture.
Specs live in `docs/01-requirements.md` (decisions A-1…A-11) and `docs/02-task-breakdown.md`
(stories URL-1…URL-25). **Spec first:** if a change isn't traceable to a story or decision, stop and ask.

## Architecture rules (non-negotiable)
- Layer direction: `Api` → `Application` → `Domain`; `Infrastructure` → `Application`/`Domain`.
  `Domain` references nothing. No EF Core, ASP.NET, or Kafka types outside `Infrastructure`/`Api`.
- The redirect path (`GET /{code}`) must never block on analytics, Kafka, or any non-critical dependency.
- External dependencies (cache, bus, clock, code generator) are behind interfaces so tests can fake them.
- Configuration comes from environment/`IOptions<T>` only (12-factor). No hard-coded hosts, ports, secrets.

## Code standards
- Nullable enabled, warnings as errors, `async` all the way with `CancellationToken` passed through.
- Errors returned as RFC 7807 `ProblemDetails`. Never leak exception messages or stack traces to clients.
- Randomness for codes: `System.Security.Cryptography.RandomNumberGenerator` only, never `System.Random`.
- Uniqueness is enforced by database constraints, not check-then-insert.
- Log with structured templates (`_logger.LogInformation("Created {Code}", code)`), never string interpolation.
  Never log full target URLs at Information level (they may contain tokens), API keys, or raw IPs.

## Testing
- Every behavior change ships with tests. Bug fixes ship with a failing-first regression test.
- Unit tests: xUnit + FluentAssertions. Integration tests: `WebApplicationFactory`.
- Test names: `Method_Scenario_ExpectedResult`.

## Security guardrails for AI usage
- Never paste secrets, real customer data, or production URLs into prompts.
- Do not add NuGet packages without stating why and checking they are maintained; prefer BCL/Microsoft packages.
- Changes marked 🔒 in the task breakdown (schema, security policy, auth, delivery semantics) require
  explicit engineer sign-off before commit.

## Traceability
- Record every significant AI interaction in `docs/ai-log/AI-TRACEABILITY.md`:
  story ID, prompt intent, outcome (**Accepted / Edited / Rejected**), and rationale.
- Commit messages reference the story ID, e.g. `URL-6: add redirect endpoint`.

## Quality gate (run before every commit)
`./scripts/quality-gate.sh`: restore with NuGet audit → format check → build (warnings as errors) →
tests + coverage → vulnerable-dependency scan. CI runs the same script.
