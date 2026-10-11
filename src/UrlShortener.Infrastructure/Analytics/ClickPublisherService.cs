using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UrlShortener.Application.Abstractions;
using UrlShortener.Domain.Analytics;

namespace UrlShortener.Infrastructure.Analytics;

/// <summary>Where a batch of clicks goes next: Kafka (full mode) or the database (zero-setup mode).</summary>
internal interface IClickBatchPublisher
{
    Task PublishAsync(IReadOnlyCollection<ClickEvent> batch, CancellationToken cancellationToken);
}

/// <summary>Drains <see cref="ClickChannel"/> in batches, off the request path. Retries a failed batch a few
/// times (duplicates are safe: ingestion is idempotent), then counts it as failed and moves on so one bad batch
/// can't stall the pipeline. On shutdown it flushes what is buffered, bounded by a deadline.</summary>
internal sealed partial class ClickPublisherService : BackgroundService
{
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2)];
    private static readonly TimeSpan ShutdownFlushDeadline = TimeSpan.FromSeconds(5);

    private readonly ClickChannel _channel;
    private readonly IClickBatchPublisher _publisher;
    private readonly ClickPipelineMetrics _metrics;
    private readonly AnalyticsOptions _options;
    private readonly ILogger<ClickPublisherService> _logger;
    private long _droppedAtLastReport;
    private long _lastReportTicks = Environment.TickCount64;

    public ClickPublisherService(ClickChannel channel, IClickBatchPublisher publisher, ClickPipelineMetrics metrics,
        IOptions<AnalyticsOptions> options, ILogger<ClickPublisherService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _channel = channel;
        _publisher = publisher;
        _metrics = metrics;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync(stoppingToken))
            {
                // Let a batch accumulate: bounds analytics latency to ~BatchWindowMs while cutting round-trips.
                await Task.Delay(_options.BatchWindowMs, stoppingToken);
                await DrainAsync(stoppingToken);
                ReportDrops();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down: fall through to the final flush.
        }

        using var deadline = new CancellationTokenSource(ShutdownFlushDeadline);
        try
        {
            await DrainAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            LogShutdownFlushIncomplete(_logger, _channel.Reader.Count);
        }
    }

    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        var batch = new List<ClickEvent>(_options.BatchSize);
        while (true)
        {
            while (batch.Count < _options.BatchSize && _channel.Reader.TryRead(out var click))
            {
                batch.Add(click);
            }

            if (batch.Count == 0)
            {
                return;
            }

            await PublishWithRetryAsync(batch, cancellationToken);
            batch.Clear();
        }
    }

    private async Task PublishWithRetryAsync(List<ClickEvent> batch, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await _publisher.PublishAsync(batch, cancellationToken);
                _metrics.RecordPublished(batch.Count);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt < RetryDelays.Length)
            {
                LogPublishRetry(_logger, ex, batch.Count, attempt + 1);
                await Task.Delay(RetryDelays[attempt], cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _metrics.RecordPublishFailed(batch.Count);
                LogPublishFailed(_logger, ex, batch.Count);
                return;
            }
        }
    }

    /// <summary>Drops are counted in metrics; this also surfaces them in logs (Splunk) at most every 10 s,
    /// because the Kafka-outage chaos test showed metric-only losses go unnoticed until metrics are scraped.</summary>
    private void ReportDrops()
    {
        if (Environment.TickCount64 - _lastReportTicks < 10_000)
        {
            return;
        }

        var dropped = _metrics.Dropped;
        if (dropped > _droppedAtLastReport)
        {
            LogClicksDropped(_logger, dropped - _droppedAtLastReport, dropped);
            _droppedAtLastReport = dropped;
        }

        _lastReportTicks = Environment.TickCount64;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Analytics backpressure: {Dropped} clicks dropped in the last interval ({Total} since start). Redirects are unaffected")]
    private static partial void LogClicksDropped(ILogger logger, long dropped, long total);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Publishing {Count} clicks failed (attempt {Attempt}); retrying")]
    private static partial void LogPublishRetry(ILogger logger, Exception exception, int count, int attempt);

    [LoggerMessage(Level = LogLevel.Error, Message = "Dropping {Count} clicks after exhausting publish retries")]
    private static partial void LogPublishFailed(ILogger logger, Exception exception, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shutdown flush deadline reached with {Remaining} clicks still buffered")]
    private static partial void LogShutdownFlushIncomplete(ILogger logger, int remaining);
}

/// <summary>Zero-setup mode: publish = ingest into the database, one DI scope per batch.</summary>
internal sealed class DirectClickBatchPublisher : IClickBatchPublisher
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ClickPipelineMetrics _metrics;

    public DirectClickBatchPublisher(IServiceScopeFactory scopes, ClickPipelineMetrics metrics)
    {
        _scopes = scopes;
        _metrics = metrics;
    }

    public async Task PublishAsync(IReadOnlyCollection<ClickEvent> batch, CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<IClickIngestor>().IngestAsync(batch, cancellationToken);
        _metrics.RecordIngested(stored, batch.Count);
    }
}
