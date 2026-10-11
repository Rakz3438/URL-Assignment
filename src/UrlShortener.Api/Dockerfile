# syntax=docker/dockerfile:1
# Build context: repository root.  docker build -f src/UrlShortener.Api/Dockerfile -t urlshortener-api .

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
# Restore first, from project files only, so the dependency layer is cached across code changes.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/UrlShortener.Domain/UrlShortener.Domain.csproj src/UrlShortener.Domain/
COPY src/UrlShortener.Application/UrlShortener.Application.csproj src/UrlShortener.Application/
COPY src/UrlShortener.Infrastructure/UrlShortener.Infrastructure.csproj src/UrlShortener.Infrastructure/
COPY src/UrlShortener.Api/UrlShortener.Api.csproj src/UrlShortener.Api/
RUN dotnet restore src/UrlShortener.Api/UrlShortener.Api.csproj
COPY src/ src/
RUN dotnet publish src/UrlShortener.Api/UrlShortener.Api.csproj -c Release --no-restore -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app .
# Non-root (UID provided by the .NET 8 base image). Port 8080 is the image default for non-root.
USER $APP_UID
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_gcServer=1 \
    Logging__Format=json
EXPOSE 8080
ENTRYPOINT ["dotnet", "UrlShortener.Api.dll"]
