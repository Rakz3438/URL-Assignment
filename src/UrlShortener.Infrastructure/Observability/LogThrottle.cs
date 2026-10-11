namespace UrlShortener.Infrastructure.Observability;

/// <summary>Allows one log line per interval and counts what it suppressed. Outages must be visible but must not flood
/// Splunk: chaos tests measured 2,500 (no breaker) and 850 (breaker, pre-open) cache warnings for one 10 s Redis outage.</summary>
internal sealed class LogThrottle(TimeSpan interval)
{
    private readonly long _intervalMs = (long)interval.TotalMilliseconds;
    private long _last = long.MinValue / 2;
    private long _suppressed;

    /// <returns><c>true</c> if the caller should log now; <paramref name="suppressed"/> = lines skipped since last time.</returns>
    public bool ShouldLog(out long suppressed)
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _last);
        if (now - last >= _intervalMs && Interlocked.CompareExchange(ref _last, now, last) == last)
        {
            suppressed = Interlocked.Exchange(ref _suppressed, 0);
            return true;
        }

        Interlocked.Increment(ref _suppressed);
        suppressed = 0;
        return false;
    }
}
