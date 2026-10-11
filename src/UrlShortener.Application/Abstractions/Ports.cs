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

/// <summary>Hands a click off for asynchronous processing (URL-11). Called on the redirect hot path, so
/// implementations must be non-blocking and must never throw: analytics is not allowed to affect redirects.</summary>
public interface IClickSink
{
    /// <returns><c>false</c> if the click could not be accepted (it is counted as dropped, never retried).</returns>
    bool TryEnqueue(ClickEvent click);
}

/// <summary>Persists a batch of clicks idempotently: a click whose ID was already stored is ignored, and
/// daily aggregates are incremented only for newly stored clicks, in the same transaction.
/// Safe under at-least-once delivery.</summary>
public interface IClickIngestor
{
    /// <returns>Number of clicks newly stored (duplicates excluded).</returns>
    Task<int> IngestAsync(IReadOnlyCollection<ClickEvent> clicks, CancellationToken cancellationToken);
}

/// <summary>The minimum needed to serve a redirect, cached so hot links never touch the database (URL-12).</summary>
public sealed record LinkSnapshot(string Code, string TargetUrl, DateTimeOffset? ExpiresAt)
{
    public static LinkSnapshot From(ShortLink link) => new(link.Code, link.TargetUrl, link.ExpiresAt);

    public bool IsExpired(DateTimeOffset now) => ExpiresAt is not null && ExpiresAt <= now;
}

/// <summary>Cache for redirect lookups. Contract: never throws and never blocks for long. On any cache
/// failure, reads return <c>null</c> (caller falls back to the database) and writes are skipped.</summary>
public interface ILinkCache
{
    Task<LinkSnapshot?> GetAsync(string code, CancellationToken cancellationToken);

    Task SetAsync(LinkSnapshot link, CancellationToken cancellationToken);

    Task RemoveAsync(string code, CancellationToken cancellationToken);
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
