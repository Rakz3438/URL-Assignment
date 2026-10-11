using Microsoft.Net.Http.Headers;
using UrlShortener.Application.Analytics;
using UrlShortener.Application.Links;

namespace UrlShortener.Api.Endpoints;

internal static class RedirectEndpoints
{
    public static IEndpointRouteBuilder MapRedirectEndpoints(this IEndpointRouteBuilder app)
    {
        // Public, unauthenticated. Literal routes (/api, /health, /swagger) take precedence over this pattern.
        app.MapGet("/{code}", RedirectAsync)
            .RequireRateLimiting(Security.RateLimiting.RedirectPolicy)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .WithName("FollowLink")
            .WithTags("Redirect")
            .WithSummary("Redirect to the link's destination (302)")
            .Produces(StatusCodes.Status302Found)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status410Gone);

        return app;
    }

    private static async Task<IResult> RedirectAsync(string code, HttpContext http, RedirectService service,
        CancellationToken cancellationToken)
    {
        var context = new ClickContext(
            http.Request.Headers.Referer.ToString(),
            http.Request.Headers.UserAgent.ToString(),
            http.Connection.RemoteIpAddress?.ToString());

        var result = await service.ResolveAsync(code, context, cancellationToken);
        if (!result.IsSuccess)
        {
            return result.Error!.ToProblem();
        }

        // 302 + no-store (decision A-2): browsers must come back to us on every click, so deletes,
        // expiry and analytics stay accurate.
        http.Response.Headers[HeaderNames.CacheControl] = "no-store";
        return Results.Redirect(result.Value, permanent: false);
    }
}
