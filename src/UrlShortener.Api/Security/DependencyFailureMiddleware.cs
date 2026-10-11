using System.Globalization;
using UrlShortener.Application.Common;

namespace UrlShortener.Api.Security;

/// <summary>Turns <see cref="DependencyUnavailableException"/> into 503 + Retry-After (scope decision R1).
/// Sits inside the global exception handler so outages never surface as a generic 500, and logs at most one
/// line per interval so an outage cannot flood Splunk (the lesson from the Redis chaos test).</summary>
internal sealed partial class DependencyFailureMiddleware
{
    private const int RetryAfterSeconds = 5;
    private static readonly long ReportIntervalMs = 10_000;

    private readonly RequestDelegate _next;
    private readonly ILogger<DependencyFailureMiddleware> _logger;
    private long _lastReport = -ReportIntervalMs;
    private long _suppressed;

    public DependencyFailureMiddleware(RequestDelegate next, ILogger<DependencyFailureMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, IProblemDetailsService problems)
    {
        try
        {
            await _next(context);
        }
        catch (DependencyUnavailableException ex) when (!context.Response.HasStarted)
        {
            Report(ex);
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.RetryAfter = RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            await problems.WriteAsync(new ProblemDetailsContext
            {
                HttpContext = context,
                ProblemDetails =
                {
                    Status = StatusCodes.Status503ServiceUnavailable,
                    Title = "Service Unavailable",
                    Detail = "A required backing service is temporarily unavailable. Please retry.",
                    Extensions = { ["errorCode"] = "dependency_unavailable" },
                },
            });
        }
    }

    private void Report(DependencyUnavailableException ex)
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastReport);
        if (now - last >= ReportIntervalMs && Interlocked.CompareExchange(ref _lastReport, now, last) == last)
        {
            LogUnavailable(_logger, ex.InnerException, ex.Dependency, Interlocked.Exchange(ref _suppressed, 0));
        }
        else
        {
            Interlocked.Increment(ref _suppressed);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "{Dependency} unavailable: answering 503 ({Suppressed} similar failures since last report)")]
    private static partial void LogUnavailable(ILogger logger, Exception? exception, string dependency, long suppressed);
}
