using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace UrlShortener.Infrastructure.Persistence;

// Used only by `dotnet ef migrations add`. No database connection is opened when generating migrations.

internal sealed class SqliteDesignTimeFactory : IDesignTimeDbContextFactory<SqliteLinkDbContext>
{
    public SqliteLinkDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SqliteLinkDbContext>().UseSqlite("Data Source=design.db").Options);
}

internal sealed class PostgresDesignTimeFactory : IDesignTimeDbContextFactory<PostgresLinkDbContext>
{
    public PostgresLinkDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PostgresLinkDbContext>().UseNpgsql("Host=localhost;Database=design").Options);
}
