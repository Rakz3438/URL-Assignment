using UrlShortener.Application.Common;

namespace UrlShortener.Api.Endpoints;

/// <summary>The single place where application errors become HTTP responses (RFC 7807 ProblemDetails).</summary>
internal static class ErrorMapping
{
    public static IResult ToProblem(this Error error)
    {
        var status = error.Type switch
        {
            ErrorType.Validation => StatusCodes.Status400BadRequest,
            ErrorType.NotFound => StatusCodes.Status404NotFound,
            ErrorType.Conflict => StatusCodes.Status409Conflict,
            ErrorType.Gone => StatusCodes.Status410Gone,
            ErrorType.Unprocessable => StatusCodes.Status422UnprocessableEntity,
            _ => StatusCodes.Status500InternalServerError,
        };

        return Results.Problem(
            statusCode: status,
            detail: error.Message,
            extensions: new Dictionary<string, object?> { ["errorCode"] = error.Code });
    }
}
