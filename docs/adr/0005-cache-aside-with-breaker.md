# ADR 0005: Redis cache-aside with a circuit breaker

**Status:** Accepted, engineer-signed (URL-12, R1)

## Decision
Cache `code → {target, expiresAt}`; TTL = min(5 min, time to expiry); write-through on create, invalidate on delete;
no negative caching. Every call bounded to 100 ms and wrapped in a Polly circuit breaker (≥50% failures over ≥10 calls in
2 s → open 15 s). Any cache failure degrades to a database read; the cache never fails a request.

## Consequences
- Redis outage: 0 user errors; 3 log lines per outage (2,500 without breaker).
- The first breaker used a 10 s window and took 9 s to open under load (worse than no breaker). Window tuned to 2 s
  from chaos-test evidence.
- A delete during a Redis outage can leave a stale entry ≤ 5 min (logged at Error). Accepted.
