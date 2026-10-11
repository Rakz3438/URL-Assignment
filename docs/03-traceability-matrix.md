# 03 — Requirements Traceability Matrix

Every requirement and decision in [01-requirements.md](01-requirements.md) mapped to its implementation,
its verifying test, and its status. Audited line by line against the code (not from memory) on 2026-10-11,
at the end of the greenfield scenario.

**Legend:** ✅ Done and verified · 🟡 Partial or deviates (see notes) · ⏳ Planned (story shown) · ❌ Gap, not yet planned

## Functional requirements

| ID | Requirement | Implementation | Verified by | Status |
|---|---|---|---|---|
| FR-1 | Create link → 201 | `LinkEndpoints.CreateAsync`, `LinkService.CreateAsync` | `LinkApiTests.Create_ValidUrl_Returns201WithLocationAndShortUrl` | ✅ |
| FR-2 | Redirect | `RedirectEndpoints`, `RedirectService` | `RedirectAndStatsTests.Redirect_ActiveLink_Returns302NoStoreWithoutApiKey` | ✅ |
| FR-3 | Get metadata | `LinkEndpoints.GetAsync` | `LinkApiTests.Get_ExistingLink_Returns200` | ✅ |
| FR-4 | Soft delete | `ShortLink.Delete`, `LinkService.DeleteAsync` | `LinkApiTests.Delete_ExistingLink_Returns204AndLinkStopsResolving` | ✅ |
| FR-5 | Custom alias: unique, charset, reserved words | `ShortCode.IsValidAlias`, unique index | `ShortCodeTests`, `Create_DuplicateAlias_Returns409`, `Create_MixedCaseAlias_IsStoredLowercaseAndFollowable` | ✅ Note 1 resolved |
| FR-6 | Expiry stops redirects | `ShortLink.GetStatus` | `Redirect_AfterExpiry_Returns410`, `GetStatus_AtExactExpiry_IsExpired` | ✅ |
| FR-7 | Stats: total, per day, referrers, UA families | `StatsService`, `DbLinkStatsReader` | `Stats_AfterClicks_ReturnsTotalsReferrersAgentsAndDailySeries`, `UniqueVisitors_BehindProxy_*` | ✅ Note 2 resolved in URL-18 |
| FR-8 | Idempotency-Key | Unique `IdempotencyKey` column, `LinkService.TryReplayAsync` | `Create_RetryWithIdempotencyKey_ReplaysIdentical201WithReplayHeader`, `CreateAsync_ConcurrentRequestsSameKey_*` | ✅ Note 3 resolved |
| FR-9 | OpenAPI | Swashbuckle, `/swagger/v1/swagger.json` | `OpenApi_Document_IsPublished` | ✅ |

## Decisions

| ID | Decision | Status | Evidence / note |
|---|---|---|---|
| A-1 | 7-char CSPRNG Base62, DB decides uniqueness | ✅ | `RandomShortCodeGenerator`; 100k-code uniqueness test; reserved-word redraw (found in this audit, fixed) |
| A-2 | 302 | ✅ | Plus `Cache-Control: no-store`; asserted in test |
| A-3 | No URL dedupe | ✅ | `CreateAsync_SameUrlTwiceWithoutKey_CreatesTwoLinks` |
| A-4 | API key on management, public redirect | ✅ | Fixed-time compare; 401 tests incl. wrong key; startup fails without a 16+ char key |
| A-5 | Analytics scope | ✅ | As FR-7 |
| A-6 | No raw IP; keyed hash | ✅ | Note 4 resolved: A-6 amended and re-signed |
| A-7 | Eventually consistent analytics | ✅ | Kafka pipeline; worker caught up within 10–18 s after 25k req/s bursts; integration tests wait with a deadline |
| A-8 | URL safety policy | ✅ | 36 test cases in `UrlPolicyTests` + 3 at HTTP level. Residual risk: no DNS resolution (documented) |
| A-9 | 404 unknown/deleted, 410 expired | ✅ | Tests for all three |
| A-10 | Reliability features | ✅ | Idempotent create, graceful degradation, retries, circuit breaker, rate limiting, health probes: all built and chaos-tested (Scenario 3) |
| A-11 | Horizontal scale | ⏳ URL-19/21 | Stateless in Postgres mode; SQLite mode is single-instance by nature |

## Non-functional requirements

