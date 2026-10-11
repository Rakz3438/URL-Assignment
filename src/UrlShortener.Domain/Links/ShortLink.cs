using UrlShortener.Domain.Common;

namespace UrlShortener.Domain.Links;

public enum LinkStatus
{
    Active,
    Expired,
    Deleted,
}

/// <summary>Aggregate root for a shortened link. Target URL is immutable after creation:
/// allowing edits would enable "bait-and-switch" phishing (see requirements §7).</summary>
public sealed class ShortLink
{
    public const int MaxTargetUrlLength = 2048;

    // Required by EF Core materialization.
    private ShortLink()
    {
        Code = string.Empty;
        TargetUrl = string.Empty;
    }

    private ShortLink(Guid id, string code, string targetUrl, bool isCustomAlias,
        DateTimeOffset createdAt, DateTimeOffset? expiresAt, string? idempotencyKey, string? requestHash)
    {
        Id = id;
        Code = code;
        TargetUrl = targetUrl;
        IsCustomAlias = isCustomAlias;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        IdempotencyKey = idempotencyKey;
        RequestHash = requestHash;
    }

    public Guid Id { get; private set; }
    public string Code { get; private set; }
    public string TargetUrl { get; private set; }
    public bool IsCustomAlias { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }

    /// <summary>Client-supplied key for safe retries (FR-8). Unique when present.</summary>
    public string? IdempotencyKey { get; private set; }

    /// <summary>Hash of the create request, to detect a reused key with a different body.</summary>
    public string? RequestHash { get; private set; }

    public static ShortLink Create(string code, string targetUrl, bool isCustomAlias,
        DateTimeOffset now, DateTimeOffset? expiresAt, string? idempotencyKey = null, string? requestHash = null)
    {
        if (!ShortCode.IsWellFormed(code))
        {
            throw new DomainException("Short code is not well-formed.");
        }

        if (string.IsNullOrWhiteSpace(targetUrl) || targetUrl.Length > MaxTargetUrlLength)
        {
            throw new DomainException("Target URL is empty or too long.");
        }

        if (expiresAt is not null && expiresAt <= now)
        {
            throw new DomainException("Expiry must be in the future.");
        }

        // Always store UTC: keeps comparisons and PostgreSQL timestamptz consistent regardless of client offset.
        return new ShortLink(Guid.NewGuid(), code, targetUrl, isCustomAlias, now.ToUniversalTime(),
            expiresAt?.ToUniversalTime(), idempotencyKey, requestHash);
    }

    public LinkStatus GetStatus(DateTimeOffset now)
    {
        if (DeletedAt is not null)
        {
            return LinkStatus.Deleted;
        }

        return ExpiresAt is not null && ExpiresAt <= now ? LinkStatus.Expired : LinkStatus.Active;
    }

    /// <summary>Soft delete. Idempotent: deleting twice keeps the original timestamp.</summary>
    public void Delete(DateTimeOffset now) => DeletedAt ??= now.ToUniversalTime();
}
