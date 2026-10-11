namespace UrlShortener.Api.Security;

/// <summary>Defensive HTTP headers on every response (scope decision S4). Cheap, and they close clickjacking,
/// MIME-sniffing and referrer-leak vectors on any HTML we might return (e.g. Swagger UI, error pages).</summary>
internal static class SecurityHeaders
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            await next(context);
        });
}
