using Microsoft.Extensions.Options;
using UrlShortener.Application.Abstractions;
using UrlShortener.Application.Common;
using UrlShortener.Domain.Links;

namespace UrlShortener.Application.Analytics;

public sealed record LinkStats(string Code, DateTimeOffset WindowStart, LinkStatsData Data);

/// <summary>Aggregated analytics for a link (FR-7).</summary>
public sealed class StatsService
{
    private readonly IShortLinkRepository _links;
    private readonly ILinkStatsReader _reader;
    private readonly TimeProvider _clock;
    private readonly int _windowDays;

    public StatsService(IShortLinkRepository links, ILinkStatsReader reader, TimeProvider clock, IOptions<ShortenerOptions> options)
    {
        _links = links;
        _reader = reader;
        _clock = clock;
        _windowDays = options.Value.StatsWindowDays;
    }

    public async Task<Result<LinkStats>> GetAsync(string code, CancellationToken cancellationToken)
    {
        var link = ShortCode.IsWellFormed(code) ? await _links.GetByCodeAsync(code, cancellationToken) : null;
        var now = _clock.GetUtcNow();
        if (link is null || link.GetStatus(now) == LinkStatus.Deleted)
        {
            return Error.NotFound();
        }

        // Window starts at UTC midnight so daily buckets are complete.
        var from = new DateTimeOffset(now.UtcDateTime.Date.AddDays(-(_windowDays - 1)), TimeSpan.Zero);
        var data = await _reader.GetStatsAsync(link.Code, from, cancellationToken);
        return new LinkStats(link.Code, from, data);
    }
}
