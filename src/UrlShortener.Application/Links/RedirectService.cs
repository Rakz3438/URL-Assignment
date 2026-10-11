using UrlShortener.Application.Abstractions;
using UrlShortener.Application.Analytics;
using UrlShortener.Application.Common;
using UrlShortener.Domain.Links;

namespace UrlShortener.Application.Links;

/// <summary>Resolves a short code for the redirect path (FR-2) and records the click.</summary>
/// <remarks>Initial design (story URL-9): the click is written synchronously before the redirect is returned.
/// This couples redirect latency and availability to the analytics write. It is the baseline that the
/// brownfield scenario (URL-10/URL-11) measures and then refactors to event-driven processing.</remarks>
public sealed class RedirectService
{
    private readonly IShortLinkRepository _repository;
    private readonly IClickRecorder _clickRecorder;
    private readonly ClickEventFactory _clickFactory;
    private readonly TimeProvider _clock;

    public RedirectService(IShortLinkRepository repository, IClickRecorder clickRecorder,
        ClickEventFactory clickFactory, TimeProvider clock)
    {
        _repository = repository;
        _clickRecorder = clickRecorder;
        _clickFactory = clickFactory;
        _clock = clock;
    }

    public async Task<Result<string>> ResolveAsync(string code, ClickContext context, CancellationToken cancellationToken)
    {
        if (!ShortCode.IsWellFormed(code))
        {
            return Error.NotFound();
        }

        var link = await _repository.GetByCodeAsync(code, cancellationToken);
        var now = _clock.GetUtcNow();

        switch (link?.GetStatus(now))
        {
            case null:
            case LinkStatus.Deleted:
                return Error.NotFound();
            case LinkStatus.Expired:
                return Error.Gone("This short link has expired.");
        }

        await _clickRecorder.RecordAsync(_clickFactory.Create(link.Code, context, now), cancellationToken);
        return link.TargetUrl;
    }
}
