using UrlShortener.Domain.Analytics;
using UrlShortener.Domain.Links;

namespace UrlShortener.Application.Abstractions;

/// <summary>Produces candidate short codes. Uniqueness is NOT guaranteed here;
/// the repository's unique index is the arbiter (decision A-1).</summary>
public interface IShortCodeGenerator
{
    string Generate();
}

public interface IShortLinkRepository
{
    Task<ShortLink?> GetByCodeAsync(string code, CancellationToken cancellationToken);

    Task<ShortLink?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>Inserts the link. Returns <c>false</c> if a unique constraint (code or idempotency key)
    /// was violated, so callers can resolve races without check-then-insert.</summary>
    Task<bool> TryAddAsync(ShortLink link, CancellationToken cancellationToken);

    Task UpdateAsync(ShortLink link, CancellationToken cancellationToken);
}

/// <summary>Records a click. In the initial (greenfield) design this writes synchronously to the database.</summary>
public interface IClickRecorder
{
    Task RecordAsync(ClickEvent click, CancellationToken cancellationToken);
}

public interface ILinkStatsReader
{
    Task<LinkStatsData> GetStatsAsync(string code, DateTimeOffset fromInclusive, CancellationToken cancellationToken);
}

public sealed record DailyClicks(DateOnly Date, long Clicks);

public sealed record NamedCount(string Name, long Count);

public sealed record LinkStatsData(
    long TotalClicks,
    long UniqueVisitors,
    IReadOnlyList<DailyClicks> Daily,
    IReadOnlyList<NamedCount> TopReferrers,
    IReadOnlyList<NamedCount> TopUserAgents);
