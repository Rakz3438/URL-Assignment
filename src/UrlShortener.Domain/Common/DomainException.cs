namespace UrlShortener.Domain.Common;

/// <summary>Thrown when a domain invariant would be violated. Indicates a programming error
/// or unvalidated input reaching the domain; the API layer validates before this point.</summary>
public sealed class DomainException : Exception
{
    public DomainException(string message) : base(message) { }

    public DomainException() { }

    public DomainException(string message, Exception innerException) : base(message, innerException) { }
}
