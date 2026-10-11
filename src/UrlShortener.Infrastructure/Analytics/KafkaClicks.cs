using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UrlShortener.Application.Abstractions;
using UrlShortener.Domain.Analytics;

namespace UrlShortener.Infrastructure.Analytics;

/// <summary>Wire contract for the "link-clicks" topic. Versioned so the producer and consumer can be deployed
/// independently; consumers must accept unknown future fields (System.Text.Json ignores them by default).</summary>
internal sealed record ClickMessage(int V, Guid Id, string Code, DateTimeOffset OccurredAt, string? ReferrerHost,
    string UserAgentFamily, string? VisitorHash)
{
    public const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static byte[] Serialize(ClickEvent click) => JsonSerializer.SerializeToUtf8Bytes(
        new ClickMessage(CurrentVersion, click.Id, click.Code, click.OccurredAt, click.ReferrerHost, click.UserAgentFamily, click.VisitorHash),
        Json);

    /// <returns><c>null</c> for anything that is not a valid v1 click (poison message).</returns>
    public static ClickEvent? TryDeserialize(byte[]? payload)
    {
        if (payload is null)
        {
            return null;
        }

        try
        {
            var m = JsonSerializer.Deserialize<ClickMessage>(payload, Json);
            return m is { V: CurrentVersion } && m.Id != Guid.Empty && !string.IsNullOrEmpty(m.Code) && m.UserAgentFamily is not null
                ? new ClickEvent(m.Id, m.Code, m.OccurredAt, m.ReferrerHost, m.UserAgentFamily, m.VisitorHash)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Full mode: publishes clicks to Kafka. Keyed by short code so all clicks for a link land on one
/// partition, which serializes aggregate updates per link in the consumer.</summary>
/// <remarks>Fix found by chaos test (Kafka stopped 10 s under load): the first version awaited delivery acks per
/// batch, so during an outage the publisher stopped draining the 10k in-memory buffer and 389,674 of 643,391 clicks
/// were dropped (~0.4 s of outage tolerance instead of the designed retry window). Now each click is handed to
/// librdkafka's own bounded queue without waiting; librdkafka retries in the background until
/// <see cref="KafkaOptions.MessageTimeoutMs"/>. Outage tolerance ≈ <see cref="KafkaOptions.MaxBufferedMessages"/> ÷ click rate.</remarks>
internal sealed partial class KafkaClickBatchPublisher : IClickBatchPublisher, IDisposable
{
    private readonly IProducer<string, byte[]> _producer;
    private readonly string _topic;
    private readonly ClickPipelineMetrics _metrics;
    private readonly ILogger<KafkaClickBatchPublisher> _logger;
    private readonly Action<DeliveryReport<string, byte[]>> _onDelivery;

    public KafkaClickBatchPublisher(IOptions<KafkaOptions> options, ClickPipelineMetrics metrics, ILogger<KafkaClickBatchPublisher> logger)
    {
        var o = options.Value;
        _topic = o.Topic;
        _metrics = metrics;
        _logger = logger;
        _onDelivery = OnDelivery;
        _producer = new ProducerBuilder<string, byte[]>(new ProducerConfig
        {
            BootstrapServers = o.BootstrapServers,
            ClientId = "urlshortener-api",
            Acks = Acks.All,                                  // durable once acknowledged
            EnableIdempotence = true,                         // no broker-side duplicates from producer retries
            LingerMs = 5,
            CompressionType = CompressionType.Lz4,
            MessageTimeoutMs = o.MessageTimeoutMs,            // the approved retry window while Kafka is down
            QueueBufferingMaxMessages = o.MaxBufferedMessages, // outage tolerance, bounded memory
            QueueBufferingMaxKbytes = o.MaxBufferedKbytes,
        }).Build();
    }

    public Task PublishAsync(IReadOnlyCollection<ClickEvent> batch, CancellationToken cancellationToken)
    {
        foreach (var click in batch)
        {
            try
            {
                // Non-blocking: enqueues into librdkafka and returns. Delivery is reported to OnDelivery.
                _producer.Produce(_topic, new Message<string, byte[]> { Key = click.Code, Value = ClickMessage.Serialize(click) }, _onDelivery);
            }
            catch (ProduceException<string, byte[]> ex) when (ex.Error.Code == ErrorCode.Local_QueueFull)
            {
                // Outage outlasted the buffer: drop (counted) instead of blocking the pipeline.
                _metrics.RecordDropped();
            }
        }

        return Task.CompletedTask;
    }

    private void OnDelivery(DeliveryReport<string, byte[]> report)
    {
        if (report.Error.IsError)
        {
            _metrics.RecordPublishFailed(1);
            LogDeliveryFailed(_logger, report.Error.Reason);
        }
        else
        {
            _metrics.RecordDelivered();
        }
    }

    public void Dispose()
    {
        // Give in-flight messages a chance to be delivered on shutdown.
        _producer.Flush(TimeSpan.FromSeconds(5));
        _producer.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Click delivery to Kafka failed after the retry window: {Reason}")]
    private static partial void LogDeliveryFailed(ILogger logger, string reason);
}

/// <summary>Analytics Worker: consumes "link-clicks" and ingests in batches. At-least-once: offsets are committed
/// only after the batch is stored; on failure the consumer seeks back and retries the same batch. Duplicates
/// from redelivery are absorbed by the idempotent ingestor.</summary>
internal sealed partial class KafkaClickConsumerService : BackgroundService
{
    private const int BatchSize = 500;
    private static readonly TimeSpan BatchWindow = TimeSpan.FromMilliseconds(200);

    private readonly IServiceScopeFactory _scopes;
    private readonly ClickPipelineMetrics _metrics;
    private readonly KafkaOptions _options;
    private readonly ILogger<KafkaClickConsumerService> _logger;

    public KafkaClickConsumerService(IServiceScopeFactory scopes, ClickPipelineMetrics metrics,
        IOptions<KafkaOptions> options, ILogger<KafkaClickConsumerService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _scopes = scopes;
        _metrics = metrics;
        _options = options.Value;
        _logger = logger;
    }

    // Consume() is a blocking call, so the loop runs on its own thread rather than inside StartAsync.
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Factory.StartNew(() => RunAsync(stoppingToken), stoppingToken, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        using var consumer = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            GroupId = _options.ConsumerGroup,
            ClientId = "urlshortener-analytics-worker",
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
            AutoOffsetReset = AutoOffsetReset.Earliest,
        }).Build();

        consumer.Subscribe(_options.Topic);
        LogStarted(_logger, _options.Topic, _options.ConsumerGroup);
        var failures = 0;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var batch = ReadBatch(consumer, stoppingToken);
                if (batch.Count == 0)
                {
                    continue;
                }

                var clicks = new List<ClickEvent>(batch.Count);
                foreach (var record in batch)
                {
                    if (ClickMessage.TryDeserialize(record.Message.Value) is { } click)
                    {
                        clicks.Add(click);
                    }
                    else
                    {
                        // Skipping (and committing past) a poison message keeps the partition moving.
                        // A dead-letter topic is the production upgrade; noted as a limitation.
                        _metrics.RecordPoison();
                        LogPoison(_logger, record.TopicPartitionOffset.ToString());
                    }
                }

                try
                {
                    await using (var scope = _scopes.CreateAsyncScope())
                    {
                        var stored = await scope.ServiceProvider.GetRequiredService<IClickIngestor>().IngestAsync(clicks, stoppingToken);
                        _metrics.RecordIngested(stored, clicks.Count);
                    }

                    foreach (var record in batch)
                    {
                        consumer.StoreOffset(record);
                    }

                    consumer.Commit();
                    failures = 0;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures++;
                    LogIngestFailed(_logger, ex, clicks.Count, failures);

                    // Rewind every partition in this batch to its first uncommitted offset, then back off.
                    foreach (var first in batch.GroupBy(r => r.TopicPartition).Select(g => g.MinBy(r => r.Offset.Value)!))
                    {
                        consumer.Seek(first.TopicPartitionOffset);
                    }

                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, failures))), stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            consumer.Close(); // leave the group cleanly so partitions rebalance immediately
        }
    }

    private static List<ConsumeResult<string, byte[]>> ReadBatch(IConsumer<string, byte[]> consumer, CancellationToken stoppingToken)
    {
        var batch = new List<ConsumeResult<string, byte[]>>(BatchSize);
        var first = consumer.Consume(TimeSpan.FromMilliseconds(500));
        if (first is null)
        {
            return batch;
        }

        batch.Add(first);
        var deadline = DateTime.UtcNow + BatchWindow;
        while (batch.Count < BatchSize && !stoppingToken.IsCancellationRequested)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            var next = consumer.Consume(remaining);
            if (next is null)
            {
                break;
            }

            batch.Add(next);
        }

        return batch;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Consuming {Topic} as group {Group}")]
    private static partial void LogStarted(ILogger logger, string topic, string group);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping undeserializable message at {Position}")]
    private static partial void LogPoison(ILogger logger, string position);

    [LoggerMessage(Level = LogLevel.Error, Message = "Ingesting {Count} clicks failed (consecutive failures: {Failures}); will retry the batch")]
    private static partial void LogIngestFailed(ILogger logger, Exception exception, int count, int failures);
}