| ID | Requirement | Status | Note |
|---|---|---|---|
| NFR-1 | Redirect p95 < 20 ms on cache hit | ✅ | **p95 3.10 ms** at 24.8k req/s (baseline 9.64 ms). Local-only measurement |
| NFR-2 | Redirect survives Redis/Kafka outage | ✅ | Chaos tests: Kafka down, 0 errors and 0 clicks lost (after fix); Redis down, 0 errors. Redis log flood → URL-16 |
| NFR-3 | Stateless, any instance serves any request | 🟡 | True with PostgreSQL. `MigrateOnStartup` with several replicas could race: run migrations as a pre-deploy job (URL-21) |
| NFR-4 | Validation, API key, rate limiting, no secrets in source, dependency scan | ✅ | Rate limiting ✅ (S1); blocklist + security headers added (S2/S4). Dependency scan ✅ (caught and fixed CVE-2025-6965). Dev-only key lives in `appsettings.Development.json`; production startup refuses to run without configured keys and salt |
| NFR-5 | JSON logs, correlation ID, OTel, health endpoints | ✅ | All built (R2/R3). EF false-error issue fixed and regression-tested (0 Error logs for expected 409s) |
| NFR-6 | Zero-setup mode **and** full-infra mode | 🟡 | Zero-setup ✅. **PostgreSQL mode ✅ verified 2026-10-11** against PostgreSQL 16.15 in Docker, switched by environment variables only: migration applied; unique indexes present; all endpoints, 20-way alias race (1×201, 19×409), idempotent replay, expiry stored as UTC, stats and delete behave as on SQLite. Full compose stack (Redis, Kafka, worker) ⏳ URL-19 |
| NFR-7 | Layers, analyzers, ≥ 80% coverage | ✅ | Architecture tests; warnings as errors; coverage **enforced** in the gate (added in this audit). Domain 92.0%, Application 97.9% |

## Failure scenarios (§8)

| Failure | Status |
|---|---|
| Code collision → retry ×5, then 500 | ✅ `CreateAsync_GeneratedCodeCollides_*`, `CreateAsync_AllAttemptsCollide_*` |
| Concurrent identical alias → one 201, rest 409 | ✅ 20 parallel requests over HTTP + real SQLite |
| Redis down / Kafka down | ✅ Chaos tests 2–3 + integration tests | 
| DB down | ✅ Chaos test + `ResilienceTests`: cached 302, uncached 503 + Retry-After; 0 clicks lost |
| Abuse → 429 | ✅ `SafetyTests` |
| Duplicate event delivery | ✅ `EfClickIngestorTests` + chaos test 4 (worker SIGKILL): raw = aggregates |

## Notes (deviations, with the engineer's decisions)

1. **Alias uniqueness is case-sensitive.** `Promo` and `promo` can both exist and point to different places,
   which helps lookalike phishing. Options: (a) keep and document; (b) lowercase aliases on create;
   (c) case-insensitive unique index on aliases only. **Decision: (b) lowercase on create. Implemented with unit + HTTP tests.**
2. **Unique visitors is always 0 behind a proxy/load balancer and untested end-to-end.** The visitor hash
   uses `Connection.RemoteIpAddress`, which is the proxy's IP in Docker/K8s and is null in the test host.
   Fix: `ForwardedHeaders` middleware with an explicit trusted-proxy list (never trust the header blindly,
   it's client-spoofable). **Decision: fix in URL-18, where it can be tested behind a real proxy.**
3. **Idempotent replay returns 200, not the original 201.** The acceptance criterion says "same response".
   200 lets a client tell a replay apart, but differs from Stripe-style APIs that replay the identical status.
   **Decision: replay identical 201 + `Idempotent-Replayed: true`. Implemented and tested.**
4. **Visitor hash is HMAC-SHA256 of IP + user-agent, truncated to 128 bits.** The signed-off text says
   "salted SHA-256 hash of IP". Including the UA separates different devices behind one NAT, but counts
   one person on two browsers twice. HMAC is the correct construction for a keyed hash. **Decision: keep;
   A-6 amended and re-signed.**

## Not yet started (by plan)

Brownfield (URL-10…13), ambiguous scenario (URL-14…18), delivery (URL-19…22: Docker, CI, K8s, load test),
documentation (URL-23…25: architecture overview, scenario write-ups, final summary).
