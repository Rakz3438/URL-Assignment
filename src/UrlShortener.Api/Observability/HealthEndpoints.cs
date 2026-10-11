using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using UrlShortener.Infrastructure.Observability;

namespace UrlShortener.Api.Observability;

/// <summary>Probes for Kubernetes and load balancers (scope decision R2).
/// <c>/health/live</c>: the process is up (no dependency checks, so a DB outage never triggers restarts).
/// <c>/health/ready</c>: dependency report; 200 when Healthy or Degraded, 503 only when Unhealthy.</summary>
internal static class HealthEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains(HealthCheckExtensions.ReadyTag),
            ResponseWriter = WriteAsync,
        });
        return app;
    }

    private static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "no-store";
        var body = new
        {
            status = report.Status.ToString(),
            durationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 1),
            checks = report.Entries.ToDictionary(e => e.Key, e => new
            {
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                data = e.Value.Data.Count > 0 ? e.Value.Data : null,
            }),
        };

        // Exception details are deliberately not returned: probes may be reachable from outside the cluster.
        return context.Response.WriteAsync(JsonSerializer.Serialize(body, Json));
    }
}
