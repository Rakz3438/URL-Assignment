using Microsoft.Extensions.Options;
using UrlShortener.Application;
using UrlShortener.Application.Links;

namespace UrlShortener.UnitTests.Application;

/// <summary>Security table for decision A-8. Each blocked case documents the attack it prevents.</summary>
public class UrlPolicyTests
{
    private readonly UrlPolicy _policy = new(Options.Create(new ShortenerOptions { PublicBaseUrl = "https://sho.rt" }));

    [Theory]
    [InlineData("https://example.com", "https://example.com/")]
    [InlineData("https://example.com/path?q=1#frag", "https://example.com/path?q=1#frag")]
    [InlineData("HTTP://EXAMPLE.COM/A", "http://example.com/A")]
    [InlineData("  https://example.com/  ", "https://example.com/")]
    [InlineData("http://8.8.8.8/dns", "http://8.8.8.8/dns")]
    [InlineData("http://172.32.0.1/", "http://172.32.0.1/")]          // just outside 172.16/12
    [InlineData("https://bücher.example/", "https://xn--bcher-kva.example/")] // IDN → punycode
    public void Validate_PublicHttpUrl_ReturnsNormalizedUrl(string input, string expected)
    {
        var result = _policy.Validate(input);

        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        result.Value.Should().Be(expected);
    }

    [Theory]
    [InlineData(null, "missing")]
    [InlineData("", "missing")]
    [InlineData("example.com", "relative / no scheme")]
    [InlineData("/relative/path", "relative")]
    [InlineData("javascript:alert(1)", "XSS via script scheme")]
    [InlineData("data:text/html,<script>alert(1)</script>", "XSS via data URL")]
    [InlineData("file:///etc/passwd", "local file scheme")]
    [InlineData("ftp://example.com/", "non-web scheme")]
    [InlineData("https://user:pass@example.com/", "credential phishing (user@host confusion)")]
    [InlineData("https://paypal.com@evil.example/", "credential phishing (user@host confusion)")]
    [InlineData("http://localhost/admin", "loopback by name")]
    [InlineData("http://LOCALHOST./", "loopback with trailing dot")]
    [InlineData("http://app.localhost/", "*.localhost resolves to loopback")]
    [InlineData("http://127.0.0.1/", "loopback IPv4")]
    [InlineData("http://2130706433/", "loopback as decimal integer")]
    [InlineData("http://0x7f000001/", "loopback as hex")]
    [InlineData("http://0/", "0.0.0.0")]
    [InlineData("http://10.0.0.5/", "private 10/8")]
    [InlineData("http://172.16.0.1/", "private 172.16/12")]
    [InlineData("http://172.31.255.255/", "private 172.16/12 upper bound")]
    [InlineData("http://192.168.1.1/", "private 192.168/16")]
    [InlineData("http://169.254.169.254/latest/meta-data/", "cloud metadata endpoint")]
    [InlineData("http://100.64.0.1/", "carrier-grade NAT")]
    [InlineData("http://[::1]/", "loopback IPv6")]
    [InlineData("http://[fe80::1]/", "link-local IPv6")]
    [InlineData("http://[fd00::1]/", "unique-local IPv6")]
    [InlineData("http://[::ffff:127.0.0.1]/", "IPv4-mapped loopback")]
    [InlineData("https://sho.rt/abc1234", "redirect loop to this service")]
    [InlineData("https://SHO.RT./x", "redirect loop, case + trailing dot")]
    public void Validate_UnsafeUrl_ReturnsValidationError(string? input, string because)
    {
        var result = _policy.Validate(input);

        result.IsSuccess.Should().BeFalse(because);
        result.Error!.Code.Should().Be("url.invalid");
    }

    [Fact]
    public void Validate_UrlLongerThan2048_ReturnsValidationError()
    {
        var url = "https://example.com/" + new string('a', 2048);

        _policy.Validate(url).IsSuccess.Should().BeFalse();
    }
}
