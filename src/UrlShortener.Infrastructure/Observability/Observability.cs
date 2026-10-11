using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using UrlShortener.Infrastructure.Analytics;

namespace UrlShortener.Infrastructure.Observability;

/// <summary>Logging, metrics and tracing shared by the API and the Analytics Worker (scope decision R3).</summary>
public static class ObservabilityExtensions
{
    /// <summary>
    /// <list type="bullet">
    /// <item>Logs: one JSON object per line on stdout (12-factor logs; ingestible by Splunk's HTTP Event Collector or a
    /// forwarder as-is) with scopes (correlation ID) and W3C trace/span IDs. <c>Logging__Format=simple</c> for humans;
    /// defaults to simple in Development.</item>
    /// <item>Metrics and traces: OpenTelemetry, exported over OTLP only when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set
    /// (standard OTel env var), so there is no collector dependency in zero-setup mode.</item>
    /// </list>
    /// </summary>
    public static IHostApplicationBuilder AddUrlShortenerObservability(this IHostApplicationBuilder builder, string serviceName,
        Action<TracerProviderBuilder>? tracing = null, Action<MeterProviderBuilder>? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var format = builder.Configuration["Logging:Format"] ?? (builder.Environment.IsDevelopment() ? "simple" : "json");
        builder.Logging.ClearProviders();
        if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
        {
            builder.Logging.AddJsonConsole(o =>
            {
                o.IncludeScopes = true;
                o.UseUtcTimestamp = true;
                o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
            });
        }
        else
        {
            builder.Logging.AddSimpleConsole(o =>
            {
                o.IncludeScopes = true;
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss ";
            });
        }

        builder.Logging.Configure(o => o.ActivityTrackingOptions = ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId);

        var otlp = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        // Measured: exporting 100% of traces cost ~13% throughput at 20k+ req/s. Sample by default outside Development;
        // ParentBased keeps whole traces consistent with upstream sampling decisions.
        var sampleRatio = builder.Configuration.GetValue("Telemetry:TraceSampleRatio", builder.Environment.IsDevelopment() ? 1.0 : 0.1);
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0";

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName, serviceVersion: version))
            .WithTracing(t =>
            {
                t.SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(sampleRatio)));
                t.AddSource("Npgsql"); // Npgsql emits DB spans natively
                tracing?.Invoke(t);
                if (otlp)
                {
                    t.AddOtlpExporter();
                }
            })
            .WithMetrics(m =>
            {
                m.AddMeter(ClickPipelineMetrics.MeterName).AddRuntimeInstrumentation();
                metrics?.Invoke(m);
                if (otlp)
                {
                    m.AddOtlpExporter();
                }
            });

        if (otlp)
        {
            builder.Logging.AddOpenTelemetry(o =>
            {
                o.IncludeScopes = true;
                o.AddOtlpExporter();
            });
        }

        return builder;
    }
}
