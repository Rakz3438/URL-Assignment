# Scenario 1 — Greenfield: core URL shortener from a one-line requirement

| Field | Value |
|---|---|
| Stories | URL-1 … URL-9 |
| Input | "Build a URL shortener service from scratch with core APIs, analytics, and reliability features." |
| Status | ✅ Complete. Engineer sign-off: Rakz3438, 2026-10-11 |

## 1. Requirement understanding

The one-line brief was normalized into 9 functional requirements, 7 non-functional requirements and **11 ambiguity
decisions**, each with options, choice and rationale ([01-requirements.md](../01-requirements.md)). Five were marked
high-impact and signed off by the engineer before any code: code generation (A-1), redirect status (A-2), auth model
(A-4), click privacy (A-6), URL safety policy (A-8).

## 2. Decomposition

6 epics → 25 Jira-style stories with dependencies, acceptance criteria and an AI plan per story
([02-task-breakdown.md](../02-task-breakdown.md), importable [jira-backlog.csv](../jira-backlog.csv)).
Critical path for this scenario: URL-1 skeleton → URL-3 domain → URL-4 URL policy → URL-5 persistence → URL-6 endpoints.

| Story | Delivered |
|---|---|
| URL-1 | Solution skeleton, central package management, analyzers as errors, quality-gate script |
| URL-2 | `CLAUDE.md` + `copilot-instructions.md` (custom instructions), AI traceability log |
| URL-3 🔒 | `ShortLink` (immutable target, soft delete, UTC), `ShortCode` (charset, reserved words), CSPRNG generator |
| URL-4 🔒 | `UrlPolicy`: 29 attack patterns: script schemes, credentials, loopback in decimal/hex, private and metadata IPs, IPv6, self-loop |
| URL-5 🔒 | EF Core with SQLite and PostgreSQL migration sets; unique indexes decide uniqueness |
| URL-6/7 | Create / get / delete / redirect / stats; API-key auth (fixed-time compare); ProblemDetails; OpenAPI |
| URL-8 | `Idempotency-Key` on the link row itself (one atomic insert decides concurrent retries) |
| URL-9 | Synchronous click recording: the deliberately simple baseline that Scenario 2 later measures and replaces |

## 3. AI-assisted execution (highlights from the traceability log)

| What happened | Outcome |
|---|---|
| AI proposed Python/FastAPI | **Rejected**: the role is .NET |
| AI proposed a separate idempotency table | **Rejected** by AI on review: two writes and a race; moved the key onto the link row |
| Smoke test: `/stats` returned 500 (untranslatable EF query) | **Edited**: query rewritten; regression test added |
| Test table: IDN hosts stored as Unicode, which would crash redirects (non-ASCII `Location`) | **Edited**: punycode normalization |
| Vulnerability gate: SQLite native library CVE-2025-6965 (High) via EF Core | **Edited**: first pin still vulnerable; advisory checked; 2.1.13 pinned |
| Engineer asked "did you check the requirements line by line?" | AI had verified with tests only. Full audit → [03-traceability-matrix.md](../03-traceability-matrix.md): 2 defects fixed, 4 deviations decided by the engineer, 1 unverified claim (PostgreSQL) flagged and later verified |
| Planned "brownfield bug" (alias race) | **Rejected**: prevented by design; a fake bug would misrepresent the work. The real defect was found in Scenario 2 |

## 4. Validation

- 121 tests at the end of the scenario (unit + `WebApplicationFactory` integration), coverage 92–98% per layer,
  0 vulnerable packages, 0 warnings.
- 20 concurrent creates of one alias: exactly 1 × 201 and 19 × 409, no 500, on SQLite and later on PostgreSQL.
- PostgreSQL mode verified end-to-end in Docker after Docker was installed (migration, indexes, every endpoint).
- Requirement-by-requirement audit, after the engineer challenged the AI's verification (lesson: *passing tests ≠ meeting the spec*).
