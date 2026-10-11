using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using UrlShortener.Infrastructure.Analytics;
using UrlShortener.Infrastructure.Caching;
using UrlShortener.Infrastructure.Persistence;

namespace UrlShortener.Infrastructure.Observability;

/// <summary>Readiness checks (scope decision R2). Tag "ready" = included in /health/ready.</summary>
public static class HealthCheckExtensions
{
    public const string ReadyTag = "ready";

    /// <summary>
    /// Database failure maps to <b>Degraded</b> by default, not Unhealthy. The database is shared by all replicas: failing
    /// readiness everywhere at once would make the load balancer drop every instance, turning "cached links keep working,
    /// the rest get 503" into a total outage. <c>Health__FailReadinessOnDatabaseOutage=true</c> restores the original
    /// requirement (§8) if the engineer prefers it.
    /// </summary>
    public static IHealthChecksBuilder AddUrlShortenerHealthChecks(this IServiceCollection services, IConfiguration configuration,
        bool includeClickPipeline)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var databaseFailure = configuration.GetValue("Health:FailReadinessOnDatabaseOutage", false)
            ? HealthStatus.Unhealthy
            : HealthStatus.Degraded;

        var builder = services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("database", databaseFailure, [ReadyTag])
            .AddCheck<CacheHealthCheck>("cache", HealthStatus.Degraded, [ReadyTag]);

        if (includeClickPipeline)
        {
            builder.AddCheck<ClickPipelineHealthCheck>("click-pipeline", HealthStatus.Degraded, [ReadyTag]);
        }

        return builder;
    }
}

internal sealed class DatabaseHealthCheck(IServiceScopeFactory scopes) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<LinkDbContext>();
            return await db.Database.CanConnectAsync(timeout.Token)
                ? HealthCheckResult.Healthy()
                : new HealthCheckResult(context.Registration.FailureStatus, "Database unreachable; cached links still served");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "Database unreachable; cached links still served", ex);
        }
    }
}

internal sealed class CacheHealthCheck(ICacheHealth cache) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(cache.IsCircuitOpen
            ? new HealthCheckResult(context.Registration.FailureStatus, "Cache circuit open; serving from the database")
            : HealthCheckResult.Healthy());
}

internal sealed class ClickPipelineHealthCheck(ClickChannel channel, IOptions<AnalyticsOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var depth = channel.Reader.Count;
        var capacity = options.Value.BufferCapacity;
        var data = new Dictionary<string, object> { ["bufferDepth"] = depth, ["bufferCapacity"] = capacity };
        return Task.FromResult(depth > capacity * 0.8
            ? new HealthCheckResult(context.Registration.FailureStatus, "Click buffer above 80%: analytics backpressure", data: data)
            : HealthCheckResult.Healthy(data: data));
    }
}
