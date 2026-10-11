# URL Shortener — AI-Assisted Engineering Assignment (.NET 8)

A production-shaped URL shortener built with Claude Code under engineer-led controls: spec first, signed-off
decisions, quality gates, verification on real infrastructure, and a full AI traceability log.

**Start here:** [docs/FINAL-SUMMARY.md](docs/FINAL-SUMMARY.md) · [Architecture](docs/04-architecture.md) ·
[Scenarios](docs/scenarios/) · [AI traceability log](docs/ai-log/AI-TRACEABILITY.md)

| | |
|---|---|
| Core | Create / get / delete / redirect (302) / stats · API-key auth · idempotent create · URL safety policy |
| Scale | Redis cache-aside · Kafka event-driven analytics · separate Analytics Worker · stateless API replicas |
| Safety & reliability | Rate limiting · domain blocklist · security headers · circuit breaker · 503 + Retry-After on DB outage |
| Operability | JSON logs (Splunk-ready) with correlation + trace IDs · OpenTelemetry · `/health/live` + `/health/ready` |
| Evidence | 175 automated tests · coverage gate · k6 load tests · 10 chaos tests · 0 vulnerable packages |

## Prerequisites

| For | Install |
|---|---|
| Zero-setup mode and tests | [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) |
| Full infrastructure, load and chaos tests | [Docker Desktop](https://www.docker.com/products/docker-desktop/) |

## Run it: three ways

### 1. Zero-setup (SQLite, in-memory cache, no Docker)

```bash
dotnet run --project src/UrlShortener.Api
```

Swagger UI: http://localhost:5080/swagger · development API key: `dev-api-key-change-me`

```bash
curl -s -X POST http://localhost:5080/api/v1/links \
  -H "X-Api-Key: dev-api-key-change-me" -H "Content-Type: application/json" \
  -d '{"url":"https://learn.microsoft.com/dotnet","customAlias":"dotnet-docs"}'
curl -i http://localhost:5080/dotnet-docs
curl -s http://localhost:5080/api/v1/links/dotnet-docs/stats -H "X-Api-Key: dev-api-key-change-me"
```

### 2. Full infrastructure, apps on the host (best for debugging)

PostgreSQL, Redis and Kafka in Docker; API and Analytics Worker run locally against them:

```bash
./scripts/run-full-local.sh
```

Same API key and port as above. Optional telemetry viewer (logs, metrics, traces):

```bash
docker compose -f deploy/docker-compose.yml --profile observability up -d
```

Then open http://localhost:18888. The run script detects it automatically.

### 3. Everything in containers (production shape)

One-shot migration, 2 API replicas behind nginx, Analytics Worker, PostgreSQL, Redis, Kafka:

```bash
docker compose -f deploy/docker-compose.yml --profile app up -d --build --wait
```

API at http://localhost:8080 · API key: `compose-demo-api-key-0123456789` · `curl http://localhost:8080/health/ready`

Stop everything:

```bash
docker compose -f deploy/docker-compose.yml --profile app --profile observability down
```

## Test it

```bash
./scripts/quality-gate.sh
```

Restore with NuGet audit → format check → build with analyzers (warnings as errors) → 132 unit + 43 integration tests
with coverage → coverage threshold (≥ 80% Domain + Application) → vulnerable-dependency scan. CI runs the same script.

Load test (k6 in Docker; start mode 2 with `RATE_LIMITS=off`, since one load generator looks like one abusive client):

```bash
docker run --rm -i -e BASE_URL=http://host.docker.internal:5080 -e CODE=dotnet-docs grafana/k6:0.54.0 run - < loadtest/redirect.js
```

## API

| Method | Path | Auth | Purpose | Responses |
|---|---|---|---|---|
| POST | `/api/v1/links` | API key | Create (`url`, optional `customAlias` (stored lowercase), `expiresAt`; optional `Idempotency-Key` header) | 201 (replays add `Idempotent-Replayed: true`), 400, 401, 409, 422, 429, 503 |
| GET | `/api/v1/links/{code}` | API key | Link metadata | 200, 404 |
| DELETE | `/api/v1/links/{code}` | API key | Deactivate (soft delete) | 204, 404 |
| GET | `/api/v1/links/{code}/stats` | API key | Total, unique visitors, daily series, top referrers, top browsers | 200, 404 |
| GET | `/{code}` | public | Redirect | 302, 404, 410 (expired or blocked), 429, 503 |
| GET | `/health/live`, `/health/ready` | public | Probes | 200, 503 |

Errors are RFC 7807 `application/problem+json` with a stable `errorCode`. Full OpenAPI: `/swagger/v1/swagger.json`.

## Configuration (12-factor: environment variables)

| Variable | Default | Purpose |
|---|---|---|
| `Database__Provider` / `ConnectionStrings__Links` | `Sqlite` / local file | `Postgres` for full mode |
| `ConnectionStrings__Redis` | unset (in-memory cache) | Redis connection string |
| `Analytics__Mode` / `Kafka__BootstrapServers` | `Direct` | `Kafka` for event-driven analytics |
| `Auth__ApiKeys__0..n` | none outside Development | API keys (16+ chars; several for rotation) |
| `Shortener__VisitorHashSalt` | dev-only value | Required outside Development |
| `RateLimiting__CreatePerMinute` / `__RedirectPerMinute` / `__Enabled` | 30 / 300 / true | Abuse protection |
| `Security__BlockedDomains__0..n` | none | Blocked destination domains (and subdomains) |
| `Network__TrustedNetworks__0..n` | none | Proxies allowed to set `X-Forwarded-For` |
| `Health__FailReadinessOnDatabaseOutage` | false | See ADR 0006 |
| `OTEL_EXPORTER_OTLP_ENDPOINT` / `Telemetry__TraceSampleRatio` | unset / 1.0 dev, 0.1 otherwise | OpenTelemetry export |
| `Logging__Format` | `simple` in Development, `json` otherwise | Log format |

## Repository map

| Path | What |
|---|---|
| `docs/01-requirements.md` | Requirement normalization: ambiguities → signed-off decisions |
| `docs/02-task-breakdown.md`, `docs/jira-backlog.csv` | Epics/stories with dependencies (Jira-importable) |
| `docs/03-traceability-matrix.md` | Every requirement → code → test → status |
| `docs/04-architecture.md`, `docs/adr/` | Architecture overview and decision records |
| `docs/scenarios/` | Greenfield, brownfield and ambiguous scenarios: decomposition, execution, validation |
| `docs/perf/` | Raw load and chaos test results |
| `docs/ai-log/AI-TRACEABILITY.md` | Every AI step (accepted / edited / rejected) + engineer sign-off register |
| `docs/FINAL-SUMMARY.md` | Plan, artifacts, risks, trade-offs, assumptions, limitations |
| `CLAUDE.md`, `.github/copilot-instructions.md` | Custom instructions constraining the AI assistants |
| `src/` | Domain, Application, Infrastructure, Api, AnalyticsWorker |
| `tests/` | Unit (incl. architecture rules) and integration tests |
| `deploy/`, `k8s/`, `.github/workflows/`, `scripts/` | Compose, Kubernetes, CI, quality gate and run scripts |
