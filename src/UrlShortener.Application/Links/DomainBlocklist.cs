using System.Collections.Frozen;
using Microsoft.Extensions.Options;

namespace UrlShortener.Application.Links;

/// <summary>Section "Security" (env: Security__BlockedDomains__0, ...). Scenario 3, S2.</summary>
public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>Registrable domains whose links must not be created or followed. A domain also blocks all of
    /// its subdomains. Read through <see cref="IOptionsMonitor{TOptions}"/>, so file-based configuration
    /// changes apply without a restart.</summary>
    public IReadOnlyList<string> BlockedDomains { get; set; } = [];
}

/// <summary>Local, configurable destination blocklist (scope decision S2). Deliberately the seam where an external
/// reputation check (S2+, out of scope) would plug in later.</summary>
public sealed class DomainBlocklist
{
    private readonly IOptionsMonitor<SecurityOptions> _options;
    private FrozenSet<string> _domains = FrozenSet<string>.Empty;
    private IReadOnlyList<string>? _source;

    public DomainBlocklist(IOptionsMonitor<SecurityOptions> options) => _options = options;

    /// <summary>For tests and tooling.</summary>
    public static DomainBlocklist FromDomains(params string[] domains) =>
        new(new StaticMonitor(new SecurityOptions { BlockedDomains = domains }));

    /// <summary>True if <paramref name="host"/> or any parent domain is blocked. O(number of labels).</summary>
    public bool IsBlocked(string host)
    {
        var domains = Current();
        if (domains.Count == 0)
        {
            return false;
        }

        var candidate = Normalize(host);
        while (true)
        {
            if (domains.Contains(candidate))
            {
                return true;
            }

            var dot = candidate.IndexOf('.', StringComparison.Ordinal);
            if (dot < 0)
            {
                return false;
            }

            candidate = candidate[(dot + 1)..];
        }
    }

    public bool IsBlocked(Uri url) => IsBlocked(url.IdnHost);

    private FrozenSet<string> Current()
    {
        // Rebuild only when the configured list instance changes (config reload), not per call.
        var source = _options.CurrentValue.BlockedDomains;
        if (!ReferenceEquals(source, _source))
        {
            _domains = source.Where(d => !string.IsNullOrWhiteSpace(d)).Select(Normalize).ToFrozenSet(StringComparer.Ordinal);
            _source = source;
        }

        return _domains;
    }

    private static string Normalize(string domain)
    {
        var trimmed = domain.Trim().TrimEnd('.');
        // Accept Unicode entries in config; compare in punycode like UrlPolicy stores hosts.
        return Uri.TryCreate($"http://{trimmed}/", UriKind.Absolute, out var uri)
            ? uri.IdnHost.ToLowerInvariant()
            : trimmed.ToLowerInvariant();
    }

    private sealed class StaticMonitor(SecurityOptions value) : IOptionsMonitor<SecurityOptions>
    {
        public SecurityOptions CurrentValue => value;
        public SecurityOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<SecurityOptions, string?> listener) => null;
    }
}
