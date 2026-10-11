using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UrlShortener.Application.Abstractions;
using UrlShortener.Infrastructure.Analytics;
using UrlShortener.Infrastructure.Caching;
using UrlShortener.Infrastructure.Codes;
using UrlShortener.Infrastructure.Persistence;

namespace UrlShortener.Infrastructure;

public enum DatabaseProvider
{
    Sqlite,
    Postgres,
}

public static class DependencyInjection
{
    /// <summary>Services shared by the API and the Analytics Worker. Backing services are selected purely by
    /// configuration (12-factor, factor IV): <c>Database__Provider</c>, <c>ConnectionStrings__Links</c>,
    /// <c>ConnectionStrings__Redis</c> (absent = in-memory cache).</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var provider = configuration.GetValue("Database:Provider", DatabaseProvider.Sqlite);
        var connectionString = configuration.GetConnectionString("Links")
            ?? (provider == DatabaseProvider.Sqlite
                ? "Data Source=urlshortener.db"
                : throw new InvalidOperationException("ConnectionStrings:Links is required for PostgreSQL."));

        if (provider == DatabaseProvider.Postgres)
        {
            services.AddDbContext<PostgresLinkDbContext>(o => o.UseNpgsql(connectionString));
            services.AddScoped<LinkDbContext>(sp => sp.GetRequiredService<PostgresLinkDbContext>());
        }
        else
        {
            services.AddDbContext<SqliteLinkDbContext>(o => o.UseSqlite(connectionString));
            services.AddScoped<LinkDbContext>(sp => sp.GetRequiredService<SqliteLinkDbContext>());
        }

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IShortCodeGenerator, RandomShortCodeGenerator>();
        services.AddScoped<IShortLinkRepository, ShortLinkRepository>();
        services.AddScoped<IClickIngestor, EfClickIngestor>();
        services.AddScoped<ILinkStatsReader, DbLinkStatsReader>();

        // Cache (URL-12): Redis when configured, otherwise in-process. Same IDistributedCache abstraction either way.
        services.AddOptions<CacheOptions>().Bind(configuration.GetSection(CacheOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        var redis = configuration.GetConnectionString("Redis");
        if (string.IsNullOrWhiteSpace(redis))
        {
            services.AddDistributedMemoryCache();
        }
        else
        {
            services.AddStackExchangeRedisCache(o =>
            {
                o.Configuration = redis;
                o.InstanceName = "urlshortener:";
            });
        }

        services.AddSingleton<ILinkCache, DistributedLinkCache>();

        services.AddMetrics();
        services.AddSingleton<ClickPipelineMetrics>();
        services.AddOptions<KafkaOptions>().Bind(configuration.GetSection(KafkaOptions.SectionName)).ValidateDataAnnotations();
        return services;
    }

    /// <summary>API side of the click pipeline (URL-11): non-blocking sink on the redirect path plus a background
    /// publisher whose destination depends on <c>Analytics__Mode</c> (Direct = database, Kafka = topic).</summary>
    public static IServiceCollection AddClickPipeline(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<AnalyticsOptions>().Bind(configuration.GetSection(AnalyticsOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddSingleton<ClickChannel>();
        services.AddSingleton<IClickSink>(sp => sp.GetRequiredService<ClickChannel>());

        if (configuration.GetValue("Analytics:Mode", AnalyticsMode.Direct) == AnalyticsMode.Kafka)
        {
            services.AddSingleton<IClickBatchPublisher, KafkaClickBatchPublisher>();
        }
        else
        {
            services.AddSingleton<IClickBatchPublisher, DirectClickBatchPublisher>();
        }

        services.AddHostedService<ClickPublisherService>();
        return services;
    }

    /// <summary>Analytics Worker side: Kafka consumer feeding the idempotent ingestor.</summary>
    public static IServiceCollection AddClickConsumer(this IServiceCollection services)
    {
        services.AddHostedService<KafkaClickConsumerService>();
        return services;
    }

    /// <summary>Applies pending migrations. Convenient for local/dev; in Kubernetes run it as a
    /// pre-deploy Job instead so replicas don't race to migrate (see deployment docs).</summary>
    public static async Task MigrateDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LinkDbContext>();
        await db.Database.MigrateAsync(cancellationToken);
    }
}
