using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.OpenApi.Models;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using UrlShortener.Api.Auth;
using UrlShortener.Api.Endpoints;
using UrlShortener.Api.Observability;
using UrlShortener.Api.Security;
using UrlShortener.Application;
using UrlShortener.Infrastructure;
using UrlShortener.Infrastructure.Observability;

var builder = WebApplication.CreateBuilder(args);

// ---- Observability (R3): JSON logs with correlation/trace IDs; OTel metrics + traces over OTLP when configured ----
builder.AddUrlShortenerObservability("urlshortener-api",
    tracing: t => t.AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health")),
    metrics: m => m.AddAspNetCoreInstrumentation());

// ---- Configuration (12-factor: everything overridable by environment variables) ----
builder.Services.AddOptions<ShortenerOptions>()
    .Bind(builder.Configuration.GetSection(ShortenerOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(o => builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing")
                   || o.VisitorHashSalt != new ShortenerOptions().VisitorHashSalt,
        "Shortener:VisitorHashSalt must be set outside Development.")
    .ValidateOnStart();

builder.Services.AddOptions<ApiKeyOptions>()
    .Bind(builder.Configuration.GetSection(ApiKeyOptions.SectionName))
    .Validate(o => o.ApiKeys.Any(k => k.Length >= 16), "At least one API key of 16+ characters must be configured (Auth:ApiKeys).")
    .ValidateOnStart();

// ---- Layers ----
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddClickPipeline(builder.Configuration);
builder.Services.AddOptions<UrlShortener.Application.Links.SecurityOptions>()
    .Bind(builder.Configuration.GetSection(UrlShortener.Application.Links.SecurityOptions.SectionName));

// ---- HTTP concerns ----
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null);
builder.Services.AddUrlShortenerRateLimiting(builder.Configuration);
builder.Services.AddTrustedProxies(builder.Configuration);
builder.Services.AddUrlShortenerHealthChecks(builder.Configuration, includeClickPipeline: true);
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(LinkEndpoints.ManagePolicy, p => p.RequireAuthenticatedUser());

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "URL Shortener API",
        Version = "v1",
        Description = "Create short links, follow them (302), and read click analytics.",
    });
    var scheme = new OpenApiSecurityScheme
    {
        Name = ApiKeyAuthenticationHandler.HeaderName,
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Description = "API key for management endpoints.",
        Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = ApiKeyAuthenticationHandler.SchemeName },
    };
    o.AddSecurityDefinition(ApiKeyAuthenticationHandler.SchemeName, scheme);
    o.AddSecurityRequirement(new OpenApiSecurityRequirement { [scheme] = [] });
});

var app = builder.Build();

// First: establish the real client IP (trusted proxies only) and the correlation ID for everything after.
app.UseForwardedHeaders();
app.UseCorrelationId();

// Unhandled exceptions become a generic 500 ProblemDetails: no stack traces or messages leak to clients.
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseMiddleware<DependencyFailureMiddleware>(); // DB outage → 503 + Retry-After, rate-limited logging
app.UseSecurityHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts(); // only emitted on HTTPS requests; TLS terminates at the ingress in production
}

app.UseSwagger();
if (app.Environment.IsDevelopment())
{
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter(); // after auth: the create policy partitions by API key

app.MapHealthEndpoints();
app.MapLinkEndpoints();
app.MapRedirectEndpoints();

// Migrations: with several replicas, run them once as a separate step (Database__MigrateOnly=true: compose "migrate"
// service, Kubernetes Job) and set Database__MigrateOnStartup=false on the replicas, so they never race each other.
if (app.Configuration.GetValue("Database:MigrateOnly", false))
{
    await app.Services.MigrateDatabaseAsync();
    return;
}

if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    await app.Services.MigrateDatabaseAsync();
}

await app.RunAsync();

/// <summary>Exposed for WebApplicationFactory in integration tests.</summary>
public partial class Program;
