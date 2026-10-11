using System.Collections.Frozen;

namespace UrlShortener.Domain.Links;

/// <summary>Rules for short codes and custom aliases (decision A-1, story URL-3).</summary>
public static class ShortCode
{
    public const string Base62Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    /// <summary>Generated codes: 62^7 ≈ 3.5 × 10^12 combinations.</summary>
    public const int GeneratedLength = 7;

    public const int AliasMinLength = 4;
    public const int AliasMaxLength = 32;

    /// <summary>Route-level pattern for any code (generated or alias).</summary>
    public const string RoutePattern = "^[A-Za-z0-9_-]{4,32}$";

    /// <summary>Aliases that would collide with service routes or invite impersonation.</summary>
    private static readonly FrozenSet<string> Reserved = new[]
    {
        "api", "admin", "health", "healthz", "ready", "live", "metrics", "swagger",
        "openapi", "docs", "login", "logout", "signup", "static", "assets", "favicon.ico",
        "robots.txt", "status", "support", "help", "about",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static bool IsValidAlias(string? alias, out string? reason)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            reason = "Alias must not be empty.";
            return false;
        }

        if (alias.Length is < AliasMinLength or > AliasMaxLength)
        {
            reason = $"Alias must be between {AliasMinLength} and {AliasMaxLength} characters.";
            return false;
        }

        foreach (var c in alias)
        {
            if (!IsAllowedChar(c))
            {
                reason = "Alias may contain only letters, digits, '-' and '_'.";
                return false;
            }
        }

        if (Reserved.Contains(alias))
        {
            reason = "Alias is reserved.";
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>True for words reserved for service routes. Applies to generated codes too: several reserved
    /// words ("swagger", "healthz", "metrics") are exactly <see cref="GeneratedLength"/> characters long.</summary>
    public static bool IsReserved(string code) => Reserved.Contains(code);

    /// <summary>Cheap pre-check used on the redirect path before touching storage.</summary>
    public static bool IsWellFormed(string? code)
    {
        if (code is null || code.Length is < AliasMinLength or > AliasMaxLength)
        {
            return false;
        }

        foreach (var c in code)
        {
            if (!IsAllowedChar(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAllowedChar(char c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_';
}
