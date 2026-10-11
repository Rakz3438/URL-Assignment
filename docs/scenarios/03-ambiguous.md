# Scenario 3 — Ambiguous requirement: "Make the links safe and reliable"

| Field | Value |
|---|---|
| Stories | URL-14 (interpretation), URL-15…18 (implementation) |
| Status | URL-14 ✅ scope decided. URL-15/16/18 in progress |

## 1. Why this is ambiguous

The request names two qualities but no behavior, no threat, no target and no acceptance test. "Safe" could mean
safe *for the people clicking*, safe *for the service*, or safe *for the business*. "Reliable" could mean the
redirect stays up, the destination stays up, or the data stays correct. Each reading leads to different work,
and some readings conflict with each other or with existing decisions.

**Method.** (1) Enumerate interpretations. (2) Tie each to a concrete failure we can name, ideally one already
observed. (3) Estimate value, cost and risk. (4) Pick a scope, and write down what was deliberately left out so
the gap is a decision, not an accident.

## 2. Interpretations

### "Safe"

| ID | Interpretation | Concrete failure it prevents | Evidence / status today | Cost | Recommendation |
|---|---|---|---|---|---|
| S1 | **Abuse protection: rate limiting** | One client floods create or scans random codes, exhausting the DB or filling the keyspace | None today: an unlimited client hit 25k req/s in our own load test | Low (built into ASP.NET Core) | ✅ **In scope (URL-15)** |
| S2 | **Block known-bad destinations** with a configurable domain blocklist | Service used to launder phishing links behind our trusted domain | URL policy blocks schemes, private IPs and loops (A-8) but not bad *public* domains | Low–Medium | ✅ **In scope, local blocklist only** |
| S2+ | External reputation lookup (Google Safe Browsing etc.) | Same, with far better coverage | — | Medium: third-party dependency, API key, sends every URL to a third party (privacy), latency on create | ❌ **Out of scope**: designed as an extension point behind the same interface |
| S3 | Interstitial "you are leaving…" warning page | User surprise at destination | — | Medium; changes product UX, breaks the 302 contract (A-2) | ❌ **Out**: product decision, not engineering |
| S4 | **HTTP security headers** (HSTS, nosniff, frame-deny, referrer policy) | Clickjacking/MIME sniffing on our error pages; referrer leakage | None set today | Very low | ✅ **In scope** |
| S5 | Resist code enumeration | Guessing private links | Already mitigated: random 7-char Base62 (A-1); S1 adds rate limits | — | Already done |

### "Reliable"

| ID | Interpretation | Concrete failure it prevents | Evidence / status today | Cost | Recommendation |
|---|---|---|---|---|---|
| R1 | **Graceful degradation with circuit breakers** | Redis outage floods logs and adds up to 100 ms per request; DB outage turns every redirect into a 500 | **Observed:** 2,500 warnings in 10 s during the Redis chaos test. DB down: untested, expected 500s | Medium | ✅ **In scope (URL-16)**: Redis breaker; DB down → cached links keep working, uncached → 503 + `Retry-After` |
| R2 | **Health and readiness probes** | Load balancer keeps sending traffic to an instance that can't reach its DB; K8s can't tell live from ready | None today | Low | ✅ **In scope (URL-18)**: `/health/live` (process), `/health/ready` (DB; Redis/Kafka reported as degraded, not failing) |
| R3 | **Observability** | Can't see failures in Splunk; false alerts | **Observed:** EF logs 40 Errors per 20 expected 409s; no correlation IDs; metrics only via `dotnet-counters`; unique visitors wrong behind proxies | Medium | ✅ **In scope (URL-18)**: JSON logs, correlation ID, OpenTelemetry metrics/traces, EF noise fix, trusted-proxy client IP |
| R4 | Expiry correctness | Expired link keeps working | Done (FR-6, cache TTL ≤ expiry) | — | Already done |
| R5 | Check destinations are alive (periodic crawl, mark dead links) | Users land on 404s at the destination | — | High; the service would make outbound requests to arbitrary URLs: **a real SSRF surface**, plus crawling etiquette and scale | ❌ **Out**: risk outweighs value; contradicts A-8's threat model |
| R6 | Multi-region / disaster recovery | Region outage | — | Very high; infra, not code | ❌ **Out**: documented as a deployment concern |

## 3. Proposed scope and acceptance criteria

| Story | Scope | Acceptance criteria |
|---|---|---|
| URL-15 | Rate limiting (S1) | Create: 30/min per API key. Redirect: 300/min per client IP (sliding window). Exceeding → **429 + `Retry-After`** as ProblemDetails. Limits configurable by env. Integration-tested |
| URL-15b | Destination blocklist (S2) | Domain and all its subdomains blocked (`evil.example` blocks `a.evil.example`). Loaded from config. Checked on create **and** on redirect, so blocklisting an existing link's domain stops it without a DB change. 400 on create / 410 on redirect |
| URL-15c | Security headers (S4) | Every response carries `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, `Strict-Transport-Security` (when HTTPS) |
| URL-16 | Circuit breaker + DB-down behavior (R1) | Redis: after N consecutive failures, skip Redis for a cool-down. Redis chaos test logs ≤ a handful of lines and p99 stays near normal. DB down: cached links 302, uncached 503 + `Retry-After`; never a raw 500 |
| URL-18 | Health, observability, proxy IPs (R2, R3) | `/health/live`, `/health/ready`; JSON logs with correlation ID; OTel metrics (incl. click pipeline) + traces; expected 409s no longer logged as Error; client IP from `X-Forwarded-For` **only** via configured trusted proxies |

## 4. Explicitly out of scope (and why)

| Item | Why not now | What would change the decision |
|---|---|---|
| External reputation API (S2+) | Third-party dependency, privacy, latency | Abuse reports at real traffic; legal/privacy sign-off |
| Interstitial page (S3) | Product/UX decision; breaks 302 contract | Product owner request |
| Destination liveness crawl (R5) | Creates an SSRF surface; scale/etiquette | A dedicated, sandboxed crawler service |
| Multi-region (R6) | Infrastructure, not application code | Availability SLO above single-region |

## 5. Decision

**Approved by Rakz3438, 2026-10-11: S1, S2, S4, R1, R2, R3 in scope; S2+, S3, R5, R6 out**, as recommended.

Engineer's question before deciding: *"which one is best according to the assignment document?"* The answer mapped
each option to the assignment's evaluation criteria and the JD ("resiliency patterns, observability patterns,
Splunk"). R1 and R3 were prioritized because they fix failures already measured in Scenario 2. R5 was rejected as
a security risk disguised as a reliability feature (SSRF). S2+ is designed for (the blocklist sits behind the URL policy
so a reputation check can plug in later) but not built. Trim order if time runs short: S4 last, R1/R3 never.
