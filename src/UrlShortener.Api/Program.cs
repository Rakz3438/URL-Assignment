using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.OpenApi.Models;
using UrlShortener.Api.Auth;
using UrlShortener.Api.Endpoints;
using UrlShortener.Application;
using UrlShortener.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

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

// ---- HTTP concerns ----
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null);
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

// Unhandled exceptions become a generic 500 ProblemDetails: no stack traces or messages leak to clients.
app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseSwagger();
if (app.Environment.IsDevelopment())
{
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapLinkEndpoints();
app.MapRedirectEndpoints();

if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    await app.Services.MigrateDatabaseAsync();
}

await app.RunAsync();

/// <summary>Exposed for WebApplicationFactory in integration tests.</summary>
public partial class Program;
