# syntax=docker/dockerfile:1
# Build context: repository root.  docker build -f src/UrlShortener.AnalyticsWorker/Dockerfile -t urlshortener-worker .

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/UrlShortener.Domain/UrlShortener.Domain.csproj src/UrlShortener.Domain/
COPY src/UrlShortener.Application/UrlShortener.Application.csproj src/UrlShortener.Application/
COPY src/UrlShortener.Infrastructure/UrlShortener.Infrastructure.csproj src/UrlShortener.Infrastructure/
COPY src/UrlShortener.AnalyticsWorker/UrlShortener.AnalyticsWorker.csproj src/UrlShortener.AnalyticsWorker/
RUN dotnet restore src/UrlShortener.AnalyticsWorker/UrlShortener.AnalyticsWorker.csproj
COPY src/ src/
RUN dotnet publish src/UrlShortener.AnalyticsWorker/UrlShortener.AnalyticsWorker.csproj -c Release --no-restore -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENV DOTNET_ENVIRONMENT=Production \
    Logging__Format=json
ENTRYPOINT ["dotnet", "UrlShortener.AnalyticsWorker.dll"]
