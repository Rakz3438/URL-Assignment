using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using UrlShortener.Application;
using UrlShortener.Application.Abstractions;
using UrlShortener.Application.Analytics;
using UrlShortener.Application.Common;
using UrlShortener.Application.Links;
using UrlShortener.Domain.Links;
using UrlShortener.UnitTests.Fakes;

namespace UrlShortener.UnitTests.Application;

public class RedirectServiceTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryLinkRepository _repo = new();
    private readonly InMemoryLinkCache _cache = new();
    private readonly CollectingClickSink _sink = new();
    private readonly ClickContext _context = new("https://ref.example/", "curl/8", "203.0.113.1");

    private RedirectService CreateService(params string[] blocked) => new(_repo, _cache, _sink,
        new ClickEventFactory(Options.Create(new ShortenerOptions { VisitorHashSalt = "unit-test-salt-0123456789" })),
        DomainBlocklist.FromDomains(blocked), _clock);

    [Fact]
    public async Task ResolveAsync_CacheHit_DoesNotReadDatabase()
    {
        await _cache.SetAsync(new LinkSnapshot("abc1234", "https://cached.example/", null), default);

        var result = await CreateService().ResolveAsync("abc1234", _context, default);

        result.Value.Should().Be("https://cached.example/");
        _repo.Reads.Should().Be(0);
    }

    [Fact]
    public async Task ResolveAsync_CacheMiss_LoadsFromDatabaseAndPopulatesCache()
    {
        await _repo.TryAddAsync(ShortLink.Create("abc1234", "https://db.example/", false, _clock.GetUtcNow(), null), default);

        var first = await CreateService().ResolveAsync("abc1234", _context, default);
        var second = await CreateService().ResolveAsync("abc1234", _context, default);

        first.Value.Should().Be("https://db.example/");
        second.Value.Should().Be("https://db.example/");
        _repo.Reads.Should().Be(1, "the second lookup is a cache hit");
    }

    [Fact]
    public async Task ResolveAsync_DeletedLinkInDatabase_NotFoundAndNotCached()
    {
        var link = ShortLink.Create("abc1234", "https://db.example/", false, _clock.GetUtcNow(), null);
        link.Delete(_clock.GetUtcNow());
        await _repo.TryAddAsync(link, default);

        var result = await CreateService().ResolveAsync("abc1234", _context, default);

        result.Error!.Type.Should().Be(ErrorType.NotFound);
        _cache.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_CachedLinkPastExpiry_ReturnsGoneWithoutClick()
    {
        await _cache.SetAsync(new LinkSnapshot("abc1234", "https://x.example/", _clock.GetUtcNow().AddMinutes(1)), default);
        _clock.Advance(TimeSpan.FromMinutes(2));

        var result = await CreateService().ResolveAsync("abc1234", _context, default);

        result.Error!.Type.Should().Be(ErrorType.Gone);
        _sink.Clicks.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_DomainBlockedAfterCreation_ReturnsBlockedWithoutClick()
    {
        await _cache.SetAsync(new LinkSnapshot("abc1234", "https://cdn.later-bad.example/x", null), default);

        var result = await CreateService("later-bad.example").ResolveAsync("abc1234", _context, default);

        result.Error!.Code.Should().Be("link.blocked");
        result.Error.Type.Should().Be(ErrorType.Gone);
        _sink.Clicks.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_Active_EnqueuesExactlyOneClick()
    {
        await _cache.SetAsync(new LinkSnapshot("abc1234", "https://x.example/", null), default);

        await CreateService().ResolveAsync("abc1234", _context, default);

        _sink.Clicks.Should().ContainSingle().Which.ReferrerHost.Should().Be("ref.example");
    }
}
