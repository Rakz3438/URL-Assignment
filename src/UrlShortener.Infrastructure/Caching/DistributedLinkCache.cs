using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using UrlShortener.Application.Abstractions;
using UrlShortener.Infrastructure.Analytics;
using UrlShortener.Infrastructure.Observability;

namespace UrlShortener.Infrastructure.Caching;

/// <summary>Exposes cache health to readiness checks without leaking Polly types.</summary>
internal interface ICacheHealth
{
    bool IsCircuitOpen { get; }
}

/// <summary>Cache-aside store for redirect lookups over <see cref="IDistributedCache"/> (Redis in full mode,
/// in-memory in zero-setup mode). Honors the <see cref="ILinkCache"/> contract: every call is bounded by
/// <see cref="CacheOptions.OperationTimeoutMs"/> and failures degrade to a cache miss. They are never thrown.</summary>
/// <remarks>URL-16: a circuit breaker wraps every call. The Redis chaos test (Scenario 2) showed that without it, an
/// outage costs up to 100 ms per request and logs one warning per request (2,500 in 10 s). With the breaker open,
/// calls skip Redis instantly and silently; only state changes are logged.</remarks>
internal sealed partial class DistributedLinkCache : ILinkCache, ICacheHealth
{
    private const string KeyPrefix = "link:v1:";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IDistributedCache _cache;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _ttl;
    private readonly TimeSpan _timeout;
    private readonly ILogger<DistributedLinkCache> _logger;
    private readonly ResiliencePipeline _pipeline;
    private readonly CircuitBreakerStateProvider _breakerState = new();
    private readonly LogThrottle _failureLog = new(TimeSpan.FromSeconds(10));

    public DistributedLinkCache(IDistributedCache cache, TimeProvider clock, IOptions<CacheOptions> options,
        ILogger<DistributedLinkCache> logger)
    {
        _cache = cache;
        _clock = clock;
        _ttl = TimeSpan.FromSeconds(options.Value.TtlSeconds);
        _timeout = TimeSpan.FromMilliseconds(options.Value.OperationTimeoutMs);
        _logger = logger;

        _pipeline = new ResiliencePipelineBuilder { TimeProvider = clock }
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                // Open when at least half of ≥10 calls in a 2 s window fail; probe again after the break.
                // Window tuned by chaos test: with 10 s, the ~250k successful calls made just before an outage (at 25k
                // req/s) kept the failure ratio under 50% for ~9 s while every request paid two 100 ms timeouts.
                FailureRatio = 0.5,
                MinimumThroughput = 10,
                SamplingDuration = TimeSpan.FromSeconds(2),
                BreakDuration = TimeSpan.FromSeconds(options.Value.BreakDurationSeconds),
                ShouldHandle = new PredicateBuilder().Handle<Exception>(ex => ex is not OperationCanceledException),
                StateProvider = _breakerState,
                OnOpened = args =>
                {
                    LogCircuitOpened(_logger, args.Outcome.Exception, args.BreakDuration.TotalSeconds);
                    return default;
                },
                OnClosed = _ =>
                {
                    LogCircuitClosed(_logger);
                    return default;
                },
            })
            .Build();
    }

    public bool IsCircuitOpen => _breakerState.CircuitState is CircuitState.Open or CircuitState.Isolated;

    public async Task<LinkSnapshot?> GetAsync(string code, CancellationToken cancellationToken)
    {
        var bytes = await ExecuteAsync(
            async ct => await _cache.GetAsync(KeyPrefix + code, ct).WaitAsync(_timeout, ct), "get", cancellationToken);
        return bytes is null ? null : JsonSerializer.Deserialize<LinkSnapshot>(bytes, Json);
    }

    public async Task SetAsync(LinkSnapshot link, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(link);

        // An entry must never outlive the link's own expiry (approved design: TTL ≤ min(5 min, expiry)).
        var ttl = _ttl;
        if (link.ExpiresAt is { } expiresAt)
        {
            var untilExpiry = expiresAt - _clock.GetUtcNow();
            if (untilExpiry <= TimeSpan.Zero)
            {
                return;
            }

            ttl = untilExpiry < ttl ? untilExpiry : ttl;
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(link, Json);
        await ExecuteAsync<object?>(async ct =>
        {
            await _cache.SetAsync(KeyPrefix + link.Code, bytes,
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl }, ct).WaitAsync(_timeout, ct);
            return null;
        }, "set", cancellationToken);
    }

    public async Task RemoveAsync(string code, CancellationToken cancellationToken)
    {
        try
        {
            // Invalidation bypasses the breaker's silent skip: a missed delete must always be visible.
            await _cache.RemoveAsync(KeyPrefix + code, cancellationToken).WaitAsync(_timeout, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Accepted risk: the stale entry lives until its TTL (≤ 5 min). Logged at Error so it is alertable.
            LogInvalidationFailure(_logger, ex, code);
        }
    }

    private async Task<T?> ExecuteAsync<T>(Func<CancellationToken, ValueTask<T>> operation, string name, CancellationToken cancellationToken)
    {
        try
        {
            return await _pipeline.ExecuteAsync(operation, cancellationToken);
        }
        catch (BrokenCircuitException)
        {
            return default; // circuit open: skip the cache instantly, without per-request logging
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            if (_failureLog.ShouldLog(out var suppressed))
            {
                LogCacheFailure(_logger, ex, name, suppressed);
            }

            return default;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cache {Operation} failed; falling back to the database ({Suppressed} similar failures since last report)")]
    private static partial void LogCacheFailure(ILogger logger, Exception exception, string operation, long suppressed);

    [LoggerMessage(Level = LogLevel.Error, Message = "Cache circuit OPEN for {BreakSeconds}s: skipping Redis, serving from the database")]
    private static partial void LogCircuitOpened(ILogger logger, Exception? exception, double breakSeconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cache circuit CLOSED: Redis is healthy again")]
    private static partial void LogCircuitClosed(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Cache invalidation failed for {Code}; a deleted link may redirect until its cache TTL expires")]
    private static partial void LogInvalidationFailure(ILogger logger, Exception exception, string code);
}
