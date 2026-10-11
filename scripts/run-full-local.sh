#!/usr/bin/env bash
# Runs the API + Analytics Worker on this machine against the docker-compose backing services
# (PostgreSQL, Redis, Kafka): the "full infrastructure" mode, switched purely by environment variables.
# Usage: ./scripts/run-full-local.sh        (Ctrl+C stops both)
set -euo pipefail
cd "$(dirname "$0")/.."
DOTNET="${DOTNET:-dotnet}"; command -v "$DOTNET" >/dev/null 2>&1 || DOTNET="$HOME/.dotnet/dotnet"

docker compose -f deploy/docker-compose.yml up -d --wait

export Database__Provider=Postgres
export ConnectionStrings__Links="Host=localhost;Port=55432;Database=urlshortener;Username=app;Password=local-dev-only"
export Kafka__BootstrapServers=localhost:9092

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
