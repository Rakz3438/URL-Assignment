namespace UrlShortener.Application.Common;

/// <summary>Expected failure categories. The API maps each to one HTTP status in one place.</summary>
public enum ErrorType
{
    Validation,
    NotFound,
    Conflict,
    Gone,
    Unprocessable,
}

public sealed record Error(ErrorType Type, string Code, string Message)
{
    public static Error Validation(string code, string message) => new(ErrorType.Validation, code, message);
    public static Error NotFound(string message = "Short link not found.") => new(ErrorType.NotFound, "link.not_found", message);
    public static Error Conflict(string code, string message) => new(ErrorType.Conflict, code, message);
    public static Error Gone(string message) => new(ErrorType.Gone, "link.expired", message);
    public static Error Unprocessable(string code, string message) => new(ErrorType.Unprocessable, code, message);
}

/// <summary>Result type for expected outcomes; exceptions are reserved for unexpected failures.</summary>
public sealed class Result<T>
{
    private readonly T? _value;

    private Result(T value)
    {
        _value = value;
        IsSuccess = true;
    }

    private Result(Error error)
    {
        Error = error;
    }

    public bool IsSuccess { get; }
    public Error? Error { get; }

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("Cannot read the value of a failed result.");

    public static Result<T> Success(T value) => new(value);
    public static Result<T> Failure(Error error) => new(error);

    public static implicit operator Result<T>(T value) => Success(value);
    public static implicit operator Result<T>(Error error) => Failure(error);
}
