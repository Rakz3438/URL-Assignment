using System.ComponentModel.DataAnnotations;

namespace UrlShortener.Application;

/// <summary>Bound from configuration section "Shortener" (environment variables: Shortener__PublicBaseUrl, ...).</summary>
public sealed class ShortenerOptions
{
    public const string SectionName = "Shortener";

    /// <summary>Base URL used to build short URLs and to block self-referencing targets.</summary>
    [Required, Url]
    public string PublicBaseUrl { get; set; } = "http://localhost:5080";

    /// <summary>Salt for visitor hashing (decision A-6). Must be overridden outside development.</summary>
    [Required, MinLength(16)]
    public string VisitorHashSalt { get; set; } = "dev-only-salt-change-me";

    [Range(1, 10)]
    public int MaxCodeGenerationAttempts { get; set; } = 5;

    [Range(1, 365)]
    public int StatsWindowDays { get; set; } = 30;
}
