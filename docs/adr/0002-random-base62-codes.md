# ADR 0002: 7-character CSPRNG Base62 codes; the database decides uniqueness

**Status:** Accepted, engineer-signed (decision A-1)

## Context
Codes must be short, unguessable (private links), and unique across horizontally scaled instances.

## Decision
`RandomNumberGenerator.GetString` over Base62, length 7 (≈3.5×10¹² codes). Insert and let the unique index reject
collisions; retry up to 5 times. Reserved route words (several are exactly 7 characters) are redrawn.

## Alternatives
| Option | Rejected because |
|---|---|
| Hash of URL | Collisions; same URL always yields same code (reveals prior shortening) |
| Auto-increment → Base62 | Sequential, enumerable codes; central counter bottleneck |
| Check-then-insert | Race condition under concurrency |

## Consequences
- 20 concurrent creates of the same alias: exactly one 201, nineteen 409 (tested on SQLite and PostgreSQL).
- Collision probability is negligible until billions of links; the retry limit surfaces keyspace exhaustion as an error.
