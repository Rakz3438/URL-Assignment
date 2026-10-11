# GitHub Copilot custom instructions

This repository shares one set of AI rules across assistants. Follow everything in
[`/CLAUDE.md`](../CLAUDE.md) — architecture rules, code standards, testing, security guardrails,
and traceability. Summary of the most important points:

- .NET 8 / C#, Clean Architecture; `Domain` has no dependencies.
- Redirect path must never block on analytics, Kafka, or cache.
- `RandomNumberGenerator` for codes; DB constraints for uniqueness.
- ProblemDetails for errors; structured logging; no secrets, raw IPs, or full URLs in logs.
- Every change ships with tests; bug fixes ship with a regression test.
- Reference the Jira story ID (`URL-n`) in commits.
