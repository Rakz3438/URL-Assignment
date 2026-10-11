# Final Engineering Summary — AI-Assisted URL Shortener

**Engineer:** Rakz3438 · **AI assistant:** Claude Code (custom instructions shared with GitHub Copilot) ·
**Stack:** .NET 8, ASP.NET Core, EF Core, PostgreSQL, Redis, Kafka, Docker, Kubernetes · **Date:** 2026-10-11

> **Principle applied throughout:** the AI drafted, analyzed and verified within explicit rules; the engineer set
> scope, approved every high-impact decision, and challenged the AI's claims. 38 AI interactions are logged as
> **16 accepted · 11 edited · 9 rejected · 2 deferred**. Several of the rejections are of the AI's *own* earlier work,
> found by verification.

---

## 1. Plan and rationale

| Phase | Output | Why this order |
|---|---|---|
| 0. Requirement normalization | 11 ambiguity decisions, 5 signed off before code | Decisions are cheapest before code exists |
| 1. Decomposition | 6 epics, 25 stories, dependencies, critical path, Jira CSV | Makes sequencing and sign-off points explicit |
| 2. Greenfield (S1) | Core service with tests | A working baseline to modernize |
| 3. Brownfield (S2) | Measured the baseline, then event-driven analytics + cache | Evidence first: change justified by numbers |
| 4. Ambiguous (S3) | 12 interpretations → 6 in scope, 4 explicitly out | Scope is a decision, not an accident |
| 5. Delivery | Containers, CI, Kubernetes | Production shape, repeatable checks |
| 6. Documentation | Architecture, ADRs, this summary | Describe the system as built, not as planned |

The JD shaped feature choices so each keyword is backed by working, tested code: Kafka (event-driven analytics),
caching and profiling (Redis, k6, `dotnet-counters`), resiliency (circuit breaker, graceful degradation),
observability and Splunk (JSON logs, OpenTelemetry), 12-factor (env-only configuration), Docker/K8s/CI, PostgreSQL,
legacy modernization (Scenario 2), and spec-driven AI-assisted development (custom instructions, traceability).

## 2. Artifacts

| Artifact | Location |
|---|---|
| Working prototype: zero-setup and full infrastructure | [`README.md`](../README.md) (setup instructions) |
| Requirements and ambiguity decisions | [`01-requirements.md`](01-requirements.md) |
| Task decomposition (+ Jira import) | [`02-task-breakdown.md`](02-task-breakdown.md), [`jira-backlog.csv`](jira-backlog.csv) |
| Requirements traceability matrix | [`03-traceability-matrix.md`](03-traceability-matrix.md) |
| Architecture overview (components, tools, control flow, decisions) | [`04-architecture.md`](04-architecture.md), [`adr/`](adr/) |
| Scenario write-ups: decomposition → execution → validation | [`scenarios/01-greenfield.md`](scenarios/01-greenfield.md), [`02-brownfield.md`](scenarios/02-brownfield.md), [`03-ambiguous.md`](scenarios/03-ambiguous.md) |
| Performance and chaos evidence (raw k6 output) | [`perf/`](perf/) |
| AI traceability log + sign-off register | [`ai-log/AI-TRACEABILITY.md`](ai-log/AI-TRACEABILITY.md) |
| AI custom instructions | [`../CLAUDE.md`](../CLAUDE.md), [`../.github/copilot-instructions.md`](../.github/copilot-instructions.md) |
| API definition | OpenAPI at `/swagger/v1/swagger.json`; contracts in `src/UrlShortener.Api/Contracts` |
| Schema | EF Core migrations for PostgreSQL and SQLite (`src/UrlShortener.Infrastructure/Persistence/Migrations`) |
| Tests | 132 unit + 43 integration (`tests/`), k6 load test (`loadtest/`) |
| Delivery | Dockerfiles, `deploy/docker-compose.yml`, `k8s/`, `.github/workflows/ci.yml`, `scripts/` |

Size: ~3,200 lines of production C#, ~1,900 lines of tests, 20+ story-scoped commits with sign-off trailers.

## 3. The three scenarios at a glance

| | Greenfield | Brownfield | Ambiguous |
|---|---|---|---|
| Input | One-line brief | "Existing" synchronous analytics | "Make the links safe and reliable" |
| Key engineering move | Normalize into 11 signed decisions | Measure before changing; reject own outbox plan | Tie each interpretation to an observed failure; reject R5 as an SSRF risk |
| Headline result | 121 tests, race-safe by design | **3.3× throughput, p95 9.6 → 3.1 ms**; redirects survive analytics/Kafka outages | Redis outage logs 3 lines (was 2,500); DB outage: cached links unaffected, 0 clicks lost |
| Best catch | IDN host would crash redirects | Kafka publisher lost 61% of clicks in an outage (own bug) | Forged `X-Forwarded-For` was trusted (own bug, security) |

## 4. Testing approach

