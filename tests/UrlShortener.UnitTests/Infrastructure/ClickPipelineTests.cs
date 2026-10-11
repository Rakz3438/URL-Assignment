using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using UrlShortener.Application.Abstractions;
using UrlShortener.Domain.Analytics;
using UrlShortener.Infrastructure.Analytics;
using UrlShortener.Infrastructure.Caching;
using UrlShortener.Infrastructure.Persistence;
using UrlShortener.UnitTests.Fakes;

namespace UrlShortener.UnitTests.Infrastructure;

public class ClickChannelTests
{
    private static ClickEvent Click(string code = "abc1234") => new(Guid.NewGuid(), code, DateTimeOffset.UtcNow, null, "Other", null);

    [Fact]
    public void TryEnqueue_BufferFull_DropsOldestAndCountsEveryDrop()
    {
        using var meters = new TestMeterFactory();
        var metrics = new ClickPipelineMetrics(meters);
        var channel = new ClickChannel(Options.Create(new AnalyticsOptions { BufferCapacity = 100 }), metrics);
        var first = Click();

        channel.TryEnqueue(first).Should().BeTrue();
        for (var i = 0; i < 149; i++)
        {
            channel.TryEnqueue(Click()).Should().BeTrue("the redirect path is never refused or blocked");
        }

        metrics.Enqueued.Should().Be(150);
        metrics.Dropped.Should().Be(50);
        channel.Reader.Count.Should().Be(100);
        channel.Reader.TryPeek(out var head).Should().BeTrue();
        head!.Id.Should().NotBe(first.Id, "the oldest click is the one dropped");
    }
}

public class ClickMessageTests
{
    [Fact]
    public void Serialize_ThenDeserialize_RoundTripsEveryField()
    {
        var click = new ClickEvent(Guid.NewGuid(), "abc1234", new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero), "ref.example", "Chrome", "ABCDEF");

        var back = ClickMessage.TryDeserialize(ClickMessage.Serialize(click));

        back.Should().BeEquivalentTo(click);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"v\":2,\"id\":\"8f6a0c1e-0000-0000-0000-000000000001\",\"code\":\"abc1234\",\"userAgentFamily\":\"Chrome\"}")]
    [InlineData("{\"v\":1,\"id\":\"00000000-0000-0000-0000-000000000000\",\"code\":\"abc1234\",\"userAgentFamily\":\"Chrome\"}")]
    public void TryDeserialize_PoisonOrUnknownVersion_ReturnsNull(string payload) =>
        ClickMessage.TryDeserialize(System.Text.Encoding.UTF8.GetBytes(payload)).Should().BeNull();
}

public class DistributedLinkCacheTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));

    private DistributedLinkCache Create(IDistributedCache cache) =>
        new(cache, _clock, Options.Create(new CacheOptions { TtlSeconds = 300, OperationTimeoutMs = 100 }), NullLogger<DistributedLinkCache>.Instance);

    [Fact]
    public async Task SetAsync_LinkExpiringSoonerThanTtl_TtlCappedAtExpiry()
    {
        var spy = new SpyCache();
        await Create(spy).SetAsync(new LinkSnapshot("abc1234", "https://x.example/", _clock.GetUtcNow().AddSeconds(30)), default);

        spy.LastOptions!.AbsoluteExpirationRelativeToNow.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task SetAsync_NoExpiry_UsesConfiguredTtl()
    {
        var spy = new SpyCache();
        await Create(spy).SetAsync(new LinkSnapshot("abc1234", "https://x.example/", null), default);

        spy.LastOptions!.AbsoluteExpirationRelativeToNow.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task SetAsync_AlreadyExpired_IsNotCached()
    {
        var spy = new SpyCache();
        await Create(spy).SetAsync(new LinkSnapshot("abc1234", "https://x.example/", _clock.GetUtcNow().AddSeconds(-1)), default);

        spy.LastOptions.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_RoundTrip_ReturnsSnapshot()
    {
        var cache = Create(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));
        var snapshot = new LinkSnapshot("abc1234", "https://x.example/", _clock.GetUtcNow().AddHours(1));

        await cache.SetAsync(snapshot, default);

        (await cache.GetAsync("abc1234", default)).Should().Be(snapshot);
    }

    [Fact]
    public async Task AllOperations_CacheThrows_DegradeSilently()
    {
        var cache = Create(new FailingCache());

        (await cache.GetAsync("abc1234", default)).Should().BeNull();
        await cache.Invoking(c => c.SetAsync(new LinkSnapshot("abc1234", "https://x.example/", null), default)).Should().NotThrowAsync();
        await cache.Invoking(c => c.RemoveAsync("abc1234", default)).Should().NotThrowAsync();
    }

    [Fact]
    public async Task GetAsync_CacheHangs_TimesOutAndReturnsNull()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();

        (await Create(new HangingCache()).GetAsync("abc1234", default)).Should().BeNull();

        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2), "a hung cache must not hang the redirect");
    }

    [Fact]
    public async Task GetAsync_CacheKeepsFailing_CircuitOpensAndStopsCallingRedis()
    {
        var failing = new FailingCache();
        var cache = Create(failing);

        for (var i = 0; i < 50; i++)
        {
            (await cache.GetAsync("abc1234", default)).Should().BeNull();
        }

        cache.IsCircuitOpen.Should().BeTrue();
        failing.Calls.Should().BeLessThanOrEqualTo(10, "after the breaker opens, Redis is not called (no per-request timeout or log)");
    }

    [Fact]
    public async Task GetAsync_AfterBreakDuration_ProbesRedisAgain()
    {
        var failing = new FailingCache();
        var cache = Create(failing);
        for (var i = 0; i < 20; i++)
        {
            await cache.GetAsync("abc1234", default);
        }

        var callsWhileOpen = failing.Calls;
        _clock.Advance(TimeSpan.FromSeconds(16));
        await cache.GetAsync("abc1234", default);

        failing.Calls.Should().Be(callsWhileOpen + 1, "a half-open probe is allowed after the break duration");
    }

    private sealed class SpyCache : IDistributedCache
    {
        public DistributedCacheEntryOptions? LastOptions { get; private set; }
        public byte[]? Get(string key) => null;
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult<byte[]?>(null);
        public void Refresh(string key) { }
        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
        public void Remove(string key) { }
        public Task RemoveAsync(string key, CancellationToken token = default) => Task.CompletedTask;
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => LastOptions = options;

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            LastOptions = options;
            return Task.CompletedTask;
        }
    }

    private sealed class FailingCache : IDistributedCache
    {
        private int _calls;
        public int Calls => _calls;
        private InvalidOperationException Down
        {
            get
            {
                Interlocked.Increment(ref _calls);
                return new InvalidOperationException("Simulated Redis outage");
            }
        }

        public byte[]? Get(string key) => throw Down;
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => throw Down;
        public void Refresh(string key) => throw Down;
        public Task RefreshAsync(string key, CancellationToken token = default) => throw Down;
        public void Remove(string key) => throw Down;
        public Task RemoveAsync(string key, CancellationToken token = default) => throw Down;
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw Down;
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => throw Down;
    }

    private sealed class HangingCache : IDistributedCache
    {
        private static readonly TaskCompletionSource<byte[]?> Never = new();
        public byte[]? Get(string key) => null;
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Never.Task;
        public void Refresh(string key) { }
        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
        public void Remove(string key) { }
        public Task RemoveAsync(string key, CancellationToken token = default) => Task.CompletedTask;
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) { }
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => Task.CompletedTask;
    }
}

