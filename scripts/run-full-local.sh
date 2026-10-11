#!/usr/bin/env bash
# Runs the API + Analytics Worker on this machine against the docker-compose backing services
# (PostgreSQL, Redis, Kafka): the "full infrastructure" mode, switched purely by environment variables.
# Usage: ./scripts/run-full-local.sh        (Ctrl+C stops both)
set -euo pipefail
cd "$(dirname "$0")/.."
DOTNET="${DOTNET:-dotnet}"; command -v "$DOTNET" >/dev/null 2>&1 || DOTNET="$HOME/.dotnet/dotnet"

docker compose -f deploy/docker-compose.yml up -d --wait

export Database__Provider=Postgres
# Timeout=3: fail fast during a DB outage (Npgsql defaults to 15 s per connection attempt), so uncached requests
# get a quick 503 instead of hanging (Scenario 3, R1).
export ConnectionStrings__Links="Host=localhost;Port=55432;Database=urlshortener;Username=app;Password=local-dev-only;Timeout=3;Command Timeout=5"
export Kafka__BootstrapServers=localhost:9092
export Logging__Format="${LOG_FORMAT:-json}"   # JSON lines, as production would ship to Splunk

# Send telemetry to the Aspire dashboard when it is running (compose profile "observability").
if curl -s -o /dev/null --max-time 1 http://localhost:18888; then
  export OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317
  echo "Telemetry -> Aspire dashboard at http://localhost:18888"
fi

# Load testing from one machine looks like a single client: set RATE_LIMITS=off to measure capacity, not the limiter.
if [ "${RATE_LIMITS:-on}" = "off" ]; then export RateLimiting__Enabled=false; fi

"$DOTNET" build -c Release -v q

# Worker first so it is consuming by the time clicks arrive.
DOTNET_ENVIRONMENT=Development "$DOTNET" run -c Release --no-build --project src/UrlShortener.AnalyticsWorker &
WORKER=$!
trap 'kill $WORKER 2>/dev/null || true' EXIT

ASPNETCORE_ENVIRONMENT=Development \
ConnectionStrings__Redis="localhost:56379,abortConnect=false,connectTimeout=1000,syncTimeout=250" \
Analytics__Mode=Kafka \
Logging__LogLevel__Default="${API_LOG_LEVEL:-Information}" \
  "$DOTNET" run -c Release --no-build --project src/UrlShortener.Api --urls "http://0.0.0.0:5080"
