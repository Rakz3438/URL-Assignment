using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using UrlShortener.Application.Abstractions;
using UrlShortener.Domain.Analytics;
using UrlShortener.Domain.Links;

namespace UrlShortener.Infrastructure.Persistence;

internal sealed class ShortLinkRepository : IShortLinkRepository
{
    private readonly LinkDbContext _db;

    public ShortLinkRepository(LinkDbContext db) => _db = db;

    public Task<ShortLink?> GetByCodeAsync(string code, CancellationToken cancellationToken) =>
        _db.ShortLinks.AsNoTracking().FirstOrDefaultAsync(x => x.Code == code, cancellationToken);

    public Task<ShortLink?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken) =>
        _db.ShortLinks.AsNoTracking().FirstOrDefaultAsync(x => x.IdempotencyKey == idempotencyKey, cancellationToken);

    public async Task<bool> TryAddAsync(ShortLink link, CancellationToken cancellationToken)
    {
        var entry = _db.ShortLinks.Add(link);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (UniqueViolation.Is(ex))
        {
            return false;
        }
        finally
        {
            // Leave the context clean so the caller can retry with a new code in the same scope.
            entry.State = EntityState.Detached;
        }
    }

    public async Task UpdateAsync(ShortLink link, CancellationToken cancellationToken)
    {
        _db.ShortLinks.Update(link);
        await _db.SaveChangesAsync(cancellationToken);
        _db.Entry(link).State = EntityState.Detached;
    }
}

/// <summary>Idempotent batch ingestion (URL-11), shared by the zero-setup publisher and the Analytics Worker.
/// One transaction: insert only clicks whose ID is not yet stored, then upsert per-(code, day) aggregates by the
/// number actually inserted. Redelivered batches therefore change nothing, so at-least-once delivery yields exact counts.</summary>
internal sealed class EfClickIngestor : IClickIngestor
{
    private readonly LinkDbContext _db;

    public EfClickIngestor(LinkDbContext db) => _db = db;

    public async Task<int> IngestAsync(IReadOnlyCollection<ClickEvent> clicks, CancellationToken cancellationToken)
    {
        var batch = clicks.DistinctBy(c => c.Id).ToList();
        if (batch.Count == 0)
        {
            return 0;
        }

        var ids = batch.Select(c => c.Id).ToList();
        try
        {
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

            var existing = (await _db.ClickEvents.Where(c => ids.Contains(c.Id)).Select(c => c.Id).ToListAsync(cancellationToken)).ToHashSet();
            var fresh = batch.Where(c => !existing.Contains(c.Id)).ToList();
            if (fresh.Count > 0)
            {
                _db.ClickEvents.AddRange(fresh);
                await _db.SaveChangesAsync(cancellationToken);

                foreach (var day in fresh.GroupBy(c => (c.Code, Day: DateOnly.FromDateTime(c.OccurredAt.UtcDateTime))))
                {
                    // Atomic upsert, valid on both PostgreSQL and SQLite. Parameterized via interpolation (no injection).
                    await _db.Database.ExecuteSqlInterpolatedAsync(
                        $"""
                        INSERT INTO link_daily_stats ("Code", "Day", "Clicks") VALUES ({day.Key.Code}, {day.Key.Day}, {(long)day.Count()})
                        ON CONFLICT ("Code", "Day") DO UPDATE SET "Clicks" = link_daily_stats."Clicks" + excluded."Clicks"
                        """, cancellationToken);
                }
            }

            await tx.CommitAsync(cancellationToken);
            return fresh.Count;
        }
        finally
        {
            // A failed batch is retried by the caller with a fresh view of what is already stored.
            _db.ChangeTracker.Clear();
        }
    }
}

/// <summary>Totals and daily series come from pre-aggregated <c>link_daily_stats</c> (cheap, indexed by key).
/// Unique visitors and top-N dimensions still read raw clicks in the window: off the redirect path, and bounded by
/// the window. Pre-aggregating those is the next step if stats load grows.</summary>
internal sealed class DbLinkStatsReader : ILinkStatsReader
{
    private const int TopN = 5;
    private readonly LinkDbContext _db;

    public DbLinkStatsReader(LinkDbContext db) => _db = db;

    public async Task<LinkStatsData> GetStatsAsync(string code, DateTimeOffset fromInclusive, CancellationToken cancellationToken)
    {
        var aggregates = _db.DailyStats.AsNoTracking().Where(x => x.Code == code);
        var total = await aggregates.SumAsync(x => x.Clicks, cancellationToken);

        var fromDay = DateOnly.FromDateTime(fromInclusive.UtcDateTime);
        var daily = (await aggregates.Where(x => x.Day >= fromDay).OrderBy(x => x.Day)
                .Select(x => new { x.Day, x.Clicks }).ToListAsync(cancellationToken))
            .ConvertAll(x => new DailyClicks(x.Day, x.Clicks));

        var window = _db.ClickEvents.AsNoTracking().Where(x => x.Code == code && x.OccurredAt >= fromInclusive);
        var unique = await window.Where(x => x.VisitorHash != null).Select(x => x.VisitorHash).Distinct().LongCountAsync(cancellationToken);

        // Order and limit in SQL on the aggregate itself; EF cannot translate ordering by a member
        // of a record constructed in the projection, so the record is built after materialization.
        var referrers = (await window.Where(x => x.ReferrerHost != null)
            .GroupBy(x => x.ReferrerHost!)
            .OrderByDescending(g => g.LongCount()).Take(TopN)
            .Select(g => new { Name = g.Key, Count = g.LongCount() })
            .ToListAsync(cancellationToken))
            .ConvertAll(x => new NamedCount(x.Name, x.Count));

        var agents = (await window
            .GroupBy(x => x.UserAgentFamily)
            .OrderByDescending(g => g.LongCount()).Take(TopN)
            .Select(g => new { Name = g.Key, Count = g.LongCount() })
            .ToListAsync(cancellationToken))
            .ConvertAll(x => new NamedCount(x.Name, x.Count));

        return new LinkStatsData(total, unique, daily, referrers, agents);
    }
}

internal static class UniqueViolation
{
    private const int SqliteConstraintUnique = 2067;
    private const string PostgresUniqueViolation = "23505";

    public static bool Is(DbUpdateException ex) => ex.InnerException switch
    {
        SqliteException sqlite => sqlite.SqliteExtendedErrorCode == SqliteConstraintUnique,
        PostgresException pg => pg.SqlState == PostgresUniqueViolation,
        _ => false,
    };
}
