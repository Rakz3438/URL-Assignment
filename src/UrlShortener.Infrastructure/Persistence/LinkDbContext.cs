using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using UrlShortener.Domain.Analytics;
using UrlShortener.Domain.Links;

namespace UrlShortener.Infrastructure.Persistence;

/// <summary>Provider-neutral model. Each provider has a subclass so it can own its own migration set
/// (the EF Core-recommended pattern for multi-provider support).</summary>
public abstract class LinkDbContext : DbContext
{
    protected LinkDbContext(DbContextOptions options) : base(options) { }

    public DbSet<ShortLink> ShortLinks => Set<ShortLink>();
    public DbSet<ClickEvent> ClickEvents => Set<ClickEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<ShortLink>(b =>
        {
            b.ToTable("short_links");
            b.HasKey(x => x.Id);
            b.Property(x => x.Code).HasMaxLength(ShortCode.AliasMaxLength).IsRequired();
            b.Property(x => x.TargetUrl).HasMaxLength(ShortLink.MaxTargetUrlLength).IsRequired();
            b.Property(x => x.IdempotencyKey).HasMaxLength(128);
            b.Property(x => x.RequestHash).HasMaxLength(64);

            // The unique index is the arbiter of code uniqueness (decision A-1): no check-then-insert races.
            b.HasIndex(x => x.Code).IsUnique();
            b.HasIndex(x => x.IdempotencyKey).IsUnique();
        });

        modelBuilder.Entity<ClickEvent>(b =>
        {
            b.ToTable("click_events");
            b.HasKey(x => x.Id);
            b.Property(x => x.Code).HasMaxLength(ShortCode.AliasMaxLength).IsRequired();
            b.Property(x => x.ReferrerHost).HasMaxLength(ClickEvent.MaxReferrerLength);
            b.Property(x => x.UserAgentFamily).HasMaxLength(32).IsRequired();
            b.Property(x => x.VisitorHash).HasMaxLength(32);
            b.HasIndex(x => new { x.Code, x.OccurredAt });
        });
    }
}

public sealed class SqliteLinkDbContext : LinkDbContext
{
    public SqliteLinkDbContext(DbContextOptions<SqliteLinkDbContext> options) : base(options) { }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        // SQLite has no native DateTimeOffset type and cannot compare/order the default TEXT form.
        // All values are UTC (enforced in the domain), so a binary (ticks) encoding orders correctly.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
    }
}

public sealed class PostgresLinkDbContext : LinkDbContext
{
    public PostgresLinkDbContext(DbContextOptions<PostgresLinkDbContext> options) : base(options) { }
}
