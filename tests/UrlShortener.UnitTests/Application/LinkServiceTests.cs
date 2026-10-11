using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using UrlShortener.Application;
using UrlShortener.Application.Common;
using UrlShortener.Application.Links;
using UrlShortener.Domain.Links;
using UrlShortener.UnitTests.Fakes;

namespace UrlShortener.UnitTests.Application;

public class LinkServiceTests
{
    private const string Url = "https://example.com/page";
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryLinkRepository _repo = new();
    private readonly ShortenerOptions _options = new() { PublicBaseUrl = "https://sho.rt", MaxCodeGenerationAttempts = 3 };

    private LinkService CreateService(params string[] codes) =>
        new(_repo, new ScriptedCodeGenerator(codes), new UrlPolicy(Options.Create(_options)), _clock,
            Options.Create(_options), NullLogger<LinkService>.Instance);

    [Fact]
    public async Task CreateAsync_ValidUrl_CreatesLinkWithGeneratedCode()
    {
        var result = await CreateService("aaaaaaa").CreateAsync(new CreateLinkCommand(Url, null, null, null), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Link.Code.Should().Be("aaaaaaa");
        result.Value.Link.TargetUrl.Should().Be(Url);
        result.Value.Replayed.Should().BeFalse();
    }

    [Fact]
    public async Task CreateAsync_GeneratedCodeCollides_RetriesWithNewCode()
    {
        var service = CreateService("aaaaaaa", "aaaaaaa", "bbbbbbb");
        await service.CreateAsync(new CreateLinkCommand(Url, null, null, null), default);

        var result = await service.CreateAsync(new CreateLinkCommand(Url, null, null, null), default);

        result.Value.Link.Code.Should().Be("bbbbbbb");
        _repo.AddAttempts.Should().Be(3);
    }

    [Fact]
    public async Task CreateAsync_GeneratedCodeIsReservedWord_DrawsAgain()
    {
        var result = await CreateService("swagger", "bbbbbbb").CreateAsync(new CreateLinkCommand(Url, null, null, null), default);

        result.Value.Link.Code.Should().Be("bbbbbbb");
        _repo.AddAttempts.Should().Be(1, "the reserved code must be rejected before touching storage");
    }

    [Fact]
    public async Task CreateAsync_AllAttemptsCollide_ThrowsAfterConfiguredAttempts()
    {
        var service = CreateService("aaaaaaa", "aaaaaaa", "aaaaaaa", "aaaaaaa");
        await service.CreateAsync(new CreateLinkCommand(Url, null, null, null), default);

        var act = () => service.CreateAsync(new CreateLinkCommand(Url, null, null, null), default);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _repo.AddAttempts.Should().Be(1 + _options.MaxCodeGenerationAttempts);
    }

    [Fact]
    public async Task CreateAsync_AliasTaken_ReturnsConflictWithoutRetry()
    {
        var service = CreateService();
        await service.CreateAsync(new CreateLinkCommand(Url, "promo", null, null), default);

        var result = await service.CreateAsync(new CreateLinkCommand("https://other.example/", "promo", null, null), default);

        result.Error!.Type.Should().Be(ErrorType.Conflict);
        _repo.AddAttempts.Should().Be(2);
    }

    [Fact]
    public async Task CreateAsync_AliasDiffersOnlyByCase_StoredLowercaseAndConflicts()
    {
        var service = CreateService();

        var first = await service.CreateAsync(new CreateLinkCommand(Url, "MyPromo", null, null), default);
        var second = await service.CreateAsync(new CreateLinkCommand("https://evil.example/", "mypromo", null, null), default);

        first.Value.Link.Code.Should().Be("mypromo");
        second.Error!.Type.Should().Be(ErrorType.Conflict);
    }

    [Fact]
    public async Task CreateAsync_SameUrlTwiceWithoutKey_CreatesTwoLinks()
    {
        // Decision A-3: no URL-based dedupe; separate campaigns need separate analytics.
        var service = CreateService("aaaaaaa", "bbbbbbb");

        await service.CreateAsync(new CreateLinkCommand(Url, null, null, null), default);
        await service.CreateAsync(new CreateLinkCommand(Url, null, null, null), default);

        _repo.All.Should().HaveCount(2);
    }

    [Fact]
    public async Task CreateAsync_RetryWithSameIdempotencyKey_ReplaysOriginal()
    {
        var service = CreateService("aaaaaaa", "bbbbbbb");
        var command = new CreateLinkCommand(Url, null, null, "key-123");

        var first = await service.CreateAsync(command, default);
        var second = await service.CreateAsync(command, default);

        second.Value.Replayed.Should().BeTrue();
        second.Value.Link.Code.Should().Be(first.Value.Link.Code);
        _repo.All.Should().HaveCount(1);
    }

    [Fact]
    public async Task CreateAsync_SameKeyDifferentBody_ReturnsUnprocessable()
    {
        var service = CreateService("aaaaaaa", "bbbbbbb");
        await service.CreateAsync(new CreateLinkCommand(Url, null, null, "key-123"), default);

        var result = await service.CreateAsync(new CreateLinkCommand("https://different.example/", null, null, "key-123"), default);

        result.Error!.Type.Should().Be(ErrorType.Unprocessable);
    }

    [Fact]
    public async Task CreateAsync_ConcurrentRequestsSameKey_ExactlyOneLinkCreated()
    {
        var codes = Enumerable.Range(0, 20).Select(i => $"code{i:000}").ToArray();
        var service = CreateService(codes);
        var command = new CreateLinkCommand(Url, null, null, "key-race");

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => service.CreateAsync(command, default))));

        results.Should().OnlyContain(r => r.IsSuccess);
        results.Select(r => r.Value.Link.Code).Distinct().Should().HaveCount(1);
        _repo.All.Should().HaveCount(1);
    }

    [Fact]
    public async Task CreateAsync_ExpiryInPast_ReturnsValidationError()
    {
        var result = await CreateService("aaaaaaa")
            .CreateAsync(new CreateLinkCommand(Url, null, _clock.GetUtcNow().AddSeconds(-1), null), default);

        result.Error!.Type.Should().Be(ErrorType.Validation);
    }

    [Fact]
    public async Task CreateAsync_UnsafeUrl_ReturnsValidationErrorAndDoesNotPersist()
    {
        var result = await CreateService("aaaaaaa").CreateAsync(new CreateLinkCommand("javascript:alert(1)", null, null, null), default);

        result.Error!.Type.Should().Be(ErrorType.Validation);
        _repo.AddAttempts.Should().Be(0);
    }

    [Fact]
    public async Task CreateAsync_IdempotencyKeyTooLong_ReturnsValidationError()
    {
        var result = await CreateService("aaaaaaa")
            .CreateAsync(new CreateLinkCommand(Url, null, null, new string('k', LinkService.MaxIdempotencyKeyLength + 1)), default);

        result.Error!.Code.Should().Be("idempotencyKey.invalid");
    }

    [Fact]
    public async Task GetAsync_DeletedLink_ReturnsNotFound()
    {
        var service = CreateService("aaaaaaa");
        await service.CreateAsync(new CreateLinkCommand(Url, null, null, null), default);
        await service.DeleteAsync("aaaaaaa", default);

        (await service.GetAsync("aaaaaaa", default)).Error!.Type.Should().Be(ErrorType.NotFound);
        (await service.DeleteAsync("aaaaaaa", default)).Error!.Type.Should().Be(ErrorType.NotFound);
    }

    [Fact]
    public async Task GetAsync_ExpiredLink_StillReturnsMetadata()
    {
        var service = CreateService("aaaaaaa");
        await service.CreateAsync(new CreateLinkCommand(Url, null, _clock.GetUtcNow().AddHours(1), null), default);
        _clock.Advance(TimeSpan.FromHours(2));

        var result = await service.GetAsync("aaaaaaa", default);

        result.Value.GetStatus(_clock.GetUtcNow()).Should().Be(LinkStatus.Expired);
    }
}
