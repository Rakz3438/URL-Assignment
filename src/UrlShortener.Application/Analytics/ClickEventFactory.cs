using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using UrlShortener.Domain.Analytics;

namespace UrlShortener.Application.Analytics;

/// <summary>Raw request facts captured at redirect time. Never persisted as-is.</summary>
public sealed record ClickContext(string? Referrer, string? UserAgent, string? RemoteIp);

/// <summary>Turns raw request data into a privacy-preserving <see cref="ClickEvent"/> (decision A-6).</summary>
public sealed class ClickEventFactory
{
    private readonly byte[] _salt;

    public ClickEventFactory(IOptions<ShortenerOptions> options)
    {
        _salt = Encoding.UTF8.GetBytes(options.Value.VisitorHashSalt);
    }

    public ClickEvent Create(string code, ClickContext context, DateTimeOffset now) =>
        new(Guid.NewGuid(), code, now,
            ExtractHost(context.Referrer),
            UserAgentClassifier.Classify(context.UserAgent),
            HashVisitor(context.RemoteIp, context.UserAgent));

    /// <summary>Keeps only the referrer host: full referrer URLs can contain session tokens or PII.</summary>
    internal static string? ExtractHost(string? referrer) =>
        Uri.TryCreate(referrer, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri.IdnHost.ToLowerInvariant()
            : null;

    /// <summary>HMAC-SHA256(salt, ip|ua). Irreversible without the salt; rotating the salt resets uniqueness.</summary>
    private string? HashVisitor(string? ip, string? userAgent)
    {
        if (string.IsNullOrEmpty(ip))
        {
            return null;
        }

        var hash = HMACSHA256.HashData(_salt, Encoding.UTF8.GetBytes($"{ip}|{userAgent}"));
        return Convert.ToHexString(hash, 0, 16);
    }
}

/// <summary>Coarse user-agent family. Deliberately simple: full UA parsing is a dependency we don't need
/// for the questions the stats endpoint answers (decision A-5).</summary>
public static class UserAgentClassifier
{
    public static string Classify(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return "Unknown";
        }

        var ua = userAgent;
        if (Contains(ua, "bot") || Contains(ua, "crawler") || Contains(ua, "spider"))
        {
            return "Bot";
        }

        if (Contains(ua, "curl/") || Contains(ua, "wget/") || Contains(ua, "python-requests") || Contains(ua, "httpclient"))
        {
            return "CLI/Library";
        }

        // Order matters: Edge and Opera UAs also contain "Chrome"; Chrome UAs also contain "Safari".
        if (Contains(ua, "Edg/"))
        {
            return "Edge";
        }

        if (Contains(ua, "OPR/"))
        {
            return "Opera";
        }

        if (Contains(ua, "Firefox/"))
        {
            return "Firefox";
        }

        if (Contains(ua, "Chrome/") || Contains(ua, "CriOS/"))
        {
            return "Chrome";
        }

        return Contains(ua, "Safari/") ? "Safari" : "Other";
    }

    private static bool Contains(string source, string value) => source.Contains(value, StringComparison.OrdinalIgnoreCase);
}