| Layer | What | Count / evidence |
|---|---|---|
| Unit | Domain invariants, URL policy attack table, use cases with fakes, cache TTL and breaker, ingestor idempotency on real SQLite, architecture rules | 132 |
| Integration | Real HTTP pipeline (`WebApplicationFactory`) + real SQLite: every endpoint, auth, idempotency, 20-way race, rate limits, blocklist, DB-outage 503, health, correlation IDs, trusted proxies, log hygiene | 43 |
| Coverage gate | ≥ 80% lines on Domain + Application, enforced | 87.8% / 97.0% |
| Load | k6 (in Docker), 50 virtual users, before/after each scenario | `perf/` |
| Chaos | Real PostgreSQL / Redis / Kafka / worker killed or locked under load | 10 runs, all recorded |
| Spec audit | Every FR/NFR/decision checked against code, not tests | `03-traceability-matrix.md` |
| Security | NuGet vulnerability audit (hard gate), URL-policy attack table, spoofing tests, non-root image check in CI | 0 vulnerable packages |

Principle: **tests prove the code does what we thought; verification proves we thought of the right things.**
Defects found by verification beyond the unit suite: `/stats` 500 (smoke run), generated code equal to a reserved route
and unenforced coverage target (spec audit), analytics failure → 500 on redirect (incident measurement), Kafka publisher
losing 61% of clicks and a mis-tuned circuit breaker (chaos tests), SQLite CVE (dependency scan). Found by automated
tests: IDN hosts breaking redirects (unit), forged `X-Forwarded-For` trusted (integration).

## 5. Risks, trade-offs and validation

| Risk / trade-off | Decision | Validation | Residual risk |
|---|---|---|---|
| Analytics loss vs redirect availability | Redirect never waits on analytics (ADR 0004) | Chaos: Kafka down, DB down, worker killed: 0 clicks lost | Clicks in API memory lost on process crash; counted |
| Stale cache after delete | Invalidate on delete; TTL ≤ 5 min | Integration test; Error log if invalidation fails | ≤ 5 min stale during a Redis outage |
| Readiness during DB outage | Degraded, not Unhealthy (ADR 0006, amends §8) | Chaos: 24.2k req/s, 0 errors on cached links | Single-instance DB loss not drained (seen via 503 alerts) |
| Abuse / scanning | Per-key and per-IP rate limits | Tests + trusted-proxy spoofing tests | Limits are per instance (N replicas → up to N×) |
| Malicious destinations | URL policy + local domain blocklist | 36 policy cases; blocklist stops existing links | No reputation feed (S2+ out of scope); no DNS-rebinding check |
| Observability cost | Trace sampling 10% outside dev | Measured −13% / −24% throughput | Fewer traces for rare errors (raise ratio when debugging) |
| Dependency risk | Central versions, licence and vulnerability gates (ADR 0007) | 3 vulnerable/licence issues caught and resolved | New CVEs after delivery: CI re-runs the audit |
| Privacy | No raw IPs; HMAC(salt, IP+UA) truncated | Unit tests | Salt rotation resets unique-visitor continuity |

## 6. Assumptions

1. No user accounts: API keys identify clients (A-4).
2. Analytics may be eventually consistent (seconds) and approximate at the margins (A-5, A-7).
3. Target URLs are immutable after creation (anti bait-and-switch).
4. TLS terminates at the ingress; the service sees HTTP plus trusted `X-Forwarded-*`.
5. Performance numbers come from one laptop running all components: valid for **relative** comparison only (A-11).
6. Kubernetes manifests target a cluster with an ingress-nginx controller, a secret manager, and an OTel collector.

## 7. Limitations (honest list)

- **Not deployed to a cloud cluster.** Kubernetes manifests are schema-validated, not exercised; compose is the verified runtime.
- **Worker has no HTTP health endpoint**; health relies on consumer-lag alerting.
- **Rate limits are per instance**; a Redis-backed limiter is the upgrade path.
- **Kafka publisher path is verified by chaos tests, not automated tests**; Testcontainers-based tests are the next step.
- **Poison messages are skipped and counted**, not sent to a dead-letter topic.
- **Top-N referrers/browsers and unique visitors still scan raw clicks** in the 30-day window (off the hot path).
- **SQLite migration does not backfill** legacy clicks (PostgreSQL does, verified on 295,942 rows).
- **CI actions are pinned to major tags**, not commit SHAs.
- **Container images are ~550 MB** (Debian base); chiseled images would cut size and attack surface.

## 8. Engineer ownership: where judgment overrode the AI

| # | AI proposed or did | Engineer-led outcome |
|---|---|---|
| 1 | Python stack | .NET, to match the role |
| 2 | Claimed verification was complete | Engineer demanded a line-by-line spec audit → defects and deviations found |
| 3 | Four spec deviations | Engineer chose each resolution (aliases lowercase, replay 201, A-6 amended, proxy fix scheduled) |
| 4 | Delivery semantics options | Engineer approved Option C after trade-off table |
| 5 | 12 interpretations of an ambiguous ask | Engineer asked which best fits the assignment's criteria, then set scope |
| 6 | Readiness semantics conflicting with §8 | Engineer approved the amendment explicitly |
| 7 | Every 🔒 change | Committed only after explicit sign-off (recorded in commit trailers and the sign-off register) |
