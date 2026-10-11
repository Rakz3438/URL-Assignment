using System.Security.Cryptography;
using UrlShortener.Application.Abstractions;
using UrlShortener.Domain.Links;

namespace UrlShortener.Infrastructure.Codes;

/// <summary>Cryptographically random Base62 codes (decision A-1). <see cref="RandomNumberGenerator.GetString"/>
/// uses rejection sampling, so every character is uniformly distributed (no modulo bias).</summary>
internal sealed class RandomShortCodeGenerator : IShortCodeGenerator
{
    public string Generate() => RandomNumberGenerator.GetString(ShortCode.Base62Alphabet, ShortCode.GeneratedLength);
}
