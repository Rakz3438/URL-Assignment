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

/// <summary>Initial synchronous click recorder (story URL-9, "legacy" baseline).</summary>
internal sealed class DbClickRecorder : IClickRecorder
{
    private readonly LinkDbContext _db;

    public DbClickRecorder(LinkDbContext db) => _db = db;

    public async Task RecordAsync(ClickEvent click, CancellationToken cancellationToken)
    {
        _db.ClickEvents.Add(click);
        await _db.SaveChangesAsync(cancellationToken);
        _db.Entry(click).State = EntityState.Detached;
    }
}

/// <summary>Stats computed from raw click rows. O(clicks in window): acceptable for the baseline,
/// replaced by pre-aggregated counters in the brownfield scenario.</summary>
internal sealed class DbLinkStatsReader : ILinkStatsReader
{
    private const int TopN = 5;
    private readonly LinkDbContext _db;

    public DbLinkStatsReader(LinkDbContext db) => _db = db;

    public async Task<LinkStatsData> GetStatsAsync(string code, DateTimeOffset fromInclusive, CancellationToken cancellationToken)
    {
        var clicks = _db.ClickEvents.AsNoTracking().Where(x => x.Code == code);
        var total = await clicks.LongCountAsync(cancellationToken);

        var window = clicks.Where(x => x.OccurredAt >= fromInclusive);
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

        // Day bucketing in memory: date functions differ per provider and on SQLite's binary-encoded column.
        var timestamps = await window.Select(x => x.OccurredAt).ToListAsync(cancellationToken);
        var daily = timestamps
            .GroupBy(t => DateOnly.FromDateTime(t.UtcDateTime))
            .OrderBy(g => g.Key)
            .Select(g => new DailyClicks(g.Key, g.LongCount()))
            .ToList();

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
