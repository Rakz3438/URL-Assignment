using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace UrlShortener.Api.Auth;

/// <summary>Configured API keys (decision A-4). Supply via environment: Auth__ApiKeys__0, Auth__ApiKeys__1, ...
/// Multiple keys allow zero-downtime rotation.</summary>
public sealed class ApiKeyOptions
{
    public const string SectionName = "Auth";

    public IReadOnlyList<string> ApiKeys { get; set; } = [];
}

/// <summary>Authenticates requests carrying a valid <c>X-Api-Key</c> header. Keys are compared as SHA-256
/// digests with a fixed-time comparison, so response timing does not reveal how much of a key matched.</summary>
public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";

    private readonly byte[][] _keyHashes;

    public ApiKeyAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
        UrlEncoder encoder, IOptions<ApiKeyOptions> apiKeys)
        : base(options, logger, encoder)
    {
        _keyHashes = apiKeys.Value.ApiKeys
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => SHA256.HashData(Encoding.UTF8.GetBytes(k)))
            .ToArray();
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var values) || values.Count != 1 || string.IsNullOrEmpty(values[0]))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var presented = SHA256.HashData(Encoding.UTF8.GetBytes(values[0]!));
        var match = false;
        foreach (var hash in _keyHashes)
        {
            // No early exit: check every key so timing doesn't depend on which key matched.
            match |= CryptographicOperations.FixedTimeEquals(presented, hash);
        }

        if (!match)
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
        }

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "api-client")], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
