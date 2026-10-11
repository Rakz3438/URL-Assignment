# Scenario 2 — Brownfield: modernize analytics and the redirect hot path

| Field | Value |
|---|---|
| Stories | URL-10 (impact analysis), URL-11 🔒 (event-driven analytics), URL-12 (caching), URL-13 (bug fix) |
| Starting point | The greenfield service as committed (`61fbe4a`), treated as an existing production system |
| Status | ✅ Complete. Engineer sign-off: Rakz3438, 2026-10-11 |

---

## URL-10 — Impact analysis

### 1. Current redirect data flow (as built)

```
GET /{code}
  └─ RedirectEndpoints.RedirectAsync                       (Api)
       └─ RedirectService.ResolveAsync                     (Application)
            ├─ IShortLinkRepository.GetByCodeAsync  ──►  SELECT short_links WHERE Code = @code    [DB read]
            ├─ link.GetStatus(now)                         404 / 410 decided here
            ├─ ClickEventFactory.Create                    hash visitor, classify UA, referrer host
            └─ IClickRecorder.RecordAsync  ──────────►  INSERT click_events; SaveChanges           [DB write, awaited]
       └─ 302 Location: target
```

Stats path: `StatsService` → `DbLinkStatsReader` → five queries over raw `click_events` rows in the window
(count, distinct visitors, top referrers, top UA, timestamps bucketed in memory). Cost grows with clicks.

### 2. Coupling and problems found (with evidence)

| # | Problem | Evidence |
|---|---|---|
| P1 | **Redirect latency includes an analytics write.** Every click is a DB round-trip on the user's critical path | Code: `RedirectService` awaits `RecordAsync`. Baseline: 1 SELECT + 1 INSERT per redirect (247,840 rows written in the test) |
| P2 | **Analytics contention stalls redirects.** A lock on only `click_events` froze redirects for its full duration | Load test, 10 s lock: throughput **7,428 → 2,404 req/s (−68%)**, max latency **10.03 s** ([perf/01-baseline.md](../perf/01-baseline.md)) |
| P3 | **Analytics failure fails the redirect (500).** Violates the CLAUDE.md non-negotiable rule | Failing-first test `AnalyticsDecouplingTests.Redirect_AnalyticsStoreFailing_StillRedirects`: expected 302, got **500**. This is the real defect for URL-13 |
| P4 | **Every redirect reads the DB**, even for the same hot link | No cache. Hot links (a viral campaign) hammer one row |
| P5 | **Stats cost is O(clicks in window)** and shares the transactional database with redirects | `DbLinkStatsReader` scans raw rows; heavy stats queries compete with redirect reads |

### 3. Impacted components

| Component | Change | Risk |
|---|---|---|
| `RedirectService` (Application) | Stop awaiting analytics; hand off to a non-blocking sink; read through a cache | High: hot path. Covered by existing redirect tests + new decoupling test |
| `IClickRecorder` port | Replaced by a non-blocking `IClickSink` (enqueue, never throws) | Medium: interface change, all implementations and fakes |
| New: publisher (Api host) | Background service draining the in-memory buffer to Kafka (full mode) or DB (zero-setup mode) | Medium: delivery semantics 🔒 |
| New: `UrlShortener.AnalyticsWorker` | Kafka consumer, idempotent insert + daily aggregates | Medium: new deployable |
| `click_events` schema | Unchanged columns; consumer inserts with `ON CONFLICT (Id) DO NOTHING` | Low |
| New table `link_daily_stats` | Pre-aggregated counts per code per UTC day | Low: additive migration 🔒 |
| `DbLinkStatsReader` | Totals and daily series from aggregates; top-N still from raw rows (off the hot path) | Low |
| `LinkService` create/delete | Write-through / invalidate cache entry | Medium: stale cache = deleted link still redirects |
| Integration tests | Stats are now eventually consistent: tests must wait for ingestion, not assume immediate counts | Medium: avoid flaky sleeps; use a drain hook |
| Configuration | `Analytics:Mode` (`Direct` / `Kafka`), `Kafka:*`, `Cache:*`, `ConnectionStrings:Redis` | Low: 12-factor, defaults keep zero-setup mode working |

### 4. Data flow after the change (proposed)

```
GET /{code}
  ├─ ILinkCache (Redis | memory) ── hit ──► status check ──► 302      (no DB on the hot path)
  │       └─ miss ─► DB read ─► populate cache (TTL ≤ link expiry)
  └─ IClickSink.TryEnqueue(click)   ← in-memory bounded channel; never awaits, never throws
                │
      ClickPublisher (BackgroundService, batches)
        ├─ Analytics:Mode=Kafka  ─► topic "link-clicks" (key = code) ─► AnalyticsWorker ─► DB (idempotent)
        └─ Analytics:Mode=Direct ─► DB directly (zero-setup mode, same ingestion code)
```

