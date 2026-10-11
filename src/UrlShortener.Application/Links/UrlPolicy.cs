using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using UrlShortener.Application.Common;
using UrlShortener.Domain.Links;

namespace UrlShortener.Application.Links;

/// <summary>Decides which destination URLs may be shortened (decision A-8, story URL-4).
/// Blocks script schemes, embedded credentials, internal targets and redirect loops.</summary>
/// <remarks>Residual risk: hostnames are not DNS-resolved, so a public name pointing at a private IP
/// is not caught here. The redirect is performed by the client's browser, not this server, so this is an
/// abuse/phishing control rather than server-side SSRF; documented in the risk register.</remarks>
public sealed class UrlPolicy
{
    private readonly string _selfHost;

    public UrlPolicy(IOptions<ShortenerOptions> options)
    {
        _selfHost = new Uri(options.Value.PublicBaseUrl).IdnHost;
    }

    /// <summary>Validates and canonicalizes the URL. On success the value is the normalized absolute URL.</summary>
    public Result<string> Validate(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return Invalid("URL is required.");
        }

        if (url.Length > ShortLink.MaxTargetUrlLength)
        {
            return Invalid($"URL must be at most {ShortLink.MaxTargetUrlLength} characters.");
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return Invalid("URL must be an absolute URL.");
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return Invalid("Only http and https URLs are allowed.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return Invalid("URLs with embedded credentials are not allowed.");
        }

        var host = uri.IdnHost.TrimEnd('.');
        if (host.Length == 0)
        {
            return Invalid("URL must have a host.");
        }

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return Invalid("Local addresses are not allowed.");
        }

        if (host.Equals(_selfHost, StringComparison.OrdinalIgnoreCase))
        {
            return Invalid("URLs pointing to this service are not allowed.");
        }

        // Uri normalizes alternate IPv4 notations (decimal, hex, octal) into dotted form, so
        // "http://2130706433/" arrives here as 127.0.0.1 and is caught below.
        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6
            && IPAddress.TryParse(host.Trim('[', ']'), out var ip)
            && IsNonPublic(ip))
        {
            return Invalid("Private, loopback and link-local addresses are not allowed.");
        }

        // Store the ASCII (punycode) host: the value is later sent in a Location header, which must be ASCII.
        // AbsoluteUri alone keeps Unicode hosts (e.g. "bücher.example") and would break the redirect.
        var normalized = new UriBuilder(uri) { Host = uri.IdnHost }.Uri.AbsoluteUri;
        return normalized.Length > ShortLink.MaxTargetUrlLength
            ? Invalid($"URL must be at most {ShortLink.MaxTargetUrlLength} characters.")
            : normalized;
    }

    internal static bool IsNonPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal || ip.Equals(IPAddress.IPv6Any);
        }

        var b = ip.GetAddressBytes();
        return b[0] switch
        {
            0 => true,                                  // 0.0.0.0/8 "this network"
            10 => true,                                 // 10.0.0.0/8 private
            100 when b[1] is >= 64 and <= 127 => true,  // 100.64.0.0/10 carrier-grade NAT
            169 when b[1] == 254 => true,               // 169.254.0.0/16 link-local (cloud metadata endpoint)
            172 when b[1] is >= 16 and <= 31 => true,   // 172.16.0.0/12 private
            192 when b[1] == 168 => true,               // 192.168.0.0/16 private
            >= 224 => true,                             // multicast, reserved, broadcast
            _ => false,
        };
    }

    private static Error Invalid(string message) => Error.Validation("url.invalid", message);
}
