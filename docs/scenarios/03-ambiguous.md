# Scenario 3 — Ambiguous requirement: "Make the links safe and reliable"

| Field | Value |
|---|---|
| Stories | URL-14 (interpretation), URL-15…18 (implementation) |
| Status | ✅ Complete. Engineer sign-off: Rakz3438, 2026-10-11 |

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

---

## 6. Execution and validation

### What was built

| Scope | Implementation | Verified by |
|---|---|---|
| S1 Rate limiting | ASP.NET Core rate limiter (no new dependency). Create: fixed window per API-key fingerprint. Redirect: sliding window per client IP. 429 + `Retry-After` ProblemDetails. `RateLimiting__*` env | `SafetyTests` (per-key, per-IP) |
| S2 Blocklist | `DomainBlocklist` (domain + subdomains, punycode-normalized, label-boundary match, hot-reloadable). Checked on create (400 `url.blocked`) **and** redirect (410 `link.blocked`) | 6 unit cases incl. lookalikes; HTTP test blocks an *existing* link |
| S4 Headers | nosniff, frame DENY, no-referrer on every response; HSTS outside Development | `SafetyTests` on 3 response types |
| R1 Resilience | Polly circuit breaker on Redis (2 s window, 15 s break); `DependencyUnavailableException` → **503 + Retry-After** via middleware with throttled logging; Npgsql fail-fast timeout (3 s) | Unit: breaker opens/half-opens; unreachable DB → typed outage. HTTP: DB down → cached 302, uncached 503. Chaos tests below |
| R2 Health | `/health/live` (no deps), `/health/ready` (database, cache breaker, click buffer) JSON, no exception details | `ObservabilityTests` incl. DB-unreachable in both policy modes |
| R3 Observability | JSON logs (stdout, Splunk-ready) with CorrelationId + TraceId/SpanId scopes; `X-Correlation-ID` validated (no log injection); OpenTelemetry metrics + traces (+ OTLP logs) when `OTEL_EXPORTER_OTLP_ENDPOINT` is set; trace sampling (100% dev / 10% default); EF expected-failure logs demoted; trusted-proxy client IP; Aspire dashboard in compose | `ObservabilityTests`: correlation echo/replace, trusted vs spoofed XFF, **zero Error logs for expected 409s** |

### Chaos results on real infrastructure

| Test | Result |
|---|---|
| Redis down 10 s | **0 errors**, p99 11.5 ms. **3 log lines** for the whole outage (Scenario 2: 2,500). Breaker opened after 1.9 s, closed on recovery; readiness Degraded during, Healthy after |
| PostgreSQL down 10 s | Cached link: **0 errors, 24.2k req/s, p99 5.0 ms**. Uncached: **503 in 44 ms** + `Retry-After: 5`. Create: 503. Readiness: 200 Degraded. **604,680 / 604,680 clicks** ingested after recovery (Kafka buffered, worker retried). 9 Error lines total |

Raw outputs: [perf/03-scenario3.md](../perf/03-scenario3.md).

### Defects found by verification

1. **Spoofable client IP (security).** With no trusted proxies configured, a forged `X-Forwarded-For` was believed:
   ASP.NET Core treats *empty* trusted lists as "trust everyone". Caught by the untrusted-proxy test (3 visitors
   instead of 1). Fix: forwarding disabled unless proxies are configured. Impact if missed: per-IP rate limits
   bypassable, analytics forgeable.
2. **Circuit breaker mis-tuned (made things worse).** First version used a 10 s sampling window. Under load, ~250k
   pre-outage successes kept the failure ratio under 50% for **9 s** while each request paid 2×100 ms timeouts:
   throughput fell to 6.1k req/s and p99 rose to 205 ms, *worse than no breaker*. Found by reading the log timeline,
   fixed with a 2 s window, re-measured.
3. **Log flood persisted pre-open** (850 lines in 1.8 s). Fixed with a log throttle (one line per 10 s + suppressed
   count), now 3 lines per outage.

### Measured cost of observability (trade-off)

| Configuration | Throughput | p99 |
|---|---|---|
| Brownfield, no Scenario 3 middleware | 24.8k req/s | 5.0 ms |
| Scenario 3, telemetry not exported | 21.6k req/s (−13%) | 6.2 ms |
| Scenario 3, 100% traces exported | 18.8k req/s (−24%) | 8.2 ms |

Hence trace sampling defaults to 10% outside Development.

### Accepted limitations

- Rate limits are per instance (N replicas → up to N× the configured limit). Upgrade path: Redis-backed limiter.
- The Analytics Worker has no HTTP health endpoint (K8s would use an exec/file-based probe); covered in URL-21.
- After Redis recovers, the cache stays bypassed until the 15 s break ends (seen as "circuit CLOSED" 15 s later).

## 7. Decision: readiness during a database outage (resolved)

**Readiness during a database outage.** Requirement §8 says readiness should *fail* so the load balancer drains the
instance. Implemented default: **Degraded (200)**, because the database is shared; failing every replica at once
removes all of them and turns "cached links keep working" into a total outage. Verified both ways in tests;
switch with `Health__FailReadinessOnDatabaseOutage=true`. **Approved by Rakz3438 (2026-10-11): Degraded; §8 amended.**
