# ADR 0007: Dependency policy

**Status:** Accepted

## Decision
- One place for versions (`Directory.Packages.props`, central package management with transitive pinning).
- Prefer BCL/Microsoft packages; every third-party package is justified in docs.
- Licence check before adoption; vulnerability audit is a hard gate (restore + `dotnet list package --vulnerable`).

## Consequences (decisions this policy produced)
| Package | Decision | Why |
|---|---|---|
| FluentAssertions | Pinned 6.12.2 | v7+ moved to a commercial licence |
| SQLitePCLRaw (transitive via EF Core) | Pinned 2.1.13 | ≤ 2.1.11 bundles SQLite with CVE-2025-6965 (High); first pin attempt (2.1.11) was still vulnerable |
| OpenTelemetry OTLP exporter | 1.15.3 | 1.9.0–1.15.0 carry a published vulnerability |
| Serilog | Not adopted | Built-in JSON console logging meets the Splunk requirement without a dependency |
| Rate limiting | Built-in ASP.NET Core limiter | No package needed |
| Polly.Core, Confluent.Kafka, StackExchange.Redis cache | Adopted | Standard, maintained, permissive licences |
