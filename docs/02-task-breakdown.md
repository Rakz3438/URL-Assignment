# 02 — Task Decomposition (Agile / Jira-style)

| Field | Value |
|---|---|
| Status | **APPROVED by engineer (Rakz3438), 2026-10-11** |
| Import | [`jira-backlog.csv`](jira-backlog.csv) can be imported into Jira (Issue Type, Summary, Epic Link, Description, Story Points, Labels) |

Hierarchy: **Epic → Story → Task**. Each story has acceptance criteria (AC), dependencies, and an
**AI usage plan**: what the AI drafts, and what the engineer must verify.
🔒 = high-impact change, needs explicit human sign-off before merge.

---

## Sequencing overview

```
E0 Foundations ──► E1 Greenfield core ──► E2 Brownfield modernization ──► E3 Ambiguous: safe & reliable
                                    │                                   │
                                    └──────────► E4 Delivery (Docker / K8s / CI) ◄──┘
                                                             │
                                                             ▼
                                                  E5 Documentation & final summary
```

Critical path: URL-1 → URL-3 → URL-4 → URL-5 → URL-10 → URL-11 → URL-14 → URL-20.

---

## E0 — Foundations

| ID | Story | Depends on | AC | AI plan |
|---|---|---|---|---|
| URL-1 | Solution skeleton: `Api`, `Application`, `Domain`, `Infrastructure`, test projects (`AnalyticsWorker` deferred to URL-11, where it is introduced) | — | `dotnet build` passes with `TreatWarningsAsErrors`; project references enforce layer direction (Domain has no dependencies) | AI scaffolds; engineer verifies the dependency direction |
| URL-2 | AI governance: `CLAUDE.md`, `copilot-instructions.md`, AI traceability log, PR template with AI disclosure | — | Files exist and are referenced from README | AI drafts; engineer edits rules |

## E1 — Greenfield: core service (Scenario 1)

| ID | Story | Depends on | AC | AI plan |
|---|---|---|---|---|
| URL-3 🔒 | Domain model: `ShortLink` aggregate, `ShortCode` value object, `ICodeGenerator` (CSPRNG Base62) | URL-1 | Invariants unit-tested: code charset/length, alias rules, expiry logic | AI drafts + tests; engineer reviews the randomness source (must be `RandomNumberGenerator`, not `Random`) |
| URL-4 🔒 | URL safety policy (`UrlPolicy`) | URL-3 | Rejects non-http(s), credentials, private/loopback IPs, `localhost`, self-domain, > 2048 chars; table-driven tests | AI generates attack-case table; engineer adds cases AI missed |
| URL-5 🔒 | Persistence: EF Core `DbContext`, unique index on code, migrations, repository; SQLite + PostgreSQL providers | URL-3 | Migration applies on both providers; unique index exists | AI drafts; engineer reviews schema and indexes |
| URL-6 | Create / Get / Delete / Redirect endpoints + ProblemDetails errors + OpenAPI | URL-4, URL-5 | All FR-1..FR-4, FR-9 covered by integration tests (`WebApplicationFactory`) | AI drafts endpoints + tests; engineer checks status codes against spec |
| URL-7 | API-key auth on management endpoints | URL-6 | 401 without key, 200 with key; redirect remains public | Engineer-led; AI writes tests |
| URL-8 | Idempotency-Key on create | URL-6 | Same key + same body → same response; same key + different body → 422 | AI drafts; engineer reviews race handling |
| URL-9 | **Initial (naive) analytics:** click row written synchronously in the redirect | URL-6 | Stats endpoint returns counts | Deliberately simple: the "legacy" baseline for Scenario 2 |

## E2 — Brownfield: modernization (Scenario 2)

| ID | Story | Depends on | AC | AI plan |
|---|---|---|---|---|
| URL-10 | Impact analysis: map redirect data flow, identify coupling, measure baseline latency | URL-9 | Written analysis + baseline load-test numbers | AI analyses code; engineer validates findings |
| URL-11 🔒 | Event-driven analytics: `LinkClicked` event → transactional outbox → Kafka → Analytics Worker → aggregates. In-process channel fallback for zero-setup mode | URL-10 | Redirect no longer writes click rows synchronously; worker is idempotent by event ID; stats still correct (eventually) | AI drafts producer/consumer; engineer reviews delivery semantics (at-least-once + idempotent consumer) |
| URL-12 | Cache-aside for redirect lookups (`IDistributedCache`, Redis in full mode) with invalidation on delete | URL-10 | Cache hit avoids DB; delete invalidates cache; before/after load-test numbers | AI drafts; engineer reviews invalidation and TTL vs expiry |
| URL-13 | **Bug fix:** a real defect found during brownfield work, fixed test-first (see note) | URL-6 | Failing regression test first, then fix | AI reproduces; engineer confirms root cause |

> **Note on URL-13 (changed during execution).** The concurrent-alias race was originally planned as the
> brownfield bug. During greenfield work it was prevented by design (DB unique index, no check-then-insert)
> and proven by an integration test with 20 parallel requests. Seeding an artificial bug to "find" later
> would misrepresent the work, so URL-13 now covers a genuine defect discovered during the brownfield changes.

## E3 — Ambiguous: "make links safe and reliable" (Scenario 3)

| ID | Story | Depends on | AC | AI plan |
|---|---|---|---|---|
| URL-14 | Interpretation document: options, chosen scope, explicitly rejected scope | URL-11 | Signed-off decision record | AI enumerates interpretations; engineer decides |
| URL-15 | Rate limiting (per-client, separate policies for create vs redirect) | URL-14 | 429 + `Retry-After` beyond limit; tested | AI drafts |
| URL-16 | Resilience: Polly retry + circuit breaker around cache and Kafka; degrade gracefully | URL-14 | Tests with failing fakes prove redirect still works | AI drafts; engineer reviews timeouts and budgets |
| URL-17 | Expiry enforcement + 410; cache TTL never exceeds link expiry | URL-14 | Tests at expiry boundary | AI drafts edge-case tests |
| URL-18 | Observability: Serilog JSON, correlation ID, OpenTelemetry, health live/ready; `ForwardedHeaders` with explicit trusted proxies (real client IP for visitor hashing) | URL-14 | Logs are JSON with correlation ID; readiness fails when DB is down; unique visitors correct behind the compose/K8s proxy; spoofed `X-Forwarded-For` from untrusted sources ignored | AI drafts; engineer reviews trusted-proxy list |

## E4 — Delivery

| ID | Story | Depends on | AC | AI plan |
|---|---|---|---|---|
| URL-19 | Dockerfiles (non-root, multi-stage) + docker-compose (API ×2, Worker, Postgres, Redis, Kafka) | URL-11 | `docker compose up` works end-to-end | AI drafts; engineer reviews image hardening |
| URL-20 | GitHub Actions CI: build, format, analyzers, tests, coverage, vuln scan | URL-6 | Pipeline green; fails on any gate | AI drafts |
| URL-21 | K8s manifests: Deployment, Service, HPA, probes, ConfigMap/Secret | URL-19 | `kubectl apply --dry-run=client` validates | AI drafts |
| URL-22 | Load test script + results | URL-12 | Report with before/after numbers | AI drafts script; engineer interprets results |

## E5 — Documentation & final summary

| ID | Story | Depends on | AC |
|---|---|---|---|
| URL-23 | Architecture overview + ADRs | URL-11 | Diagram + decisions |
| URL-24 | Scenario write-ups (×3) | URL-13, URL-18 | Each shows decomposition → execution → validation |
| URL-25 | Final engineering summary | all | Plan, artifacts, risks, trade-offs, assumptions, limitations, AI traceability |