---

## URL-11 — Design decision needing sign-off 🔒 (delivery semantics)

### Correction to our own plan

Requirements §8 and the task breakdown said *"Kafka down → click events persisted in a DB outbox"*.
On analysis, **a transactional outbox is the wrong pattern for this path**. An outbox guarantees that an
event is published *if and only if* a business transaction commits. The redirect has **no** business
transaction. Writing an outbox row on every redirect would put the same synchronous DB write back on the
hot path, which is exactly P1–P3. The plan was written before the code existed; the analysis overrides it.

### Options for getting a click from the redirect to the analytics store

| Option | Redirect blocks on | Loss window | Survives Kafka outage | Complexity |
|---|---|---|---|---|
| A. Produce to Kafka inside the request | Kafka | None | ❌ redirect fails or stalls | Low |
| B. Outbox row inside the request | Database | None | ✅ | Medium, and **reintroduces P1–P3** |
| **C. Bounded in-memory buffer → background batch publish (recommended)** | Nothing | Buffered events lost if the process crashes; oldest dropped if the buffer is full during a long outage (counted in a metric) | ✅ for the producer's buffer + retry window (configurable, default 5 min) | Medium |
| D. Buffer → Kafka, spill to DB outbox when Kafka is down | Nothing | Crash window only | ✅ indefinitely | High |

**Recommendation: C.** Rationale:
- The redirect is the product; analytics is approximate by nature (A-5, A-7). Losing a few clicks during
  a crash is acceptable; failing or stalling redirects is not.
- Kafka producer configured for durability once events leave the process: `acks=all`,
  `enable.idempotence=true`, key = short code (per-link ordering).
- Consumer is **at-least-once with an idempotent write**: insert by event ID with `ON CONFLICT DO NOTHING`,
  and increment daily aggregates only for rows actually inserted, in the same DB transaction, then commit
  the Kafka offset. Duplicates from redelivery are absorbed; aggregates stay exact.
- Losses are **observable, not silent**: dropped-event counter and buffer-depth gauge (exported in URL-18).
- D is the upgrade path if the business later decides analytics feeds billing. Documented as a trade-off.

### Caching (URL-12)

Cache-aside on `code → {target, expiresAt, deletedAt}`:
- TTL = `min(Cache:Ttl (default 5 min), expiresAt − now)`, so an expired link never outlives its expiry in cache.
- Create writes through; delete removes the key. Remaining risk: a delete on one instance while another
  instance's **in-memory** cache holds the entry. Mitigation: Redis (shared) in full mode; short TTL in
  zero-setup mode (single instance anyway).
- Cache errors fall back to the database (never fail the redirect). Circuit breaker and timeouts in URL-16.
- No negative caching of 404s in this story (a code probed before creation would then 404 after creation);
  rate limiting (URL-15) protects the DB from random-code scans instead.

### New dependencies (CLAUDE.md requires justification)

| Package | Why | Maintained by |
|---|---|---|
| `Confluent.Kafka` | Official .NET Kafka client (librdkafka) | Confluent, Apache-2.0 |
| `Microsoft.Extensions.Caching.StackExchangeRedis` | `IDistributedCache` over Redis, same abstraction as the in-memory fallback | Microsoft |
| `Microsoft.Extensions.Hosting` (worker) | Generic host for the Analytics Worker | Microsoft |

### Success criteria (to be measured after implementation)

1. `AnalyticsDecouplingTests` passes (redirect 302 while analytics fails).
2. Re-run the 10 s lock incident: redirect throughput and p99 stay near normal.
3. Normal-load p95 at or below baseline (9.64 ms) with cache hits; target NFR-1 p95 < 20 ms is already met.
4. Kafka stopped during load: redirects unaffected; clicks arrive after Kafka returns (within the retry window).
5. Duplicate delivery (worker restarted mid-batch): totals still exact.

---

## Execution (URL-11, URL-12, URL-13)

### What changed

