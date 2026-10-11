# 01 — Requirement Understanding & Normalization

| Field | Value |
|---|---|
| Status | **APPROVED by engineer (Rakz3438), 2026-10-11** |
| Owner | Engineer (candidate) |
| AI role | Drafted by Claude Code from the assignment brief + JD; reviewed/edited by engineer |
| Last updated | 2026-10-11 |

---

## 1. Raw requirement (as received)

> "Build a URL shortener service from scratch with core APIs, analytics, and reliability features."

This is one sentence. Everything below is the engineering interpretation of it. Every
interpretation that was a *choice* (not a fact) is recorded in §5 with its rationale so it can be
challenged.

## 2. Normalized problem statement

Provide an HTTP service that:

1. Accepts a long URL and returns a short, unique, unguessable code.
2. Redirects `GET /{code}` to the original URL **fast and reliably**, even when non-critical
   dependencies (cache, event bus, analytics) are degraded.
3. Records every redirect as an analytics event **without slowing down the redirect**, and exposes
   aggregated statistics per link.
4. Is safe to expose publicly: it must not become an open-redirect / phishing / SSRF vector and
   must resist abuse (rate limiting, input validation).
5. Is operable in production: configurable via environment (12-factor), observable (structured
   logs, traces, metrics, health checks), containerized, horizontally scalable.

**Primary quality attribute:** redirect latency and availability. A shortener's read:write ratio
is typically 100:1 or more, so the design optimizes the read (redirect) path above all else.

## 3. Stakeholders & actors

| Actor | Needs |
|---|---|
| Link creator (API client) | Create, inspect, delete links; read analytics |
| End user (clicker) | Fast, correct redirect; not sent to a malicious destination |
| Operator / SRE | Health, metrics, logs, safe deploys, graceful degradation |
| Security reviewer | No open redirect abuse, input validated, secrets not in code |

## 4. Functional requirements

| ID | Requirement | Priority |
|---|---|---|
| FR-1 | `POST /api/v1/links` creates a short link from `{ url, customAlias?, expiresAt? }` and returns `201` with code, short URL, metadata | Must |
| FR-2 | `GET /{code}` redirects to the target URL | Must |
| FR-3 | `GET /api/v1/links/{code}` returns link metadata | Must |
| FR-4 | `DELETE /api/v1/links/{code}` deactivates a link (soft delete) | Must |
| FR-5 | Optional custom alias, unique, validated against a charset and a reserved-word list | Should |
| FR-6 | Optional expiry; expired links stop redirecting | Should |
| FR-7 | `GET /api/v1/links/{code}/stats` returns total clicks, clicks per day, top referrers, top user-agent families | Must |
| FR-8 | `Idempotency-Key` header on create, so client retries do not create duplicate links | Should |
| FR-9 | OpenAPI (Swagger) definition published by the service | Must |

## 5. Ambiguities identified → decisions taken

Each row is an ambiguity in the brief, the options considered, the decision and why.
Rows marked ⚠️ are **high-impact** and need explicit engineer sign-off.

| # | Ambiguity | Options considered | Decision | Rationale |
|---|---|---|---|---|
| A-1 ⚠️ | How are short codes generated? | (a) Hash of URL (MD5/SHA → truncate) (b) DB auto-increment → Base62 (c) Random Base62 from a CSPRNG | **(c) 7-char random Base62, CSPRNG, retry on unique-constraint collision** | (a) has collisions, and the same URL always gives the same code, which leaks that it was shortened before. (b) makes codes sequential and enumerable (privacy issue) and needs a central counter (scaling bottleneck). (c) 62⁷ ≈ 3.5×10¹² keyspace, unguessable, stateless across instances; the DB unique index is the final arbiter. |
| A-2 ⚠️ | Which redirect status code? | 301 permanent / 302 found / 307 temporary | **302** | 301 is cached by browsers, so repeat clicks never reach us and analytics silently undercount, and deleted/expired links keep working in browsers' caches. 302 keeps us authoritative. Cost: slightly more traffic, absorbed by the cache. |
| A-3 | Same long URL submitted twice: same code or new code? | Dedupe by URL / always new | **Always new code; dedupe only via `Idempotency-Key`** | Different campaigns legitimately shorten the same URL and need separate analytics. Retries are the real duplicate risk, and idempotency keys solve that precisely. |
| A-4 ⚠️ | Who may create/delete links? Brief mentions no users. | Full user accounts + OAuth / API key / open | **API key (`X-Api-Key`) on management endpoints; redirect is public** | User management is out of scope for the brief, but an unauthenticated delete endpoint is unacceptable. An API key is the minimum safe control and is replaceable by OAuth/JWT later behind the same authorization policy. |
| A-5 | What does "analytics" mean? | Count only / count + time series + dimensions / full clickstream | **Total, per-day series, top referrers, top UA families** | Covers the common product questions without building a BI tool. Raw events are kept for re-aggregation. |
| A-6 ⚠️ | Privacy of click data | Store raw IP / hash IP / don't store | **Do not store raw IP. Store an HMAC-SHA256 (keyed with a secret salt) of IP + user-agent, truncated to 128 bits, for approximate unique visitors; plus the coarse UA family only** *(amended 2026-10-11 from "salted SHA-256 of IP", engineer-approved)* | Raw IP is personal data (GDPR). The hash supports uniqueness counting without retaining the identifier. |
| A-7 | How fresh must analytics be? | Strongly consistent / eventually consistent | **Eventually consistent (target < 5 s)** | Writing analytics synchronously in the redirect couples redirect latency and availability to the analytics store. Event-driven processing (Kafka) decouples them. This is the brownfield modernization in Scenario 2. |
| A-8 ⚠️ | Which destinations are allowed? | Anything / http(s) only / http(s) + block internal targets | **http/https only, max 2048 chars, no embedded credentials, block loopback/private/link-local IP literals and `localhost`, block self-referencing (our own domain)** | Prevents `javascript:`/`data:` XSS vectors, redirect loops, and use of the service to reach internal hosts. Limitation: no DNS-resolution check at create time (DNS rebinding makes it unreliable anyway); documented as a residual risk. |
| A-9 | Expired vs deleted vs unknown: which response? | All 404 / differentiated | **Unknown or deleted → 404; expired → 410 Gone** | 410 is semantically correct and helps clients. Deleted returns 404 so a deactivated link reveals nothing about what it pointed to. |
| A-10 | "Reliability features": which ones? | Open-ended | **Graceful degradation (cache/bus down → redirect still works), retries + circuit breaker, rate limiting, health/readiness probes, idempotent create, transactional outbox for events** | Chosen against concrete failure modes in §8. Details in Scenario 3 (ambiguous). |
| A-11 | Scale target? Not stated. | — | **Design for horizontal scale; demonstrate, don't benchmark at internet scale** | Stateless API instances + shared cache + partitioned Kafka topic. A local load test gives a baseline and before/after comparison, not a capacity claim. |

