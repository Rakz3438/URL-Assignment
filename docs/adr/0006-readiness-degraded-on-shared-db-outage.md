# ADR 0006: Readiness reports Degraded, not Unhealthy, when the shared database is down

**Status:** Accepted, engineer-signed (amends requirement §8)

## Context
Requirement §8 originally said readiness should fail during a database outage. The database is shared by every replica.

## Decision
`/health/ready` reports the database as **Degraded** (HTTP 200). Liveness never checks dependencies.
`Health__FailReadinessOnDatabaseOutage=true` restores the original behavior.

## Consequences
- Failing readiness on all replicas at once would remove every instance from the load balancer and turn
  "cached links keep working" into a total outage. Measured instead: 24.2k req/s, 0 errors on cached links during
  a 10 s PostgreSQL outage; uncached lookups get a fast 503.
- Readiness no longer protects against a single instance that alone has lost DB connectivity. Accepted; that case
  shows up in alerts on 503 rates.
