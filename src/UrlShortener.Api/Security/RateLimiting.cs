using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using UrlShortener.Api.Auth;

namespace UrlShortener.Api.Security;

/// <summary>Section "RateLimiting" (env: RateLimiting__CreatePerMinute, ...). Scope decision S1.</summary>
public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    public bool Enabled { get; set; } = true;

    [Range(1, 1_000_000)]
    public int CreatePerMinute { get; set; } = 30;

    [Range(1, 10_000_000)]
    public int RedirectPerMinute { get; set; } = 300;
}

/// <summary>Per-client rate limits. Limits are per instance (in-memory): with N replicas the effective global limit is
/// up to N× higher. Accepted for this scope; a Redis-backed limiter is the upgrade path (documented trade-off).</summary>
internal static class RateLimiting
{
    public const string CreatePolicy = "create";
    public const string RedirectPolicy = "redirect";

    public static IServiceCollection AddUrlShortenerRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RateLimitingOptions>().Bind(configuration.GetSection(RateLimitingOptions.SectionName))
            .ValidateDataAnnotations().ValidateOnStart();

        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = WriteRejectionAsync;

            // Create: per API key (the key's fingerprint claim, never the key itself), fixed window.
            o.AddPolicy(CreatePolicy, http =>
            {
                var options = Options(http);
                var key = http.User.FindFirst(ApiKeyAuthenticationHandler.KeyIdClaim)?.Value ?? "anonymous";
                return options.Enabled
                    ? RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = options.CreatePerMinute,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    })
                    : RateLimitPartition.GetNoLimiter(key);
            });

            // Redirect: per client IP, sliding window (smooths bursts at window edges). The IP is the real client only
            // when ForwardedHeaders trusts the proxy (URL-18), so a spoofed X-Forwarded-For cannot dodge the limit.
            o.AddPolicy(RedirectPolicy, http =>
            {
                var options = Options(http);
                var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return options.Enabled
                    ? RateLimitPartition.GetSlidingWindowLimiter(ip, _ => new SlidingWindowRateLimiterOptions
                    {
                        PermitLimit = options.RedirectPerMinute,
                        Window = TimeSpan.FromMinutes(1),
                        SegmentsPerWindow = 6,
                        QueueLimit = 0,
                    })
                    : RateLimitPartition.GetNoLimiter(ip);
            });
        });

        return services;
    }

    private static RateLimitingOptions Options(HttpContext http) =>
        http.RequestServices.GetRequiredService<IOptions<RateLimitingOptions>>().Value;

    private static async ValueTask WriteRejectionAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var value) ? value : TimeSpan.FromSeconds(10);
        var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
        context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);

        await context.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context.HttpContext,
            ProblemDetails =
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Too Many Requests",
                Detail = $"Rate limit exceeded. Retry after {seconds} seconds.",
                Extensions = { ["errorCode"] = "rate_limited" },
            },
        });
    }
}
