using UrlShortener.Application.Abstractions;

namespace UrlShortener.Api.Contracts;

// Public API contracts (v1). Kept separate from domain types so the domain can evolve
// without breaking clients.

/// <param name="Url">Destination URL (http/https, max 2048 chars).</param>
/// <param name="CustomAlias">Optional alias: 4-32 chars of [A-Za-z0-9_-], not reserved. Stored lowercase.</param>
/// <param name="ExpiresAt">Optional expiry (ISO 8601). After this the link returns 410 Gone.</param>
public sealed record CreateLinkRequest(string? Url, string? CustomAlias, DateTimeOffset? ExpiresAt);

public sealed record LinkResponse(
    string Code,
    string ShortUrl,
    string TargetUrl,
    bool IsCustomAlias,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt);

public sealed record LinkStatsResponse(
    string Code,
    long TotalClicks,
    long UniqueVisitors,
    DateTimeOffset WindowStart,
    IReadOnlyList<DailyClicks> Daily,
    IReadOnlyList<NamedCount> TopReferrers,
    IReadOnlyList<NamedCount> TopUserAgents);
