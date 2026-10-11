# AI Traceability Log

Every significant AI-assisted step is recorded here. **Outcome** is one of:

- **Accepted**: used as generated, after review.
- **Edited**: used after engineer changes (the change is described).
- **Rejected**: not used (the reason is described).

Tools: Claude Code (Opus) in CLI/desktop. Quality gates applied to all accepted output are listed
in `CLAUDE.md`.

| # | Date | Story | Intent / prompt summary | AI output | Outcome | Engineer rationale |
|---|---|---|---|---|---|---|
| 1 | 2026-10-11 | — | Map the JD to the assignment and choose a stack | Proposed Python/FastAPI first | **Rejected** | Role is .NET. Re-planned as ASP.NET Core 8 + Kafka + Redis + PostgreSQL to match the JD |
| 2 | 2026-10-11 | — | Map JD requirements to project features | JD→feature table (Kafka analytics, outbox, Polly, OTel, 12-factor, K8s) | **Accepted** | Every JD keyword is backed by a real feature, not a buzzword |
| 3 | 2026-10-11 | — | Environment check | Found no .NET SDK or Docker | **Accepted** | Engineer approved installing .NET 8 SDK via the official Microsoft script into `~/.dotnet` (no sudo). Docker deferred |
| 4 | 2026-10-11 | URL-2 | Draft requirement normalization from a one-line brief | `docs/01-requirements.md` with 11 ambiguity decisions | _Pending review_ | |
| 5 | 2026-10-11 | URL-2 | Draft Jira-style decomposition with dependencies | `docs/02-task-breakdown.md`, `jira-backlog.csv` | _Pending review_ | |
| 6 | 2026-10-11 | URL-2 | Draft AI custom instructions | `CLAUDE.md`, `.github/copilot-instructions.md` | _Pending review_ | |

## High-impact sign-off register

| Story / decision | What was approved | Approved by | Date |
|---|---|---|---|
| | | | |
