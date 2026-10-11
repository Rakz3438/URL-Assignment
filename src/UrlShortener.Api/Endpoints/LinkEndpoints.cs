using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using UrlShortener.Api.Contracts;
using UrlShortener.Application;
using UrlShortener.Application.Analytics;
using UrlShortener.Application.Links;
using UrlShortener.Domain.Links;

namespace UrlShortener.Api.Endpoints;

internal static class LinkEndpoints
{
    public const string ManagePolicy = "ManageLinks";

    public static IEndpointRouteBuilder MapLinkEndpoints(this IEndpointRouteBuilder app)
    {
        var links = app.MapGroup("/api/v1/links")
            .WithTags("Links")
            .RequireAuthorization(ManagePolicy);

        links.MapPost("/", CreateAsync)
            .WithName("CreateLink")
            .WithSummary("Create a short link")
            .Produces<LinkResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        links.MapGet("/{code}", GetAsync)
            .WithName("GetLink")
            .WithSummary("Get link metadata")
            .Produces<LinkResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        links.MapDelete("/{code}", DeleteAsync)
            .WithName("DeleteLink")
            .WithSummary("Deactivate a link (soft delete)")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        links.MapGet("/{code}/stats", GetStatsAsync)
            .WithName("GetLinkStats")
            .WithSummary("Get click analytics for a link")
            .Produces<LinkStatsResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    public const string ReplayedHeader = "Idempotent-Replayed";

    private static async Task<IResult> CreateAsync(
        CreateLinkRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        HttpContext http,
        LinkService service,
        IOptions<ShortenerOptions> options,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var result = await service.CreateAsync(
            new CreateLinkCommand(request.Url, request.CustomAlias, request.ExpiresAt, idempotencyKey), cancellationToken);

        if (!result.IsSuccess)
        {
            return result.Error!.ToProblem();
        }

        var response = ToResponse(result.Value.Link, options.Value, clock);
        var location = $"/api/v1/links/{response.Code}";

        // A replay returns the identical response (201 + same body), so retrying clients need no special
        // handling. The header lets observability and curious clients tell a replay apart.
        if (result.Value.Replayed)
        {
            http.Response.Headers[ReplayedHeader] = "true";
        }

        return Results.Created(location, response);
    }

    private static async Task<IResult> GetAsync(string code, LinkService service, IOptions<ShortenerOptions> options,
        TimeProvider clock, CancellationToken cancellationToken)
    {
        var result = await service.GetAsync(code, cancellationToken);
        return result.IsSuccess ? Results.Ok(ToResponse(result.Value, options.Value, clock)) : result.Error!.ToProblem();
    }

    private static async Task<IResult> DeleteAsync(string code, LinkService service, CancellationToken cancellationToken)
    {
        var result = await service.DeleteAsync(code, cancellationToken);
        return result.IsSuccess ? Results.NoContent() : result.Error!.ToProblem();
    }

    private static async Task<IResult> GetStatsAsync(string code, StatsService service, CancellationToken cancellationToken)
    {
        var result = await service.GetAsync(code, cancellationToken);
        if (!result.IsSuccess)
        {
            return result.Error!.ToProblem();
        }

        var s = result.Value;
        return Results.Ok(new LinkStatsResponse(s.Code, s.Data.TotalClicks, s.Data.UniqueVisitors, s.WindowStart,
            s.Data.Daily, s.Data.TopReferrers, s.Data.TopUserAgents));
    }

    private static LinkResponse ToResponse(ShortLink link, ShortenerOptions options, TimeProvider clock) =>
        new(link.Code,
            $"{options.PublicBaseUrl.TrimEnd('/')}/{link.Code}",
            link.TargetUrl,
            link.IsCustomAlias,
            link.GetStatus(clock.GetUtcNow()).ToString().ToLowerInvariant(),
            link.CreatedAt,
            link.ExpiresAt);
}
