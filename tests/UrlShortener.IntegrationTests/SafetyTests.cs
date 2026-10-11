using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using UrlShortener.Api.Contracts;
using UrlShortener.IntegrationTests.Infrastructure;

namespace UrlShortener.IntegrationTests;

/// <summary>Scenario 3 safety scope: S1 rate limiting, S2 blocklist, S4 security headers.</summary>
public class SafetyTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public SafetyTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Create_OverPerKeyLimit_Returns429WithRetryAfterProblem()
    {
        var client = Limited(createPerMinute: 3, redirectPerMinute: 1000);

        var statuses = new List<HttpStatusCode>();
        HttpResponseMessage last = null!;
        for (var i = 0; i < 4; i++)
        {
            last = await client.CreateLinkAsync(new { url = $"https://example.com/limit/{i}" });
            statuses.Add(last.StatusCode);
        }

        statuses.Should().Equal(HttpStatusCode.Created, HttpStatusCode.Created, HttpStatusCode.Created, HttpStatusCode.TooManyRequests);
        last.Headers.RetryAfter!.Delta.Should().BePositive();
        last.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        (await last.Content.ReadAsStringAsync()).Should().Contain("rate_limited");
    }

    [Fact]
    public async Task Redirect_OverPerClientLimit_Returns429()
    {
        var client = Limited(createPerMinute: 1000, redirectPerMinute: 5);
        var created = await client.CreateLinkAsync(new { url = "https://example.com/redirect-limit" });
        var code = (await created.Content.ReadFromJsonAsync<LinkResponse>())!.Code;

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
        {
            statuses.Add((await client.GetAsync($"/{code}")).StatusCode);
        }

        statuses.Take(5).Should().OnlyContain(s => s == HttpStatusCode.Found);
        statuses[5].Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Create_BlockedDomain_Returns400Blocked()
    {
        var response = await _factory.CreateAuthorizedClient().CreateLinkAsync(new { url = "https://login.blocked.example/" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("url.blocked");
    }

    [Fact]
    public async Task Redirect_DomainBlockedAfterCreation_Returns410Blocked()
    {
        // Created while allowed, then the domain is blocklisted: existing links must stop working too.
        var created = await _factory.CreateAuthorizedClient().CreateLinkAsync(new { url = "https://later.example/x" });
        var code = (await created.Content.ReadFromJsonAsync<LinkResponse>())!.Code;
        var blocked = _factory.WithWebHostBuilder(b => b.UseSetting("Security:BlockedDomains:1", "later.example"))
            .CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await blocked.GetAsync($"/{code}");

        response.StatusCode.Should().Be(HttpStatusCode.Gone);
        (await response.Content.ReadAsStringAsync()).Should().Contain("link.blocked");
    }

    [Theory]
    [InlineData("/api/v1/links/nope1234")]
    [InlineData("/swagger/v1/swagger.json")]
    [InlineData("/nope1234")]
    public async Task AnyResponse_CarriesSecurityHeaders(string path)
    {
        var response = await _factory.CreateAnonymousClient().GetAsync(path);

        response.Headers.GetValues("X-Content-Type-Options").Should().ContainSingle("nosniff");
        response.Headers.GetValues("X-Frame-Options").Should().ContainSingle("DENY");
        response.Headers.GetValues("Referrer-Policy").Should().ContainSingle("no-referrer");
    }

    private HttpClient Limited(int createPerMinute, int redirectPerMinute)
    {
        var client = _factory.WithWebHostBuilder(b =>
            {
                b.UseSetting("RateLimiting:CreatePerMinute", createPerMinute.ToString(System.Globalization.CultureInfo.InvariantCulture));
                b.UseSetting("RateLimiting:RedirectPerMinute", redirectPerMinute.ToString(System.Globalization.CultureInfo.InvariantCulture));
            })
            .CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.ApiKey);
        return client;
    }
}
