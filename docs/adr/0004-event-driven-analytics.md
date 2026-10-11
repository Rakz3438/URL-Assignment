# ADR 0004: Event-driven analytics; transactional outbox rejected for the redirect path

**Status:** Accepted, engineer-signed (URL-11, Option C)

## Context
The greenfield design wrote each click synchronously. Measured: a 10 s lock on the analytics table cut redirect
throughput by 68% with 10 s stalls; an analytics failure returned 500 to users.

## Decision
Redirect enqueues the click into a bounded in-memory buffer (non-blocking). A background publisher batches into Kafka
(non-blocking produce; `acks=all`, idempotent producer, key = short code). A separate Analytics Worker consumes with
manual offsets and ingests idempotently (insert unseen click IDs + upsert daily aggregates in one transaction, then
commit offsets).

## Alternatives
| Option | Rejected because |
|---|---|
| Produce to Kafka inside the request | Couples redirects to Kafka availability |
| Outbox row per redirect (AI's original plan) | The redirect has no business transaction; an outbox would reinstate the synchronous DB write being removed |
| Buffer + spill to DB when Kafka is down | Two delivery paths; complexity not justified for approximate analytics |

## Consequences
- 3.3× throughput, p95 9.6 → 3.1 ms; redirects unaffected by analytics, Kafka or worker failures (chaos-tested).
- Clicks in API memory are lost on a crash; outages longer than the buffers drop clicks. Both counted in metrics and logs.
- First implementation awaited acks per batch and lost 61% of clicks in a Kafka outage; found by chaos test, fixed.
