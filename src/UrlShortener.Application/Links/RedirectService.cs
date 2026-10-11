using UrlShortener.Application.Abstractions;
using UrlShortener.Application.Analytics;
using UrlShortener.Application.Common;
using UrlShortener.Domain.Links;

namespace UrlShortener.Application.Links;

/// <summary>Resolves a short code for the redirect path (FR-2) and records the click.</summary>
/// <remarks>Brownfield refactor (URL-11/URL-12). Before: one DB read plus one awaited DB write per redirect, so an
/// analytics failure returned 500 and an analytics lock stalled redirects (docs/scenarios/02-brownfield.md, P1–P4).
/// After: the lookup is served from <see cref="ILinkCache"/> when possible, and the click is handed to a
/// non-blocking <see cref="IClickSink"/>. Nothing analytics-related is awaited on this path.</remarks>
public sealed class RedirectService
{
    private readonly IShortLinkRepository _repository;
    private readonly ILinkCache _cache;
    private readonly IClickSink _clickSink;
    private readonly ClickEventFactory _clickFactory;
    private readonly DomainBlocklist _blocklist;
    private readonly TimeProvider _clock;

    public RedirectService(IShortLinkRepository repository, ILinkCache cache, IClickSink clickSink,
        ClickEventFactory clickFactory, DomainBlocklist blocklist, TimeProvider clock)
    {
        _blocklist = blocklist;
        _repository = repository;
        _cache = cache;
        _clickSink = clickSink;
        _clickFactory = clickFactory;
        _clock = clock;
    }

    public async Task<Result<string>> ResolveAsync(string code, ClickContext context, CancellationToken cancellationToken)
    {
        if (!ShortCode.IsWellFormed(code))
        {
            return Error.NotFound();
        }

        var snapshot = await _cache.GetAsync(code, cancellationToken) ?? await LoadAndCacheAsync(code, cancellationToken);
        if (snapshot is null)
        {
            return Error.NotFound();
        }

        var now = _clock.GetUtcNow();
        if (snapshot.IsExpired(now))
        {
            return Error.Gone("This short link has expired.");
        }

        // Checked at redirect time too, so blocklisting a domain also stops links created before it was listed,
        // without touching the database (scope decision S2).
        if (_blocklist.IsBlocked(new Uri(snapshot.TargetUrl)))
        {
            return Error.Blocked();
        }

        // Fire-and-forget by contract: TryEnqueue never blocks or throws. A full buffer drops the click
        // (counted in metrics) rather than slowing the redirect.
        _clickSink.TryEnqueue(_clickFactory.Create(snapshot.Code, context, now));
        return snapshot.TargetUrl;
    }

    private async Task<LinkSnapshot?> LoadAndCacheAsync(string code, CancellationToken cancellationToken)
    {
        var link = await _repository.GetByCodeAsync(code, cancellationToken);
        if (link is null || link.GetStatus(_clock.GetUtcNow()) == LinkStatus.Deleted)
        {
            // Not cached (no negative caching, approved design): a code probed before creation must work once created.
            return null;
        }

        var snapshot = LinkSnapshot.From(link);
        await _cache.SetAsync(snapshot, cancellationToken);
        return snapshot;
    }
}
