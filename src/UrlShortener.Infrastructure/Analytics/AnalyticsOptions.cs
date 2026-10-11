using System.ComponentModel.DataAnnotations;

namespace UrlShortener.Infrastructure.Analytics;

internal enum AnalyticsMode
{
    /// <summary>Zero-setup mode: the API's background publisher writes clicks straight to the database.</summary>
    Direct,

    /// <summary>Full mode: the API publishes to Kafka; the Analytics Worker consumes and writes.</summary>
    Kafka,
}

/// <summary>Section "Analytics" (env: Analytics__Mode, Analytics__BufferCapacity, ...).</summary>
internal sealed class AnalyticsOptions
{
    public const string SectionName = "Analytics";

    public AnalyticsMode Mode { get; set; } = AnalyticsMode.Direct;

    /// <summary>Clicks held in memory awaiting publish. When full, the oldest click is dropped and counted.</summary>
    [Range(100, 1_000_000)]
    public int BufferCapacity { get; set; } = 10_000;

    [Range(1, 10_000)]
    public int BatchSize { get; set; } = 500;

    /// <summary>How long the publisher waits for a batch to fill. Bounds added analytics latency.</summary>
    [Range(1, 10_000)]
    public int BatchWindowMs { get; set; } = 200;
}

/// <summary>Section "Kafka" (env: Kafka__BootstrapServers, ...).</summary>
internal sealed class KafkaOptions
{
    public const string SectionName = "Kafka";

    public string BootstrapServers { get; set; } = "localhost:9092";

    public string Topic { get; set; } = "link-clicks";

    public string ConsumerGroup { get; set; } = "analytics-worker";

    /// <summary>How long the producer keeps retrying a click while Kafka is unreachable (the approved
    /// "retry window", default 5 min). After that the click is reported as failed and counted.</summary>
    [Range(1_000, 3_600_000)]
    public int MessageTimeoutMs { get; set; } = 300_000;

    /// <summary>Clicks the Kafka client may hold while the broker is unreachable. At ~200 bytes per click the
    /// default is ≈100 MB and rides out ≈20 s of outage at 25k clicks/s (the measured local peak), or longer
    /// at real traffic levels. Beyond it, clicks are dropped and counted, never blocking redirects.</summary>
    [Range(1_000, 10_000_000)]
    public int MaxBufferedMessages { get; set; } = 500_000;

    [Range(1_024, 2_097_151)]
    public int MaxBufferedKbytes { get; set; } = 131_072;
}

/// <summary>Section "Cache" (env: Cache__TtlSeconds, ...). Redis is used when ConnectionStrings:Redis is set.</summary>
internal sealed class CacheOptions
{
    public const string SectionName = "Cache";

    [Range(1, 86_400)]
    public int TtlSeconds { get; set; } = 300;

    /// <summary>Upper bound on any single cache call. Past it, the redirect falls back to the database.</summary>
    [Range(1, 10_000)]
    public int OperationTimeoutMs { get; set; } = 100;
}