public class DatabaseOutageTests
{
    [Fact]
    public async Task GetByCodeAsync_DatabaseUnreachable_ThrowsDependencyUnavailable()
    {
        await using var db = new SqliteLinkDbContext(new DbContextOptionsBuilder<SqliteLinkDbContext>()
            .UseSqlite("Data Source=/nonexistent-dir/unreachable.db;Mode=ReadOnly").Options);
        var repo = new ShortLinkRepository(db);

        var act = () => repo.GetByCodeAsync("abc1234", default);

        (await act.Should().ThrowAsync<UrlShortener.Application.Common.DependencyUnavailableException>())
            .Which.InnerException.Should().BeOfType<SqliteException>();
    }
}

/// <summary>Idempotent ingestion against a real SQLite database: the guarantee at-least-once delivery relies on.</summary>
public sealed class EfClickIngestorTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ingestor-{Guid.NewGuid():N}.db");
    private SqliteLinkDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _db = new SqliteLinkDbContext(new DbContextOptionsBuilder<SqliteLinkDbContext>().UseSqlite($"Data Source={_path}").Options);
        await _db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    [Fact]
    public async Task IngestAsync_SameBatchDeliveredTwice_CountsEachClickOnce()
    {
        var ingestor = new EfClickIngestor(_db);
        var day = new DateTimeOffset(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);
        var batch = Enumerable.Range(0, 10).Select(i => new ClickEvent(Guid.NewGuid(), "abc1234", day.AddMinutes(i), null, "Other", null)).ToList();

        var first = await ingestor.IngestAsync(batch, default);
        var redelivered = await ingestor.IngestAsync(batch, default);

        first.Should().Be(10);
        redelivered.Should().Be(0);
        (await _db.ClickEvents.CountAsync()).Should().Be(10);
        (await _db.DailyStats.SingleAsync()).Clicks.Should().Be(10);
    }

    [Fact]
    public async Task IngestAsync_PartiallyOverlappingBatches_AggregatesExact()
    {
        var ingestor = new EfClickIngestor(_db);
        var t = new DateTimeOffset(2026, 10, 11, 23, 59, 0, TimeSpan.Zero);
        var a = new ClickEvent(Guid.NewGuid(), "abc1234", t, null, "Other", null);
        var b = new ClickEvent(Guid.NewGuid(), "abc1234", t.AddMinutes(2), null, "Other", null); // next UTC day
        var c = new ClickEvent(Guid.NewGuid(), "xyz9876", t, null, "Other", null);

        await ingestor.IngestAsync([a, b], default);
        await ingestor.IngestAsync([b, c, c], default); // redelivered b, duplicate c within batch

        var stats = await _db.DailyStats.OrderBy(x => x.Code).ThenBy(x => x.Day).ToListAsync();
        stats.Select(x => (x.Code, x.Day.ToString("yyyy-MM-dd"), x.Clicks)).Should().Equal(
            ("abc1234", "2026-10-11", 1L), ("abc1234", "2026-10-12", 1L), ("xyz9876", "2026-10-11", 1L));
    }
}

public class LogThrottleTests
{
    [Fact]
    public void ShouldLog_BurstWithinInterval_LogsOnceAndCountsTheRest()
    {
        var throttle = new UrlShortener.Infrastructure.Observability.LogThrottle(TimeSpan.FromHours(1));

        var logged = Enumerable.Range(0, 1000).Count(_ => throttle.ShouldLog(out long _));

        logged.Should().Be(1);
    }
}
