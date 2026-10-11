# ADR 0003: 302 redirects with Cache-Control: no-store

**Status:** Accepted, engineer-signed (decision A-2)

## Context
Browsers cache 301 responses indefinitely.

## Decision
302 Found plus `Cache-Control: no-store` on every redirect.

## Consequences
- Every click reaches the service: analytics stay accurate; deletes, expiry and blocklisting take effect immediately.
- More traffic than 301: absorbed by the Redis cache (p95 3.1 ms at ~25k req/s locally).
