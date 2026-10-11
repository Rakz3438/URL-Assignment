using Microsoft.Extensions.Options;
using UrlShortener.Application;
using UrlShortener.Application.Analytics;

namespace UrlShortener.UnitTests.Application;

public class AnalyticsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private readonly ClickEventFactory _factory = new(Options.Create(new ShortenerOptions { VisitorHashSalt = "unit-test-salt-0123456789" }));

    [Theory]
    [InlineData("Mozilla/5.0 (Windows NT 10.0) AppleWebKit/537.36 Chrome/120.0 Safari/537.36 Edg/120.0", "Edge")]
    [InlineData("Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36", "Chrome")]
    [InlineData("Mozilla/5.0 (Macintosh) AppleWebKit/605.1.15 Version/17.0 Safari/605.1.15", "Safari")]
    [InlineData("Mozilla/5.0 (X11; Linux x86_64; rv:121.0) Gecko/20100101 Firefox/121.0", "Firefox")]
    [InlineData("Mozilla/5.0 Chrome/120.0 Safari/537.36 OPR/106.0", "Opera")]
    [InlineData("Mozilla/5.0 (compatible; Googlebot/2.1)", "Bot")]
    [InlineData("curl/8.4.0", "CLI/Library")]
    [InlineData("", "Unknown")]
    [InlineData(null, "Unknown")]
    [InlineData("SomethingElse/1.0", "Other")]
    public void Classify_KnownUserAgents_ReturnsFamily(string? userAgent, string expected) =>
        UserAgentClassifier.Classify(userAgent).Should().Be(expected);

    [Fact]
    public void Create_RawRequestData_StoresNoRawIpOrFullReferrer()
    {
        var click = _factory.Create("abc1234",
            new ClickContext("https://mail.example.com/inbox?session=SECRET", "curl/8.0", "203.0.113.7"), Now);

        click.ReferrerHost.Should().Be("mail.example.com");
        click.VisitorHash.Should().NotBeNull().And.NotContain("203.0.113.7");
        click.UserAgentFamily.Should().Be("CLI/Library");
    }

    [Fact]
    public void Create_SameVisitorTwice_SameHash_DifferentVisitor_DifferentHash()
    {
        var a1 = _factory.Create("abc1234", new ClickContext(null, "ua", "203.0.113.7"), Now);
        var a2 = _factory.Create("abc1234", new ClickContext(null, "ua", "203.0.113.7"), Now);
        var b = _factory.Create("abc1234", new ClickContext(null, "ua", "203.0.113.8"), Now);

        a1.VisitorHash.Should().Be(a2.VisitorHash);
        b.VisitorHash.Should().NotBe(a1.VisitorHash);
        a1.Id.Should().NotBe(a2.Id);
    }

    [Theory]
    [InlineData("android-app://com.example")]
    [InlineData("not a url")]
    [InlineData(null)]
    public void ExtractHost_NonHttpOrInvalidReferrer_ReturnsNull(string? referrer) =>
        ClickEventFactory.ExtractHost(referrer).Should().BeNull();
}
