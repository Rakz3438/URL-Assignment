# AI Traceability Log

Every significant AI-assisted step is recorded here. **Outcome** is one of:

- **Accepted**: used as generated, after review.
- **Edited**: used after engineer changes (the change is described).
- **Rejected**: not used (the reason is described).

Tools: Claude Code (Opus) in CLI/desktop. Quality gates applied to all accepted output are listed
in `CLAUDE.md`.

| # | Date | Story | Intent / prompt summary | AI output | Outcome | Rationale (reviewed by engineer) |
|---|---|---|---|---|---|---|
| 1 | 2026-10-11 | — | Map the JD to the assignment and choose a stack | Proposed Python/FastAPI first | **Rejected** | Role is .NET. Re-planned as ASP.NET Core 8 + Kafka + Redis + PostgreSQL to match the JD |
| 2 | 2026-10-11 | — | Map JD requirements to project features | JD→feature table (Kafka analytics, outbox, Polly, OTel, 12-factor, K8s) | **Accepted** | Every JD keyword is backed by a real feature, not a buzzword |
| 3 | 2026-10-11 | — | Environment check | Found no .NET SDK or Docker | **Accepted** | Engineer approved installing .NET 8 SDK via the official Microsoft script into `~/.dotnet` (no sudo). Docker deferred |
| 4 | 2026-10-11 | URL-2 | Draft requirement normalization from a one-line brief | `docs/01-requirements.md` with 11 ambiguity decisions | **Accepted** | Reviewed by engineer; all 5 high-impact decisions approved without change |
| 5 | 2026-10-11 | URL-2 | Draft Jira-style decomposition with dependencies | `docs/02-task-breakdown.md`, `jira-backlog.csv` | **Accepted** | Reviewed by engineer; all 5 high-impact decisions approved without change |
| 6 | 2026-10-11 | URL-2 | Draft AI custom instructions | `CLAUDE.md`, `.github/copilot-instructions.md` | **Accepted** | Reviewed by engineer; all 5 high-impact decisions approved without change |
| 7 | 2026-10-11 | — | Push to GitHub | First push failed (no CLI credentials); then found the repo URL did not exist (404) | **Edited** | Engineer chose to have the AI create a **private** repo via the browser after engineer signed in; final push done by engineer via GitHub Desktop (engineer controls what leaves the machine) |
| 8 | 2026-10-11 | — | Self-correction | AI had claimed the repo was public based on a flawed check (exit code of `head`, not `git`) | **Rejected** (own claim) | Re-verified with the GitHub API (404 → private or missing). Lesson: verify the exit code of the command that matters |
| 9 | 2026-10-11 | URL-1 | Scaffold Clean Architecture solution with central package management | 4 src + 2 test projects, `Directory.Build.props` (warnings as errors, analyzers), `Directory.Packages.props` | **Edited** | Moved `AnalyticsWorker` out of URL-1 into URL-11 so the brownfield scenario starts from a genuinely monolithic baseline |
| 10 | 2026-10-11 | URL-1 | Choose test assertion library | FluentAssertions latest | **Edited** | Pinned to 6.12.2: v7+ moved to a commercial licence. Licence review is part of dependency approval |
| 11 | 2026-10-11 | URL-3 | Domain model: `ShortLink`, `ShortCode`, `ClickEvent` | Entities with invariants, soft delete, UTC normalization | **Accepted** | Target URL immutable by design (anti bait-and-switch). Code generator uses `RandomNumberGenerator.GetString` (unbiased); verified against CLAUDE.md rule |
| 12 | 2026-10-11 | URL-4 | URL safety policy + attack-case table | `UrlPolicy` + 36 test cases (schemes, credentials, loopback encodings, private ranges, metadata IP, IPv6, self-loop) | **Edited** | Test table exposed that IDN hosts were stored as Unicode, which would break the ASCII-only `Location` header (500 on redirect). Fixed: normalize host to punycode; regression test added at unit and HTTP level |
| 13 | 2026-10-11 | URL-5 | EF Core persistence, SQLite + PostgreSQL | One model, provider subclasses with separate migration sets; unique indexes on `Code` and `IdempotencyKey` | **Edited** | SQLite cannot order/compare `DateTimeOffset`: added binary converter for SQLite only. Unique-violation detection narrowed from SQLite code 19 (any constraint) to extended code 2067 (UNIQUE only) so NOT NULL/FK errors aren't misreported as conflicts |
| 14 | 2026-10-11 | URL-6/URL-9 | Endpoints + stats query | Minimal API endpoints, ProblemDetails mapping, stats via LINQ GroupBy | **Edited** | Manual smoke test found `/stats` returned 500: EF could not translate `OrderBy` over a record built in the projection. Rewrote to order on the aggregate in SQL; regression integration test added |
| 15 | 2026-10-11 | URL-8 | Idempotency design | Initial idea: separate idempotency table | **Rejected** | A separate table needs two writes and a race between them. Chosen: nullable unique `IdempotencyKey` on the link row itself, so one atomic insert decides; concurrent-retry test proves a single link is created |
| 16 | 2026-10-11 | URL-20 | Dependency vulnerability scan | Gate flagged `SQLitePCLRaw.lib.e_sqlite3 2.1.6` (High, CVE-2025-6965) via EF Core | **Edited** | First pin to 2.1.11 was still vulnerable; checked the GitHub advisory (affects ≤ 2.1.11) and pinned 2.1.13 with transitive pinning. Re-scan: 0 vulnerable packages |
| 17 | 2026-10-11 | URL-13 | Planned "brownfield bug" (alias race) | Plan said fix the race later | **Rejected** | Race prevented by design from day one; seeding a fake bug would be dishonest. URL-13 redefined (see task breakdown note) |
| 18 | 2026-10-11 | — | Observation, deferred | EF Core logs handled unique-violations at Error level | **Deferred → URL-18** | Expected 409 conflicts would raise false alerts in Splunk. To be addressed in the observability story |
| 19 | 2026-10-11 | — | Engineer asked: "did you check the requirements doc line by line?" | AI had verified via tests and gates only, not against the spec. Re-audited every FR/NFR/decision against the code | **Edited** | Audit found 2 in-scope defects (generated code could equal a 7-char reserved route; coverage target measured but not enforced), now fixed with a test and a gate. It also found 4 deviations needing an engineer decision and 1 unverified claim (PostgreSQL mode never executed). Captured in `docs/03-traceability-matrix.md`. Lesson: passing tests ≠ meeting the spec; traceability must be checked explicitly |
| 20 | 2026-10-11 | FR-5, FR-8, A-6, URL-18 | Present the 4 audit deviations as options with a recommendation | 4 decisions with trade-offs | **Accepted** | Engineer chose all 4 recommendations: lowercase aliases; replay identical 201 + header; amend A-6 (re-signed); proxy IP fix scheduled in URL-18 |

## High-impact sign-off register

| Story / decision | What was approved | Approved by | Date |
|---|---|---|---|
| A-1 | Random 7-char Base62 codes from CSPRNG; DB unique index arbitrates | Rakz3438 | 2026-10-11 |
| A-2 | 302 redirects | Rakz3438 | 2026-10-11 |
| A-4 | API key on management endpoints; public redirect | Rakz3438 | 2026-10-11 |
| A-6 | No raw IP stored; salted SHA-256 hash only | Rakz3438 | 2026-10-11 |
| A-8 | URL safety policy as specified | Rakz3438 | 2026-10-11 |
| A-6 (amended) | HMAC-SHA256 of IP + UA instead of salted SHA-256 of IP | Rakz3438 | 2026-10-11 |
| Greenfield URL-1…9 (incl. 🔒 URL-3, URL-4, URL-5) | Approved for commit after review of code, AI log entries 9–20 and traceability matrix | Rakz3438 | 2026-10-11 |
| FR-5 / FR-8 changes | Aliases stored lowercase; idempotent replay returns identical 201 + `Idempotent-Replayed` header | Rakz3438 | 2026-10-11 |
