using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using UrlShortener.Application.Abstractions;
using UrlShortener.Domain.Analytics;

namespace UrlShortener.Infrastructure.Analytics;

/// <summary>Click pipeline metrics. Losses must be observable, not silent (approved design, Option C).
/// Exported via OpenTelemetry in URL-18; readable today with `dotnet-counters monitor -n UrlShortener.Api`.</summary>
internal sealed class ClickPipelineMetrics
{
    public const string MeterName = "UrlShortener.Analytics";

    private readonly Counter<long> _enqueued;
    private readonly Counter<long> _dropped;
    private readonly Counter<long> _published;
    private readonly Counter<long> _delivered;
    private readonly Counter<long> _publishFailed;
    private readonly Counter<long> _ingested;
    private readonly Counter<long> _duplicates;
    private readonly Counter<long> _poison;

    public ClickPipelineMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        Meter = meterFactory.Create(MeterName);
        _enqueued = Meter.CreateCounter<long>("clicks.enqueued", description: "Clicks accepted into the in-memory buffer");
        _dropped = Meter.CreateCounter<long>("clicks.dropped", description: "Clicks dropped because the buffer was full");
        _published = Meter.CreateCounter<long>("clicks.published", description: "Clicks handed to Kafka or the database");
        _delivered = Meter.CreateCounter<long>("clicks.delivered", description: "Clicks acknowledged by Kafka");
        _publishFailed = Meter.CreateCounter<long>("clicks.publish_failed", description: "Clicks lost after publish retries");
        _ingested = Meter.CreateCounter<long>("clicks.ingested", description: "Clicks newly stored by ingestion");
        _duplicates = Meter.CreateCounter<long>("clicks.duplicates", description: "Redelivered clicks ignored by idempotent ingestion");
        _poison = Meter.CreateCounter<long>("clicks.poison", description: "Undeserializable messages skipped by the consumer");
    }

    public Meter Meter { get; }

    // Read-back totals for tests and logs; counters themselves are write-only.
    public long Enqueued => Interlocked.Read(ref _enqueuedTotal);
    public long Dropped => Interlocked.Read(ref _droppedTotal);

    private long _enqueuedTotal;
    private long _droppedTotal;

    public void RecordEnqueued()
    {
        Interlocked.Increment(ref _enqueuedTotal);
        _enqueued.Add(1);
    }

    public void RecordDropped()
    {
        Interlocked.Increment(ref _droppedTotal);
        _dropped.Add(1);
    }

    public void RecordPublished(int count) => _published.Add(count);
    public void RecordDelivered() => _delivered.Add(1);
    public void RecordPublishFailed(int count) => _publishFailed.Add(count);

    public void RecordIngested(int stored, int received)
    {
        _ingested.Add(stored);
        _duplicates.Add(received - stored);
    }

    public void RecordPoison() => _poison.Add(1);
}

/// <summary>Bounded in-memory buffer between the redirect path and the publisher (Option C).
/// <see cref="TryEnqueue"/> is O(1), lock-free for writers, and never blocks or throws.</summary>
internal sealed class ClickChannel : IClickSink
{
    private readonly Channel<ClickEvent> _channel;
    private readonly ClickPipelineMetrics _metrics;

    public ClickChannel(IOptions<AnalyticsOptions> options, ClickPipelineMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(options);
        _metrics = metrics;
        _channel = Channel.CreateBounded<ClickEvent>(
            new BoundedChannelOptions(options.Value.BufferCapacity)
            {
                // Under sustained overload keep the newest clicks; the drop callback makes every loss visible.
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            },
            _ => _metrics.RecordDropped());

        metrics.Meter.CreateObservableGauge("clicks.buffer_depth", () => _channel.Reader.Count,
            description: "Clicks waiting in the in-memory buffer");
    }

    public ChannelReader<ClickEvent> Reader => _channel.Reader;

    public bool TryEnqueue(ClickEvent click)
    {
        if (_channel.Writer.TryWrite(click))
        {
            _metrics.RecordEnqueued();
            return true;
        }

        // Only reachable after shutdown has completed the writer.
        _metrics.RecordDropped();
        return false;
    }
}
