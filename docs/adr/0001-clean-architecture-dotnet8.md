# ADR 0001: Clean Architecture on .NET 8 with enforced layering

**Status:** Accepted (2026-10-11) · **Deciders:** Rakz3438 (engineer), Claude Code (proposal)

## Context
The role is a .NET engineering role; the assignment grades modularity, testability and safe change. The service has a
small domain but several volatile infrastructure choices (two databases, cache, message bus).

## Decision
.NET 8 LTS, ASP.NET Core Minimal APIs, four layers (Domain → Application → Infrastructure/Api) plus a separate worker.
Infrastructure sits behind ports. Layering is enforced by tests, not convention.

## Alternatives
- Single-project minimal API: faster to start; infrastructure leaks into business logic and the brownfield swap
  (sync DB writes → Kafka) would have touched use cases.
- Python/FastAPI: AI's first proposal; rejected because the role is .NET (AI log #1).

## Consequences
- The brownfield refactor changed one port (`IClickRecorder` → `IClickSink` + `IClickIngestor`) and no domain code.
- Architecture tests caught two drifts (12 types made public; composition types added deliberately).
- More projects and indirection than a prototype strictly needs: accepted for production shape.
