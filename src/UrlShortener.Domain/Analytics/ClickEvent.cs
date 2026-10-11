namespace UrlShortener.Domain.Analytics;

/// <summary>One redirect of a short link. Contains no raw IP address (decision A-6):
/// <see cref="VisitorHash"/> is a salted hash used only for approximate unique-visitor counts.</summary>
public sealed class ClickEvent
{
    public const int MaxReferrerLength = 512;

    // Required by EF Core materialization.
    private ClickEvent()
    {
        Code = string.Empty;
        UserAgentFamily = string.Empty;
    }

    public ClickEvent(Guid id, string code, DateTimeOffset occurredAt, string? referrerHost,
        string userAgentFamily, string? visitorHash)
    {
        Id = id;
        Code = code;
        OccurredAt = occurredAt.ToUniversalTime();
        ReferrerHost = referrerHost is { Length: > MaxReferrerLength } ? referrerHost[..MaxReferrerLength] : referrerHost;
        UserAgentFamily = userAgentFamily;
        VisitorHash = visitorHash;
    }

    /// <summary>Unique event ID. Also the idempotency key for at-least-once consumers.</summary>
    public Guid Id { get; private set; }
    public string Code { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>Host only (e.g. "twitter.com"), never the full referrer URL, which can carry tokens.</summary>
    public string? ReferrerHost { get; private set; }
    public string UserAgentFamily { get; private set; }
    public string? VisitorHash { get; private set; }
}