## 6. Non-functional requirements

| ID | Category | Requirement | How verified |
|---|---|---|---|
| NFR-1 | Performance | Redirect p95 < 20 ms on cache hit, local environment | Load test (k6/NBomber), before/after caching |
| NFR-2 | Availability | Redirect works when Redis **or** Kafka is unavailable | Integration tests with failing fakes + manual chaos test in docker-compose |
| NFR-3 | Scalability | API is stateless; any instance can serve any request | Design review; 2 replicas in compose/K8s |
| NFR-4 | Security | Input validation, API-key auth, rate limiting, no secrets in source, dependency scan | Tests + `dotnet list package --vulnerable` in CI |
| NFR-5 | Observability | JSON structured logs (Splunk-ready), correlation ID, OpenTelemetry traces + metrics, `/health/live` and `/health/ready` | Manual check + tests for health endpoints |
| NFR-6 | Operability | 12-factor config; zero-setup local mode **and** full infra mode, switched by config only | Run both modes end-to-end |
| NFR-7 | Maintainability | Clean Architecture layers, analyzers with warnings as errors, ≥ 80% line coverage on Domain + Application | CI gates |

## 7. Out of scope (explicit)

- User accounts, UI/front-end, billing, multi-tenancy.
- QR codes, link editing (change target after creation: deliberately excluded because it enables
  "bait-and-switch" phishing).
- Malware/phishing reputation lookups (e.g., Google Safe Browsing). Noted as a future extension point.
- Real cloud deployment. K8s manifests are provided and validated, not deployed to GCP.

## 8. Failure scenarios considered up front

| Failure | Expected behavior |
|---|---|
| Redis down | Cache bypassed (circuit breaker), redirect served from DB, latency rises, no errors |
| Kafka down | Redirect unaffected. Clicks wait in the producer's buffer and are delivered when Kafka returns within the retry window (default 5 min); beyond that the oldest are dropped and counted in a metric *(amended 2026-10-11: the DB outbox was rejected for the redirect path, see scenarios/02-brownfield.md)* |
| DB down | Cached links still redirect; uncached → 503 with `Retry-After`; readiness probe fails so the load balancer drains the instance |
| Code collision | Unique index violation → retry with new code (max 5), then 500 + alert-worthy log |
| Concurrent identical custom alias | Exactly one succeeds (201), the other gets 409 Conflict. The DB constraint decides, not an application-level check |
| Abuse / scraping | Per-client rate limits → 429 with `Retry-After` |
| Duplicate event delivery (Kafka at-least-once) | Consumer is idempotent by event ID |

## 9. Acceptance criteria (definition of done for the whole system)

1. `dotnet run` starts the API with zero external dependencies; a link can be created, followed and
   its stats viewed within one minute of cloning.
2. `docker compose up` runs API + Analytics Worker + PostgreSQL + Redis + Kafka, switched purely by
   environment variables.
3. All FRs have at least one automated test; all failure scenarios in §8 that can be automated are.
4. CI pipeline runs build → format check → analyzers → tests + coverage → vulnerability scan.
5. Each of the three scenarios (greenfield / brownfield / ambiguous) has a written record of
   decomposition, AI-assisted execution (with accepted/edited/rejected log) and validation.

## 10. Sign-off

| Item | Decision | Engineer approval |
|---|---|---|
| A-1 Code generation | Random Base62, 7 chars | ✅ Approved 2026-10-11 |
| A-2 Redirect status | 302 | ✅ Approved 2026-10-11 |
| A-4 Auth model | API key | ✅ Approved 2026-10-11 |
| A-6 Privacy | Hashed IP, no raw IP | ✅ Approved 2026-10-11 |
| A-8 URL safety policy | As specified | ✅ Approved 2026-10-11 |
