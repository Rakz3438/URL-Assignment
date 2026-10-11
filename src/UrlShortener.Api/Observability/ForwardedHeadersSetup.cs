using Microsoft.AspNetCore.HttpOverrides;
using IPNetwork = Microsoft.AspNetCore.HttpOverrides.IPNetwork;

namespace UrlShortener.Api.Observability;

/// <summary>Section "Network" (env: Network__TrustedProxies__0, Network__TrustedNetworks__0 = "10.0.0.0/8", ...).</summary>
public sealed class NetworkOptions
{
    public const string SectionName = "Network";

    /// <summary>Exact proxy IPs whose X-Forwarded-For / -Proto headers are trusted.</summary>
    public IReadOnlyList<string> TrustedProxies { get; set; } = [];

    /// <summary>CIDR ranges of trusted proxies, e.g. the Kubernetes pod network or the Docker bridge.</summary>
    public IReadOnlyList<string> TrustedNetworks { get; set; } = [];
}

/// <summary>Real client IP behind a reverse proxy (traceability note 2, URL-18). The client IP drives visitor hashing (A-6)
/// and per-IP rate limits (S1), so trusting X-Forwarded-For from anyone would let a client forge both.
/// Only explicitly configured proxies are trusted, and ASP.NET's default trust of loopback is removed.</summary>
internal static class ForwardedHeadersSetup
{
    public static IServiceCollection AddTrustedProxies(this IServiceCollection services, IConfiguration configuration)
    {
        var network = configuration.GetSection(NetworkOptions.SectionName).Get<NetworkOptions>() ?? new NetworkOptions();

        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.KnownProxies.Clear();
            o.KnownNetworks.Clear();

            // Security trap (caught by ObservabilityTests): ASP.NET Core treats EMPTY KnownProxies + KnownNetworks as
            // "trust every sender". With nothing configured, forwarding must be OFF, or any client could forge
            // X-Forwarded-For to dodge per-IP rate limits and skew visitor analytics.
            if (network.TrustedProxies.Count == 0 && network.TrustedNetworks.Count == 0)
            {
                o.ForwardedHeaders = ForwardedHeaders.None;
                return;
            }

            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.ForwardLimit = 1; // only the hop added by our own proxy

            foreach (var proxy in network.TrustedProxies)
            {
                o.KnownProxies.Add(System.Net.IPAddress.Parse(proxy));
            }

            foreach (var cidr in network.TrustedNetworks)
            {
                var parts = cidr.Split('/');
                o.KnownNetworks.Add(new IPNetwork(System.Net.IPAddress.Parse(parts[0]), int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)));
            }
        });

        return services;
    }
}
