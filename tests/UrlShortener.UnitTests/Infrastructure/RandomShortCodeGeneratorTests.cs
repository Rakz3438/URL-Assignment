using UrlShortener.Domain.Links;
using UrlShortener.Infrastructure.Codes;

namespace UrlShortener.UnitTests.Infrastructure;

public class RandomShortCodeGeneratorTests
{
    private readonly RandomShortCodeGenerator _generator = new();

    [Fact]
    public void Generate_Always_ReturnsSevenBase62Characters()
    {
        for (var i = 0; i < 1_000; i++)
        {
            var code = _generator.Generate();
            code.Should().HaveLength(ShortCode.GeneratedLength);
            code.Should().MatchRegex("^[0-9A-Za-z]{7}$");
        }
    }

    [Fact]
    public void Generate_ManyCodes_NoCollisionsAndUsesWholeAlphabet()
    {
        // 100k draws from 3.5e12: P(any collision) ≈ 0.0014. A failure here signals a broken RNG.
        var codes = Enumerable.Range(0, 100_000).Select(_ => _generator.Generate()).ToList();

        codes.Should().OnlyHaveUniqueItems();
        codes.SelectMany(c => c).Distinct().Should().HaveCount(62);
    }
}
