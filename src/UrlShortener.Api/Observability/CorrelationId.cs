using System.Diagnostics;

namespace UrlShortener.Api.Observability;

/// <summary>Correlation ID for every request (scope decision R3): taken from <c>X-Correlation-ID</c> when the caller sends
/// a well-formed one, otherwise the W3C trace ID. Echoed in the response and attached to every log line as a scope, so
/// one Splunk search finds everything a request did, across the API and its traces.</summary>
internal static class CorrelationId
{
    public const string HeaderName = "X-Correlation-ID";
    private const int MaxLength = 64;

    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var incoming = context.Request.Headers[HeaderName].ToString();
            var id = IsWellFormed(incoming)
                ? incoming
                : Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;

            context.Response.Headers[HeaderName] = id;
            Activity.Current?.SetTag("correlation.id", id);

            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("UrlShortener.Request");
            using (logger.BeginScope("CorrelationId:{CorrelationId}", id))
            {
                await next(context);
            }
        });

    /// <summary>Client-supplied values are untrusted: bounded length and a safe charset prevent log injection.</summary>
    internal static bool IsWellFormed(string value) =>
        value.Length is > 0 and <= MaxLength && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
