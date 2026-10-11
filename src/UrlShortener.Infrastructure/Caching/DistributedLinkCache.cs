using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UrlShortener.Application.Abstractions;
using UrlShortener.Infrastructure.Analytics;

namespace UrlShortener.Infrastructure.Caching;

/// <summary>Cache-aside store for redirect lookups over <see cref="IDistributedCache"/> (Redis in full mode,
/// in-memory in zero-setup mode). Honors the <see cref="ILinkCache"/> contract: every call is bounded by
/// <see cref="CacheOptions.OperationTimeoutMs"/> and failures degrade to a cache miss. They are never thrown.</summary>
internal sealed partial class DistributedLinkCache : ILinkCache
{
    private const string KeyPrefix = "link:v1:";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IDistributedCache _cache;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _ttl;
    private readonly TimeSpan _timeout;
    private readonly ILogger<DistributedLinkCache> _logger;

    public DistributedLinkCache(IDistributedCache cache, TimeProvider clock, IOptions<CacheOptions> options,
        ILogger<DistributedLinkCache> logger)
    {
        _cache = cache;
        _clock = clock;
        _ttl = TimeSpan.FromSeconds(options.Value.TtlSeconds);
        _timeout = TimeSpan.FromMilliseconds(options.Value.OperationTimeoutMs);
        _logger = logger;
    }

    public async Task<LinkSnapshot?> GetAsync(string code, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await _cache.GetAsync(KeyPrefix + code, cancellationToken).WaitAsync(_timeout, cancellationToken);
            return bytes is null ? null : JsonSerializer.Deserialize<LinkSnapshot>(bytes, Json);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            LogCacheFailure(_logger, ex, "get");
            return null;
        }
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

        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(link, Json);
            await _cache.SetAsync(KeyPrefix + link.Code, bytes,
                    new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl }, cancellationToken)
                .WaitAsync(_timeout, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            LogCacheFailure(_logger, ex, "set");
        }
    }

    public async Task RemoveAsync(string code, CancellationToken cancellationToken)
    {
        try
        {
            await _cache.RemoveAsync(KeyPrefix + code, cancellationToken).WaitAsync(_timeout, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Accepted risk: the stale entry lives until its TTL (≤ 5 min). Logged at Error so it is alertable.
            LogInvalidationFailure(_logger, ex, code);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cache {Operation} failed; falling back to the database")]
    private static partial void LogCacheFailure(ILogger logger, Exception exception, string operation);

    [LoggerMessage(Level = LogLevel.Error, Message = "Cache invalidation failed for {Code}; a deleted link may redirect until its cache TTL expires")]
    private static partial void LogInvalidationFailure(ILogger logger, Exception exception, string code);
}
