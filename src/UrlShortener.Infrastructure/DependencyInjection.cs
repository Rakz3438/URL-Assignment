using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UrlShortener.Application.Abstractions;
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
    /// <summary>Backing services are selected purely by configuration (12-factor, factor IV):
    /// <c>Database__Provider</c> and <c>ConnectionStrings__Links</c>.</summary>
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

        services.AddSingleton<IShortCodeGenerator, RandomShortCodeGenerator>();
        services.AddScoped<IShortLinkRepository, ShortLinkRepository>();
        services.AddScoped<IClickRecorder, DbClickRecorder>();
        services.AddScoped<ILinkStatsReader, DbLinkStatsReader>();
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