| Area | Change |
|---|---|
| `IClickRecorder` (blocking) | **Removed.** Replaced by `IClickSink.TryEnqueue` (non-blocking, never throws) and `IClickIngestor` (idempotent batch write) |
| `RedirectService` | Cache → DB on miss → populate cache. Click handed to the sink; nothing analytics-related is awaited |
| `LinkService` | Write-through on create, invalidate on delete |
| New `ClickChannel` | Bounded buffer (10k), drop-oldest with a counted callback; buffer-depth gauge |
| New `ClickPublisherService` | Batches (500 / 200 ms), retries a failed batch 3× then counts it, flushes on shutdown, logs drops every ≤10 s |
| New `KafkaClickBatchPublisher` | Non-blocking `Produce` into librdkafka's bounded queue; `acks=all`, idempotent producer, key = code |
| New `UrlShortener.AnalyticsWorker` | Kafka consumer: batch, ingest, then commit offsets; seek back and retry on failure; poison messages skipped and counted |
| New `EfClickIngestor` | One transaction: insert unseen click IDs, upsert `(code, day)` aggregates by the number inserted |
| New table `link_daily_stats` | Migration on both providers; PostgreSQL migration **backfills** legacy clicks |
| New `DistributedLinkCache` | Redis or in-memory behind `IDistributedCache`; 100 ms bound per call; TTL ≤ min(5 min, expiry); failures degrade to a miss |
| `deploy/docker-compose.yml` | + Redis (LRU, 256 MB), Kafka 3.8 KRaft, explicit topic `link-clicks` (6 partitions, auto-create off) |
| `scripts/run-full-local.sh` | Runs API + worker against the compose services, switched by env vars only |

### Results against the success criteria

| # | Criterion | Result |
|---|---|---|
| 1 | `AnalyticsDecouplingTests` passes | ✅ Failing-first test now green (was **500**, now **302**). Plus: hanging analytics doesn't delay redirects; cache down falls back to DB; delete invalidates cache |
| 2 | 10 s analytics lock: redirects near normal | ✅ **2,404 → 25,379 req/s; max 10.03 s → 34.9 ms**; all 507,608 clicks still ingested after the lock |
| 3 | Normal-load p95 ≤ baseline | ✅ **p95 9.64 → 3.10 ms, p99 13.69 → 5.00 ms, 7,428 → 24,800 req/s (3.3×)** |
| 4 | Kafka stopped: redirects unaffected, clicks arrive later | ✅ **after a fix** (below): 620,535 / 620,535 delivered, 0 dropped |
| 5 | Worker killed mid-batch: totals exact | ✅ 521,780 / 521,780; raw rows = aggregates (3,606,064) |
| — | Legacy data preserved through migration | ✅ Backfill: 295,942 raw clicks → 295,942 in aggregates |
| — | Redis stopped | ✅ 0 errors (DB fallback), throughput 14.7k req/s. ⚠️ Found a log flood, see below |

Raw outputs: [perf/01-baseline.md](../perf/01-baseline.md), [perf/02-after-brownfield.md](../perf/02-after-brownfield.md).
Machine: Apple Silicon laptop, everything local (k6, API, worker, PostgreSQL, Redis, Kafka on one host): relative
comparisons are meaningful, absolute numbers are not capacity claims (A-11).

### Defects found by verification (not by unit tests)

1. **Kafka outage dropped 61% of clicks (first implementation).** The chaos test showed 210,200 of 625,796
   clicks arriving. Consumer lag was 0, so the loss was in the API. `dotnet-counters` confirmed it exactly:
   643,391 enqueued = 253,717 published + 389,674 dropped; buffer pinned at 10,000.
   **Root cause:** the publisher awaited Kafka acks per batch, so during the outage it stopped draining and the
   10k buffer overflowed (~0.4 s of outage tolerance instead of the designed retry window).
   **Fix:** non-blocking `Produce` with a delivery callback into librdkafka's bounded queue
   (`Kafka:MaxBufferedMessages`, default 500k ≈ 100 MB ≈ 20 s at 25k clicks/s). **Re-test: 0 dropped.**
   Also added: rate-limited drop warnings in logs, because the loss was invisible without a metrics tool.
2. **Redis outage → log flood (open, assigned to URL-16).** 2,500 warnings in 10 s: every request still tries
   Redis, waits up to 100 ms, then logs. Fix: circuit breaker (stop calling Redis for a cool-down after repeated
   failures), which is exactly URL-16.
3. **URL-13 (the genuine brownfield defect):** analytics failure returned 500 on redirect, violating our own
   architecture rule. Fixed by the decoupling; regression tests in `AnalyticsDecouplingTests`.

### Trade-offs accepted (approved in design)

- Clicks still in memory are lost if the API process crashes (Option C). Bounded by buffer size; counted on overflow.
- A delete during a Redis outage can leave a stale cache entry for ≤ 5 min (logged at Error).
- SQLite (zero-setup) migration does not backfill legacy clicks; dev databases can be reset. PostgreSQL does.
- The Kafka producer path is verified by chaos tests against a real broker, not by unit tests
  (Testcontainers-based tests are a follow-up).
