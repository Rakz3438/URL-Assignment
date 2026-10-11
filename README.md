# URL Shortener — AI-Assisted Engineering Assignment

A production-shaped URL shortener in **.NET 8 / ASP.NET Core**, built with Claude Code and GitHub Copilot
under engineer-led controls (spec-first, traceability log, quality gates, human sign-off).

> Status: **greenfield ✅, brownfield ✅** (Kafka event-driven analytics, Redis cache, Analytics Worker:
> 3.3× throughput, p95 9.6 → 3.1 ms, redirects unaffected by analytics/Kafka/Redis outages). Ambiguous scenario next.
> See [docs/scenarios/02-brownfield.md](docs/scenarios/02-brownfield.md).

## Quick start (zero setup: SQLite, no Docker)

Prerequisite: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
dotnet run --project src/UrlShortener.Api
```

Swagger UI opens at http://localhost:5080/swagger. The development API key is `dev-api-key-change-me`.

```bash
# Create a short link
curl -s -X POST http://localhost:5080/api/v1/links \
  -H "X-Api-Key: dev-api-key-change-me" -H "Content-Type: application/json" \
  -d '{"url":"https://learn.microsoft.com/dotnet","customAlias":"dotnet-docs"}'

# Follow it (302)
curl -i http://localhost:5080/dotnet-docs

# Analytics
curl -s http://localhost:5080/api/v1/links/dotnet-docs/stats -H "X-Api-Key: dev-api-key-change-me"
```

## Full infrastructure mode (PostgreSQL + Redis + Kafka + Analytics Worker)

Requires Docker. Same code, switched by environment variables only:

```bash
./scripts/run-full-local.sh
```

Load test (k6 runs in Docker, nothing to install):

```bash
docker run --rm -i -e BASE_URL=http://host.docker.internal:5080 -e CODE=<code> grafana/k6:0.54.0 run - < loadtest/redirect.js
```

## API

| Method | Path | Auth | Purpose | Responses |
|---|---|---|---|---|
| POST | `/api/v1/links` | API key | Create link (`url`, optional `customAlias` (stored lowercase), `expiresAt`; optional `Idempotency-Key` header) | 201 (replays add `Idempotent-Replayed: true`), 400, 401, 409, 422 |
| GET | `/api/v1/links/{code}` | API key | Link metadata | 200, 404 |
| DELETE | `/api/v1/links/{code}` | API key | Deactivate (soft delete) | 204, 404 |
| GET | `/api/v1/links/{code}/stats` | API key | Clicks: total, unique visitors, daily series, top referrers, top browsers | 200, 404 |
| GET | `/{code}` | public | Redirect | 302, 404, 410 (expired) |

Errors use RFC 7807 `application/problem+json` with a stable `errorCode`.

## Quality gate

```bash
./scripts/quality-gate.sh
```

Restore with NuGet audit → format check → build with analyzers (warnings as errors) → 118 unit + integration
tests with coverage → vulnerable-dependency scan.

## Repository map

| Path | What |
|---|---|
| `docs/01-requirements.md` | Requirement normalization: ambiguities → signed-off decisions |
| `docs/02-task-breakdown.md`, `docs/jira-backlog.csv` | Epics/stories with dependencies (Jira-importable) |
| `docs/ai-log/AI-TRACEABILITY.md` | Every AI step: accepted / edited / rejected, with rationale |
| `CLAUDE.md`, `.github/copilot-instructions.md` | Custom instructions that constrain the AI assistants |
| `src/UrlShortener.Domain` | Entities and invariants (no dependencies) |
| `src/UrlShortener.Application` | Use cases, URL safety policy, ports |
| `src/UrlShortener.Infrastructure` | EF Core (SQLite/PostgreSQL), code generator |
| `src/UrlShortener.Api` | Minimal API endpoints, auth, OpenAPI |
| `tests/` | xUnit unit tests (incl. architecture rules) and `WebApplicationFactory` integration tests |
